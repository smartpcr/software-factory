// -----------------------------------------------------------------------
// <copyright file="CrewStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Services.Stories.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Real-LLM <see cref="IStoryArtifactStrategy"/> -- invokes the same
/// <see cref="IAgentRunner"/> rail that <c>CrewQueueWorker.RunAgentAsync</c>
/// uses for live work-item execution. Per-phase agents (Architect / Engineer
/// / Review / QA) are resolved via the matching <c>crew-{role}</c> row and
/// its <c>Lead</c> agent; their crew row supplies model + backend +
/// invoke-mode; their <c>soul.md</c> is loaded as the system prompt.
///
/// <para>The agent runs against a per-invocation Forge-managed workspace
/// (<c>{stateDir}/storyworkspaces/{spawnIdSlug}/{phaseTag}/</c>) -- never
/// %TEMP%, never the repo root. The agent uses its filesystem tools to
/// write the expected files at relative paths inside that directory; the
/// strategy reads them back and returns them to the orchestrator, which
/// commits them via <see cref="IStoryGitOps"/>.</para>
/// </summary>
public sealed class CrewStoryArtifactStrategy : IStoryArtifactStrategy
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Operator rule shared by every agent prompt that can raise open questions: never ask
    /// without options. <see cref="QuestionOptions"/> enforces it server-side for agents
    /// that ignore this.
    /// </summary>
    internal const string QuestionOptionsRule = """
        - EVERY question MUST be `type: "choice"` with 2-5 concrete, mutually
          exclusive `choices`. Never emit `type: "open"` and never ask a
          question without options. The operator can ONLY pick one of your
          options (typed answers are not accepted), so the options must
          cover every realistic answer; never add an "Other" option.
        - For "who", "when" or "which value" questions, list the concrete
          candidates (e.g. "The operator runs it now", "Defer it to a
          follow-up PR", "Drop the requirement") instead of asking open-ended.
        """;

    /// <summary>
    /// Workstream (impl) prompts only: how an agent marks a choice that stops the work
    /// to wait for something outside it. Forge acts on it (<see cref="ParkedByAnswerException"/>),
    /// so an answer like "pause until the repair merges" really parks the workstream.
    /// </summary>
    internal const string QuestionParkRule = """
        - If a choice means this workstream must STOP and wait for something
          outside it (another PR or workstream merging, an external fix, a
          person acting), list that choice verbatim in the question's
          `parks` array, e.g. `"parks": ["Pause until the repair merges"]`.
          When the operator picks it, Forge parks the workstream: no further
          iterations until the operator clicks Retry. Never offer a choice
          that keeps iterating while waiting on something outside the work.
        """;

    /// <summary>
    /// Maximum number of files <see cref="ProduceStageE2eAsync"/> will
    /// accept from the QA crew's scratch workspace before failing fast.
    /// Legitimate qa-e2e producers emit a handful of artifacts
    /// (a `.feature` file, a few step-def classes, optionally a base
    /// class + appsettings template). When the scratch workspace gets
    /// polluted with the full repo (e.g. the agent shells out to
    /// <c>git clone</c> or copies the worktree into the workspace dir),
    /// <see cref="ReadAllFiles"/> can return hundreds of paths — every
    /// one of which becomes a path arg to a downstream
    /// <c>git check-ignore --</c> invocation that then blows the
    /// Windows <c>CreateProcess</c> argv limit. The cap is intentionally
    /// generous so legitimate edge cases stay clear; the 2026-05-28
    /// incident emitted 825 files, an order of magnitude above the cap.
    /// </summary>
    internal const int QaE2eProducerFileCountCap = 50;

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IAgentRunner runner;
    private readonly TaskChannel taskChannel;
    private readonly IConfiguration config;
    private readonly IForgeDbPaths dbPaths;
    private readonly IStoryGitOps git;
    private readonly AgentSlotService slots;
    private readonly CopilotSessionPool copilotPool;
    // Phase 59 Stage C — pluggable impacted-test resolver chain
    // (Roslyn → Heuristic). Nullable so existing test-doubles of
    // this strategy that don't supply the chain still construct;
    // when null the gate runs the legacy `ImpactedTestResolver.Resolve`
    // static facade directly (pure heuristic, no Roslyn).
    private readonly ImpactedTests.ImpactedTestResolverChain? impactedTestChain;
    private readonly ILogger<CrewStoryArtifactStrategy>? logger;

    /// <summary>
    /// docs/forge/e2e.md Stage 6.3 — per-spawn cache of the parsed
    /// <c>e2e-scenarios.md</c> phase map. An N-stage story whose
    /// stages spread across multiple phases would otherwise read +
    /// parse the file once per stage in <c>ProduceStageE2eAsync</c>;
    /// this cache reduces that to once per spawn. Cache key is the
    /// spawn id (stable across stages within a single spawn);
    /// value is the result of <see cref="E2eScenariosSetupReader.ReadAllPhases"/>
    /// applied to the merged file's content. <see cref="InvalidatePhaseSetupCache"/>
    /// purges a single entry when the spawn's e2e-scenarios.md changes
    /// (e.g. operator Resume on the artifact); orchestrator restart
    /// drops the whole map since the strategy is a singleton.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, E2eScenariosSetupReader.PhaseSetup>> phaseSetupCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// docs/forge/e2e.md Stage 6.3 — test hook: count of cache misses
    /// per spawn id. Exposed so the integration test can prove the
    /// cache short-circuits a second call for the same spawn without
    /// re-parsing. Best-effort observability; not used in production
    /// logic.
    /// </summary>
    internal int PhaseSetupCacheMissesFor(string spawnId) =>
        this.phaseSetupCacheMisses.TryGetValue(spawnId, out var n) ? n : 0;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> phaseSetupCacheMisses =
        new(StringComparer.Ordinal);

    /// <summary>Test hook — purges the phase-setup cache for a single spawn.</summary>
    internal void InvalidatePhaseSetupCache(string spawnId)
    {
        this.phaseSetupCache.TryRemove(spawnId, out _);
        this.phaseSetupCacheMisses.TryRemove(spawnId, out _);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — per-spawn idempotency marker for
    /// <c>E2eBase.cs</c>. The first <see cref="ProduceStageE2eAsync"/>
    /// call that wins <c>TryAdd</c> on this set folds the rendered
    /// base-class file into its output dict; subsequent qa-e2e
    /// workstreams for the same spawn skip the entry so the spawn ends
    /// up with exactly one <c>tests/Steps/{storyId}/E2eBase.cs</c>.
    /// On orchestrator restart this set clears (strategy is a
    /// singleton); the per-spawn re-emission is harmless because git
    /// commits are content-idempotent.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> e2eBaseEmittedForSpawn =
        new(StringComparer.Ordinal);

    /// <summary>Test hook — has this strategy instance already emitted
    /// the per-spawn E2eBase.cs for <paramref name="spawnId"/>?</summary>
    internal bool HasEmittedE2eBaseFor(string spawnId) =>
        this.e2eBaseEmittedForSpawn.ContainsKey(spawnId);

    /// <summary>Test hook — forget the per-spawn E2eBase marker so the
    /// next <see cref="ProduceStageE2eAsync"/> call for the spawn
    /// re-emits. Used by integration tests that span a strategy
    /// "restart" without spinning up a new instance.</summary>
    internal void ResetE2eBaseEmittedFor(string spawnId) =>
        this.e2eBaseEmittedForSpawn.TryRemove(spawnId, out _);

    public CrewStoryArtifactStrategy(
        IServiceScopeFactory scopeFactory,
        IAgentRunner runner,
        TaskChannel taskChannel,
        IConfiguration config,
        IForgeDbPaths dbPaths,
        IStoryGitOps git,
        AgentSlotService slots,
        CopilotSessionPool copilotPool,
        ImpactedTests.ImpactedTestResolverChain? impactedTestChain = null,
        ILogger<CrewStoryArtifactStrategy>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.taskChannel = taskChannel;
        this.config = config;
        this.dbPaths = dbPaths;
        this.git = git;
        this.slots = slots;
        this.copilotPool = copilotPool;
        this.impactedTestChain = impactedTestChain;
        this.logger = logger;
    }

    // ── Decomposition shape (shared by decomposer + reconciler) ──────────────

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — synthetic anchor suffix appended to
    /// a Stage's anchor to produce the qa-e2e companion's anchor
    /// (e.g. <c>phase-foo/stage-bar/e2e</c>). Single source of truth so
    /// the decomposer, the reconciler, and the qa-e2e routing layer
    /// (Stage 5.1) all compare against the same literal.
    /// </summary>
    public const string QaE2eAnchorSuffix = "/e2e";

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — anchor for the qa-e2e companion of
    /// the given impl-stage anchor. Pure function so unit tests and the
    /// reconciler share one definition.
    /// </summary>
    public static string QaE2eAnchorFor(string stageAnchor) =>
        stageAnchor + QaE2eAnchorSuffix;

    /// <summary>
    /// docs/forge/e2e.md Stage 3.1 — pure projection of a parsed plan +
    /// resolved deps into the canonical <see cref="DecompositionWorkstream"/>
    /// list the orchestrator persists. Emits one impl row per stage AND
    /// one trailing <see cref="WorkstreamCategories.QaE2e"/> row per
    /// stage that declares at least one <c>### Test Scenarios</c>
    /// checkbox.
    ///
    /// <para>The qa-e2e row's <c>Deps</c> contains its impl sibling's
    /// title (so the existing title-keyed dep wiring in
    /// <c>StorySpawnOrchestrator.PersistDecompositionAsync</c> routes the
    /// edge correctly) followed by every upstream title from the
    /// resolver's <c>CrossStageE2eDeps</c> projection — the cross-stage
    /// edges declared via the Stage 2.1 H3. Stage 3.2 will rewrite the
    /// branch / worktree-slug shape; for now the slug fall-out of
    /// <c>PlanAnchor.Replace('/', '-')</c> already gives qa-e2e rows
    /// distinct ids (<c>ws-{storyId}-{anchor}-e2e</c>).</para>
    ///
    /// <para>Static so unit tests can feed it a parsed plan + resolver
    /// output directly; the strategy's instance method
    /// <see cref="ProduceDecompositionAsync"/> just calls this after
    /// reading the plan file from git.</para>
    /// </summary>
    internal static IReadOnlyList<DecompositionWorkstream> BuildDecompositionFromPlan(
        PlanParser.CanonicalPlan canonical,
        IReadOnlyDictionary<string, DecompositionDepsResolver.ResolvedStageDeps> resolved)
    {
        var stages = canonical.AllStages().ToList();
        var output = new List<DecompositionWorkstream>(stages.Count * 2);

        foreach (var s in stages)
        {
            resolved.TryGetValue(s.Anchor, out var rd);
            var deps = rd?.Deps ?? Array.Empty<string>();
            var unresolved = rd?.UnresolvedDeps ?? Array.Empty<string>();
            // Phase 46 -- cache the phase-{slug} prefix so the swarm
            // DAG can group cards without re-parsing the plan.
            var slash = s.Anchor.IndexOf('/');
            var phaseAnchor = slash > 0 ? s.Anchor[..slash] : s.Anchor;

            output.Add(new DecompositionWorkstream(
                Title: s.Name,
                Description: s.Steps.Count == 0
                    ? $"Stage {s.PhaseNumber}.{s.StageNumber}"
                    : string.Join(" ", s.Steps.Take(3).Select(st => st.Text)),
                AcceptanceCriteria: (IReadOnlyList<string>)s.TestScenarios.ToList(),
                Deps: deps,
                Deliverables: (IReadOnlyList<string>)Array.Empty<string>(),
                Timeline: string.Empty,
                EstimatedEffort: string.Empty,
                PlanAnchor: s.Anchor,
                PlanContentHash: s.ContentHash,
                PhaseAnchor: phaseAnchor,
                UnresolvedDeps: unresolved.Count == 0 ? null : unresolved,
                Category: WorkstreamCategories.Impl));

            // docs/forge/e2e.md Stage 3.1 — trailing qa-e2e row when the
            // stage declares any test scenarios. Skipped silently for
            // stages whose `### Test Scenarios` block is empty (legacy
            // plans / utility stages with no observable behaviour).
            if (s.TestScenarios.Count == 0) continue;

            // Deps shape: [impl-sibling-title, ...cross-stage e2e upstream
            // titles]. PersistDecompositionAsync resolves Deps through a
            // title→id map (siblings are titles, not ids), so the impl
            // sibling's title is exactly what we need; the resolver's
            // CrossStageE2eDeps projection is already in the same title
            // form because ResolveAll projects anchors → titles via
            // stagesByAnchor.
            var qaDeps = new List<string>(1 + (rd?.CrossStageE2eDeps?.Count ?? 0)) { s.Name };
            if (rd is { CrossStageE2eDeps: { Count: > 0 } crossE2eDeps })
            {
                foreach (var t in crossE2eDeps)
                {
                    // Preserve plan-order (resolver alphabetises already);
                    // dedupe defensively in case a plan declares the impl
                    // sibling as both an ordinary AND a cross-stage dep.
                    if (!qaDeps.Contains(t, StringComparer.OrdinalIgnoreCase))
                    {
                        qaDeps.Add(t);
                    }
                }
            }

            output.Add(new DecompositionWorkstream(
                Title: $"{s.Name} — E2E",
                Description: $"E2E tests for {s.Name}: Gherkin feature + step definitions + e2e run "
                    + $"covering {s.TestScenarios.Count} acceptance "
                    + (s.TestScenarios.Count == 1 ? "scenario." : "scenarios."),
                AcceptanceCriteria: (IReadOnlyList<string>)s.TestScenarios.ToList(),
                Deps: qaDeps,
                Deliverables: (IReadOnlyList<string>)Array.Empty<string>(),
                Timeline: string.Empty,
                EstimatedEffort: string.Empty,
                PlanAnchor: QaE2eAnchorFor(s.Anchor),
                // Reuse the parent stage's content hash. Stage 2.1 already
                // folds TestScenarios + CrossStageE2eDeps into the stage's
                // hash, so any change to either re-prompts both the impl
                // and qa-e2e rows under this stage.
                PlanContentHash: s.ContentHash,
                PhaseAnchor: phaseAnchor,
                UnresolvedDeps: null,
                Category: WorkstreamCategories.QaE2e));
        }

        return output;
    }

    // ── Per-phase entry points ───────────────────────────────────────────────

    /// <summary>
    /// Single source of truth for the four plan-doc briefs the architect
    /// agent receives in its user prompt. Both <see cref="ProducePlanArtifactsAsync"/>
    /// (full plan run) and <see cref="ResumeSingleArtifactAsync"/> (operator
    /// re-runs one doc) MUST consume this so the brief stays consistent --
    /// the two were drifted in 2026-05-04: Resume was sending a one-liner
    /// brief while the full-run path had been expanded to detail the LIVEDOC
    /// structure, so a Resume click was producing a doc that didn't match
    /// the spec. Keying by the URL slug (no <c>.md</c> extension) matches
    /// the <c>POST /spawn/artifacts/{key}/resume</c> route.
    /// </summary>
    internal static Dictionary<string, (string FileName, string Brief, string RepoPath)> BuildPlanDocSpecs(string docDir)
    {
        return new Dictionary<string, (string FileName, string Brief, string RepoPath)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tech-spec"] = ("tech-spec.md",
                "Problem statement, in/out-of-scope items, non-goals, hard constraints, identified risks. Anchor every section in the story description's concrete details.",
                $"{docDir}/tech-spec.md"),

            ["architecture"] = ("architecture.md",
                """
                Components and their responsibilities, data model (entities + key fields),
                interfaces between components, end-to-end sequence flows for the primary scenarios.

                ALSO include a `## Test & Environment Contract` section that, for each primary
                acceptance area, pins the PROOF STRATEGY and its DEPENDENCY + how the gate provides
                it (the gate host has NO docker and NO pre-provisioned services):
                - `in-process` — pure unit/logic test, zero external I/O.
                - `golden` — committed snapshot for preservation/equality proofs ("byte-identical",
                  "unchanged after the refactor"); the only honest option when the pre-change code
                  no longer exists in the tree.
                - `service:<name>` — needs a live backing service; NAME it and state how the gate
                  provides it (embedded/ephemeral ONLY if the schema needs no non-bundled extension
                  an embedded build lacks; otherwise a provided DSN). Such a criterion can NEVER be `inline`.
                - `lab` — physical/hardware lab only.
                Downstream docs (implementation-plan, e2e-scenarios) MUST honor this contract.
                """,
                $"{docDir}/architecture.md"),

            ["implementation-plan"] = ("implementation-plan.md",
                """
                This file is a LIVEDOC -- it tracks implementation progress with checkboxes.
                Operators tick boxes as work lands; the doc is the source of truth for "what's done".

                Structure MUST follow this hierarchy EXACTLY (Phase 46 layout):

                ---
                title: "{story title}"
                storyId: "{WI-id}"
                ---

                # Phase 1: {phase name}

                ## Dependencies
                - _none -- start phase_

                ## Stage 1.1: {stage name}

                ### Implementation Steps
                - [ ] Step description (concrete, actionable -- one PR-sized unit of work)
                - [ ] Another step

                ### Dependencies
                - _none -- start stage_

                ### Test Scenarios
                - [ ] Scenario: {name} -- Given {context}, When {action}, Then {expected outcome} [proof: in-process|golden|service:<name>|lab; deps: none|<service + how the gate provides it>]
                - [ ] Scenario: {name} -- …

                ## Stage 1.2: {stage name}

                ### Implementation Steps
                - [ ] …

                ### Dependencies
                - phase-{phase-1-name-slug}/stage-{stage-1-1-name-slug}

                ### Test Scenarios
                - [ ] …

                # Phase 2: {phase name}

                ## Dependencies
                - phase-{phase-1-name-slug}

                ## Stage 2.1: …

                Hard requirements (the evaluator will refuse pass if any are violated):
                - Top-level YAML front-matter (`title:`, `storyId:`) MUST be present so the doc rendering treats phases as root sections.
                - Each Phase is an H1 (`# Phase N: …`); Stages are H2 (`## Stage N.M: …`); subsections are H3 (`### …`).
                - Each Phase MUST contain a `## Dependencies` section directly under the H1 heading, before any stage. An empty body MUST read `- _none -- start phase_`.
                - Each Stage MUST contain THREE subsections in this exact order: `### Implementation Steps`, `### Dependencies`, `### Test Scenarios`. A fourth OPTIONAL subsection `### Cross-Stage Dependencies` may appear AFTER `### Test Scenarios` when this stage's trailing qa-e2e workstream needs to wait on additional upstream stages (i.e. an e2e scenario that exercises this stage AND another stage).
                - Dep refs use anchor form, NOT titles. Stage anchor: `phase-{phase-name-slug}/stage-{stage-name-slug}` (no phase or stage numbers -- slugs are derived from the heading name only). Phase anchor: `phase-{phase-name-slug}`.
                - The `### Dependencies` body MUST list one bullet per upstream stage anchor (e.g. `- phase-foo/stage-bar`). An empty body MUST read `- _none -- start stage_`.
                - The optional `### Cross-Stage Dependencies` body lists one bullet per upstream stage or phase anchor (same anchor form as `### Dependencies`). Omit the H3 entirely when the qa-e2e workstream only depends on this stage's implementation. An unresolved anchor under this H3 is fatal (parse error `plan.dep_unresolved`); cycles introduced via cross-stage e2e deps fire `plan.dep_cycle` like any other dep cycle.
                - Phase deps may name a phase anchor (e.g. `- phase-foo`); the orchestrator expands phase deps as "every stage in this phase depends on every stage in the upstream phase".
                - Stage TITLES must be unique across the entire plan; a duplicate title is rejected as `plan.duplicate_stage_title`.
                - At most 20 Stages per Phase. At most 20 Implementation Steps per Stage. Each step changes ≤ 20 files in the eventual PR.
                - Every Implementation Step is a checkbox `- [ ]` (not bullets, not numbered).
                - Every Test Scenario is a checkbox `- [ ]` and is verifiable pass/fail (not subjective).
                - Include at least 2 test scenarios per Stage.
                - Each Implementation Step must be small enough for a single agent iteration to complete.
                - Every Test Scenario carries a `[proof: …; deps: …]` tag whose proof kind is one of
                  `in-process | golden | service:<name> | lab`, consistent with architecture.md's
                  `## Test & Environment Contract`. A scenario asserting PERSISTED or external state
                  (a live DB, a DSN env-var, "byte-identical"/"same rows" of persisted data) MUST use
                  `golden` or `service:<name>` — NEVER `in-process` — and name the service + how the
                  gate provides it.
                """,
                $"{docDir}/implementation-plan.md"),

            ["e2e-scenarios"] = ("e2e-scenarios.md",
                """
                Gherkin-style feature scenarios (Feature / Scenario / Given-When-Then)
                covering happy paths and edge cases QA will run, GROUPED BY PHASE.

                Each Phase H1 (`# Phase N: ...`) MUST be followed by:

                  ### Setup
                  - **Type**: inline | compose | lab-wsfc | lab-bare-metal
                  - **Local**: <how a developer runs this on their workstation>
                  - **CI runner**: <runner pool / labels / hosted image>
                  - **Secrets**: <none | KeyVault path | GH environment + secret names>
                  - **Pre-test bootstrap**: <command(s), or "none">
                  - **Gate provisioning**: for `inline`/`compose`, how the test
                    suite stands its dependencies up IN-PROCESS when the
                    connection env-var is unset (embedded/ephemeral service, no
                    docker) so required scenarios run in Forge's gate instead of
                    skipping; `compose` keeps the CI stack as the env-var-set path.

                  ### Scenarios
                  - **Scenario:** ...

                Setup type MUST be one of: `inline`, `compose`, `lab-wsfc`,
                `lab-bare-metal`. State the type explicitly as the first sub-bullet of
                `### Setup` (e.g. `- **Type**: lab-wsfc`).

                When a phase needs `docker-compose`, name the compose file path at
                `tests/e2e/{phase-slug}/docker-compose.yml` and list the services it
                starts.

                When a phase needs a lab environment (Windows Server Failover Cluster,
                CAU, hardware-specific NIC, etc.), state:
                  - the ADO agent pool name (default `forge-lab-wsfc` for clusters,
                    `forge-lab-hw` for hardware),
                  - the GitHub self-hosted runner labels,
                  - the Azure Key Vault name and variable group (ADO),
                  - the GitHub environment name and secret names.

                Hard requirements (evaluator refuses pass otherwise):
                - Every Phase H1 has a `### Setup` H3 directly before `### Scenarios`.
                - Setup `Type` is one of the closed set above.
                - When Type is `lab-*`, the Secrets bullet names BOTH a KeyVault entry
                  AND a GitHub environment (Forge supports both providers).
                - Connection strings are referenced as env-var names, never literal
                  hostnames or credentials.
                - For `inline`/`compose`, required scenarios MUST run without docker
                  or a pre-provisioned service (self-provision in-test); a skip for a
                  missing dependency is allowed only for `lab-*`.
                """,
                $"{docDir}/e2e-scenarios.md"),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
    {
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var docDir = StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId);
        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        var specs = BuildPlanDocSpecs(docDir).Values.ToArray();

        // Phase 42 -- set up the real git worktree for plan work. All four
        // architects share this worktree (git refuses two worktrees on one
        // branch), with per-iter commits serialised by the IStoryGitOps lock.
        // The agent's working dir is now the actual repo root, so its tools
        // can read source files / prior docs / etc. -- no more isolated
        // scratch dir + sibling-copy trick.
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
        // Phase 45 -- root the worktree at the spawn's chosen base branch
        // (defaults to main). Plan branch was branched off this base, so
        // worktree create here mirrors that ancestry.
        var worktreeBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreePath = await this.git.EnsureWorktreeAsync(
            repo, planBranch, worktreeBase, ct,
            worktreeSlug: PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug());

        // Phase 43 -- copy operator-uploaded attachments into the worktree
        // so architect agents can read them with their file tools. Refresh
        // from DB so attachments uploaded after the orchestrator's outer
        // tick loaded `spawn` show up on the next plan drive.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        var attachmentPaths = await CopyAttachmentsToWorktreeAsync(
            attachmentsJson, worktreePath, ct, stateDir: this.dbPaths.Folder);

        // ── Assign different generator + evaluator agents per doc (Phase 47) ──
        // Load both rosters so each of the 4 plan docs runs with a
        // distinct (generator, evaluator) pair instead of sharing the
        // crew's DefaultEvaluatorAgentId. The four invocations run via
        // Task.WhenAll below; with shared evaluators they'd serialise
        // behind one agent (operator-reported on failover-cluster:XRAFT
        // 2026-05-09).
        var (generatorAgentIds, evaluatorAgentIds) =
            await this.LoadArchitectAndEvaluatorRostersAsync(specs.Length, ct);

        var tasks = specs.Select((spec, idx) =>
            this.ProducePlanFileAsync(
                spawn, story, spec, budget, forceResume,
                generatorAgentOverride: generatorAgentIds.Count > idx ? generatorAgentIds[idx] : null,
                evaluatorAgentOverride: evaluatorAgentIds.Count > idx ? evaluatorAgentIds[idx] : null,
                siblingSpecs: specs.Where((_, i) => i != idx).Select(s => s.RepoPath).ToArray(),
                worktreePath: worktreePath,
                repo: repo,
                planBranch: planBranch,
                attachmentsJson: attachmentsJson,
                ct: ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < specs.Length; i++)
        {
            dict[specs[i].RepoPath] = results[i];
        }
        return dict;
    }

    /// <summary>
    /// Phase 53 -- produce ONE plan doc on the caller-supplied branch + worktree.
    /// The orchestrator's sequential drive owns branch / worktree / commit /
    /// PR lifecycle; this method only runs the gen↔eval loop and returns
    /// the resulting file content (already written to disk in the worktree
    /// by the architect agent).
    ///
    /// <para>Sibling docs surfaced in the agent prompt are the three other
    /// canonical keys. On a sequential-mode spawn they live on three
    /// different worktrees, but the prompt only needs their repo paths
    /// (the agent reads them via filesystem tools at runtime -- the
    /// previously-merged docs are already on this worktree's base, so the
    /// agent's `read` against e.g. `docs/stories/{id}/architecture.md`
    /// resolves directly).</para>
    /// </summary>
    public async Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
        StorySpawnRow spawn,
        WorkItemRow story,
        string docKey,
        string worktreePath,
        string planBranch,
        RepoRow? repo,
        bool forceResume = false,
        CancellationToken ct = default)
    {
        if (!PlanDocStateHelpers.PlanDocFileName.ContainsKey(docKey))
        {
            throw new ArgumentException($"Unknown plan-doc key '{docKey}'.", nameof(docKey));
        }

        var docDir = StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId);
        var specs = BuildPlanDocSpecs(docDir);
        if (!specs.TryGetValue(docKey, out var spec))
        {
            throw new InvalidOperationException($"BuildPlanDocSpecs missing entry for '{docKey}'.");
        }

        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        // Phase 47 / 53 -- pick the doc's canonical roster slot even though
        // sequential mode invokes one doc per scheduler tick. Loading the full
        // four-doc roster preserves the Phase-47 pair distribution contract;
        // requesting count=1 here re-pinned every sequential doc to slot 0.
        var (generatorAgentId, evaluatorAgentId) =
            await this.LoadPlanDocAgentPairAsync(docKey, ct);

        // Siblings are the three non-active canonical keys, in canonical
        // order. The agent's filesystem tools resolve them against the
        // worktree (previously-merged docs are on base; the current
        // worktree base is `spawn.BaseBranch`, which already contains
        // any upstream merged doc).
        var siblingSpecs = PlanDocStateHelpers.PlanDocOrder
            .Where(k => !string.Equals(k, docKey, StringComparison.OrdinalIgnoreCase))
            .Select(k => specs[k].RepoPath)
            .ToArray();

        // Phase 43 -- refresh attachments snapshot and materialise into the
        // pre-existing per-doc worktree (orchestrator set it up at
        // StorySpawnOrchestrator.cs:497 before calling us). Sequential plan
        // mode runs one ProduceSinglePlanArtifactAsync per orchestrator tick,
        // so this is also where operator-uploaded attachments first reach
        // the architect agent for sequential spawns.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(
            attachmentsJson, worktreePath, ct, stateDir: this.dbPaths.Folder);

        var content = await this.ProducePlanFileAsync(
            spawn, story, spec, budget, forceResume,
            generatorAgentOverride: generatorAgentId,
            evaluatorAgentOverride: evaluatorAgentId,
            siblingSpecs: siblingSpecs,
            worktreePath: worktreePath,
            repo: repo,
            planBranch: planBranch,
            attachmentsJson: attachmentsJson,
            ct: ct);

        return (spec.RepoPath, content);
    }

    /// <summary>
    /// Phase 42 -- resolve the <see cref="RepoRow"/> for a spawn so we can
    /// drive the real-repo worktree. Returns null when the spawn isn't
    /// bound to a repo (legacy / synthetic cases -- the strategy then
    /// falls back to the deterministic-test no-op path that doesn't touch
    /// git). Logged at warning when null + ProducePlan is called so the
    /// operator sees why the worktree tab stays empty.
    /// </summary>
    private async Task<RepoRow?> LookupRepoAsync(string? repoId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repoId)) return null;
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct);
    }

    /// <summary>
    /// Resolve the on-disk state directory (where forge.db lives and
    /// where storyworkspaces / attachments are rooted). Phase A: routes
    /// through <see cref="IForgeDbPaths.Folder"/>.
    /// </summary>
    private string ResolveStateDir() => this.dbPaths.Folder;

    /// <summary>
    /// Phase 50 -- render operator-pinned decisions as a prompt block that
    /// the generator must treat as binding. Pins are stored on the spawn
    /// as <c>StorySpawnRow.DecisionPinsJson</c>; this helper resolves the
    /// spawn id from the work item id (plan-doc: prefix → spawn id;
    /// engineer workstream → walk StorySpawnId), loads the JSON, filters
    /// by scope (pins with <c>workItemId == null</c> apply globally; pins
    /// scoped to a specific id only fire for that id), and returns a
    /// rendered markdown block.
    ///
    /// <para>Returns empty string when no pins apply -- caller checks for
    /// non-empty before inserting the block + separator.</para>
    /// </summary>
    /// <para>Phase 82.2 — promoted to <c>internal static</c> + parameterised
    /// on <see cref="IServiceScopeFactory"/> so <see cref="CrewQueueWorker"/>
    /// (which doesn't inherit from this strategy) can reuse the exact same
    /// rendering for impl-path workstreams. Without it, audit goal #4
    /// (global rules / pins) is honoured only on plan-doc + qa-e2e prompts;
    /// impl-path prompts silently drop the operator's pinned decisions.</para>
    /// </summary>
    /// <summary>
    /// The open question of <paramref name="workItemId"/> whose answer parks the work and
    /// has not been applied yet (<see cref="QuestionOptions.FindParkInForce"/>), stamped as
    /// applied so a later Retry resumes the work; null when none.
    /// </summary>
    internal static async Task<DeliverableQuestionRow?> ApplyParkAnswerAsync(IServiceScopeFactory scopeFactory, string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return null;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var rows = await db.DeliverableQuestions.Where(q => q.WorkItemId == workItemId).ToListAsync(ct);
        var park = QuestionOptions.FindParkInForce(rows);
        if (park is null) return null;
        park.ExplanationJson = QuestionOptions.WithParkApplied(park.ExplanationJson, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return park;
    }

    /// <summary>
    /// The operator's binding directives for one iteration of
    /// <paramref name="workItemId"/>: the story spawn's decision pins, then
    /// (for a workstream) the rules its tags attach from the product rule
    /// catalog (Phase 108, <see cref="Forge.Api.Services.Rules.RuleCatalogResolver"/>).
    /// Read fresh on every iteration, so a rule or tag change reaches each
    /// workstream on its next iteration. A catalog failure is logged and the
    /// prompt goes on without catalog rules; it never fails the iteration.
    /// </summary>
    internal static async Task<string> BuildDecisionPinsBlockAsync(IServiceScopeFactory scopeFactory, string workItemId, CancellationToken ct, ILogger? logger = null)
    {
        var pins = await BuildSpawnPinsBlockAsync(scopeFactory, workItemId, ct);
        var rules = string.Empty;
        if (!string.IsNullOrEmpty(workItemId) && !workItemId.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var resolved = await Forge.Api.Services.Rules.RuleCatalogResolver.ResolveAsync(db, workItemId, ct);
                rules = Forge.Api.Services.Rules.RuleCatalogResolver.RenderPromptBlock(resolved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "Rule catalog could not be resolved for {WorkItemId}; this iteration's prompt has no catalog rules.",
                    workItemId);
            }
        }

        if (pins.Length == 0) return rules;
        return rules.Length == 0 ? pins : pins + Environment.NewLine + rules;
    }

    private static async Task<string> BuildSpawnPinsBlockAsync(IServiceScopeFactory scopeFactory, string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return string.Empty;

        using var scope = scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Resolve spawn id: plan-doc:{spawnId}:{file} → spawnId; else
        // workstream row's StorySpawnId; else workItemId itself (when
        // the caller passes a spawn id directly, e.g. decompose/qa).
        string? spawnId = null;
        if (workItemId.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            var rest = workItemId.Substring("plan-doc:".Length);
            var lastColon = rest.LastIndexOf(':');
            spawnId = lastColon > 0 ? rest.Substring(0, lastColon) : rest;
        }
        else
        {
            var ws = await db.Workstreams.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            spawnId = ws?.StorySpawnId ?? workItemId;
        }
        if (string.IsNullOrEmpty(spawnId)) return string.Empty;

        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (spawn is null || string.IsNullOrEmpty(spawn.DecisionPinsJson)) return string.Empty;

        List<System.Text.Json.JsonElement>? pins = null;
        try
        {
            pins = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(
                spawn.DecisionPinsJson);
        }
        catch { return string.Empty; }
        if (pins is null || pins.Count == 0) return string.Empty;

        var applicable = new List<System.Text.Json.JsonElement>();
        foreach (var pin in pins)
        {
            var scopedTo = pin.TryGetProperty("workItemId", out var wid)
                && wid.ValueKind == System.Text.Json.JsonValueKind.String
                ? wid.GetString()
                : null;
            if (string.IsNullOrEmpty(scopedTo)
                || string.Equals(scopedTo, workItemId, StringComparison.Ordinal))
            {
                applicable.Add(pin);
            }
        }
        if (applicable.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  OPERATOR DECISION PINS -- BINDING, OVERRIDES EVERYTHING ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("The operator has explicitly arbitrated the following decisions");
        sb.AppendLine("after observing convergence loops in prior iterations. You MUST");
        sb.AppendLine("treat each pin as a non-negotiable directive that overrides any");
        sb.AppendLine("sibling-doc text, evaluator critique, or prior chain context.");
        sb.AppendLine("If a pin contradicts what a sibling doc says, follow the pin");
        sb.AppendLine("(the sibling doc is wrong and will be corrected separately).");
        sb.AppendLine();
        for (var i = 0; i < applicable.Count; i++)
        {
            var pin = applicable[i];
            var text = pin.TryGetProperty("text", out var t)
                && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(text)) continue;
            sb.AppendLine($"{i + 1}. {text}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 92 — build the human-operator steering block for a workstream.
    /// Returns every ACTIVE (non-dismissed) <c>operator_prompts</c> row for the
    /// workstream, formatted as a binding directive block, in chronological
    /// order. Injected into BOTH the generator and the evaluator prompt on
    /// every iteration of <see cref="RunGenEvalLoopAsync"/> (refreshed per iter
    /// so a freshly-added prompt takes effect on the next turn, and a dismissed
    /// one drops out immediately). Returns empty string when there are none.
    /// </summary>
    internal static async Task<string> BuildOperatorSteeringBlockAsync(IServiceScopeFactory scopeFactory, string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // SQLite/EF cannot translate a DateTimeOffset comparison in Where, so
        // filter the (cheap) WorkstreamId server-side and drop dismissed rows
        // in memory. A workstream accrues only a handful of operator prompts.
        var rows = await db.OperatorPrompts.AsNoTracking()
            .Where(p => p.WorkstreamId == workItemId)
            .ToListAsync(ct);
        var active = rows
            .Where(p => p.DismissedAt is null && !string.IsNullOrWhiteSpace(p.Body))
            .OrderBy(p => p.CreatedAt)
            .ToList();
        if (active.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  HUMAN OPERATOR STEERING -- BINDING, MUST FOLLOW         ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("A human operator is supervising this workstream and has entered");
        sb.AppendLine("the following steering / follow-up directives. BOTH the generator");
        sb.AppendLine("and the evaluator MUST respect them. Treat each as a non-negotiable");
        sb.AppendLine("instruction that overrides sibling-doc text and prior chain context;");
        sb.AppendLine("if you are the evaluator, score against these directives being");
        sb.AppendLine("honoured. If a directive conflicts with the brief, follow the");
        sb.AppendLine("operator directive.");
        sb.AppendLine();
        for (var i = 0; i < active.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {active[i].Body.Trim()}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 49 follow-up -- resolve the build command the build gates
    /// (per-iter and post-pass) should run for this workstream. The
    /// gates were originally hard-coded to <c>dotnet build</c> at the
    /// call site (<see cref="StoryBuildRunner.RunBuildAsync"/> defaults
    /// to it when <c>buildCommand</c> is null), so a Rust workspace
    /// (Cargo.toml at root) would exit with code 1 in ~200 ms with no
    /// parsed errors and route the workstream to <c>blocked</c> even
    /// though the agent's diff was scored at pass.
    ///
    /// <para>Resolution order:
    /// <list type="number">
    ///   <item>Operator override on the repo row (<see cref="RepoRow.BuildCommand"/>) wins.</item>
    ///   <item>Else <see cref="BuildSystemDetector.Detect"/> on the worktree
    ///         (which is structurally identical to the repo's CloneRoot).
    ///         Ambiguous detections (multiple .sln files, unsupported
    ///         python build) collapse to null so we don't run an
    ///         arbitrary build that may false-pass or false-fail.</item>
    ///   <item>Returns null when no build system can be confidently
    ///         identified -- caller skips the gate with telemetry rather
    ///         than running <c>dotnet build</c> against an unknown
    ///         workspace.</item>
    /// </list></para>
    /// </summary>
    private static string? ResolveBuildCommand(RepoRow? repo, string workspace)
    {
        if (repo is not null && !string.IsNullOrWhiteSpace(repo.BuildCommand))
        {
            return repo.BuildCommand;
        }
        var detected = BuildSystemDetector.Detect(workspace);
        if (detected.Ambiguous)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(detected.BuildCommand) ? null : detected.BuildCommand;
    }

    /// <summary>
    /// 2026-05-22 — resolve the repo's init command (e.g. <c>init.cmd</c>),
    /// run FIRST in each gate stage's single shell session so the PATH /
    /// CDP_* env it sets is visible to restore + build + test (see
    /// <see cref="StoryBuildRunner.RunBuildAsync"/>'s session block).
    /// Repo-override only — there is no ecosystem default for init, so a
    /// repo that doesn't configure one keeps today's bare-build behaviour.
    /// </summary>
    private static string? ResolveInitCommand(RepoRow? repo) =>
        string.IsNullOrWhiteSpace(repo?.InitCommand) ? null : repo!.InitCommand;

    /// <summary>
    /// 2026-05-22 — resolve the repo's restore command (e.g. <c>restore</c>,
    /// which on the <c>update</c> repo is a <c>.dev</c> script reachable
    /// only after init.cmd puts <c>.dev</c> on PATH). Run AFTER init and
    /// BEFORE build/test in the same session. Repo-override only.
    /// </summary>
    private static string? ResolveRestoreCommand(RepoRow? repo) =>
        string.IsNullOrWhiteSpace(repo?.RestoreCommand) ? null : repo!.RestoreCommand;

    /// <summary>
    /// 2026-05-22 — compose the ordered shell-session prologue
    /// (init → restore) the gate prepends to each build/test command so
    /// they run in one cmd.exe session. Empty when the repo configures
    /// neither, which makes <see cref="StoryBuildRunner.RunBuildAsync"/>
    /// keep its direct-exec path.
    /// </summary>
    private static IReadOnlyList<string> BuildSessionSetup(string? initCommand, string? restoreCommand)
    {
        var list = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(initCommand)) list.Add(initCommand!);
        if (!string.IsNullOrWhiteSpace(restoreCommand)) list.Add(restoreCommand!);
        return list;
    }

    /// <summary>
    /// Phase 49 follow-up -- resolve the quality-gate commands (run AFTER
    /// the build succeeds at the post-pass trip point) for this
    /// workstream. Mirrors the upstream-CI shape: a Cargo workspace's
    /// <c>ci.yml</c> runs <c>cargo fmt --check</c> and
    /// <c>cargo clippy -D warnings</c> as required jobs, so a PR that
    /// passes Forge's local <c>cargo build</c> gate but fails fmt/clippy
    /// upstream will leave <c>mergeable_state=unstable</c> and the
    /// orchestrator's merge-retry loop livelocks (observed 2026-05-11
    /// on PR smartpcr/failover-cluster#6 sitting un-merged 3+ hours).
    /// Running the same gates locally surfaces the failure on the
    /// engineer's side BEFORE push so the next iteration self-corrects.
    /// <para>
    /// Resolution: detector-only for now. There is no per-repo override
    /// column yet -- if an operator needs to disable a noisy quality gate
    /// for a specific repo, add a <c>QualityCommandsJson</c> column with
    /// the same null=auto / empty=disabled / populated=override
    /// semantics used elsewhere. Returns an empty list when the workspace
    /// has no detected build system, when the detection is ambiguous, or
    /// when the ecosystem has no universal quality opinions (dotnet /
    /// node / go / maven / python vary too much per-repo to ship
    /// defaults that won't false-fail).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ResolveQualityCommands(RepoRow? repo, string workspace)
    {
        _ = repo;
        var detected = BuildSystemDetector.Detect(workspace);
        if (detected.Ambiguous)
        {
            return Array.Empty<string>();
        }
        return detected.QualityCommands;
    }

    /// <summary>
    /// Phase 49 follow-up -- resolve the test command for the post-pass
    /// test gate. Same precedence as <see cref="ResolveBuildCommand"/>:
    /// operator override on the repo row wins; else
    /// <see cref="BuildSystemDetector.Detect"/> on the worktree picks one
    /// of the ecosystem defaults (<c>cargo test</c>,
    /// <c>dotnet test {sln}</c>, <c>npm test</c>, <c>go test ./...</c>,
    /// <c>mvn test</c>, <c>pytest</c>). Returns null when nothing is
    /// detectable or detection is ambiguous, which the caller surfaces
    /// as <c>task.test-gate.skipped</c> rather than failing closed.
    /// <para>The companion to build + quality gates: after a diff
    /// compiles cleanly and the upstream-CI-mirroring quality gates
    /// (fmt / clippy) pass, this gate ensures the test suite still
    /// passes before Forge opens a PR. Without it the evaluator can
    /// pass a refactor that compiles but silently broke a unit test --
    /// the operator only finds out when CI fails on the PR
    /// (observed pre-fix on failover-cluster:XRAFT where the upstream
    /// build was already broken; with this gate the engineer agent
    /// can't even merge if the new code regresses an existing test).</para>
    /// </summary>
    private static string? ResolveTestCommand(RepoRow? repo, string workspace)
    {
        if (repo is not null && !string.IsNullOrWhiteSpace(repo.TestCommand))
        {
            return repo.TestCommand;
        }
        var detected = BuildSystemDetector.Detect(workspace);
        if (detected.Ambiguous)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(detected.TestCommand) ? null : detected.TestCommand;
    }

    /// <summary>
    /// Phase 56 — run the unified gate chain (build → quality → test)
    /// once and return the first failing stage, or null when every
    /// resolved command exited zero (or was skipped because the
    /// resolver returned null/empty for that stage).
    /// <para>The chain SHORT-CIRCUITS on the first failure: if build
    /// fails, quality + test are NOT run (no point linting code that
    /// doesn't compile). This matches today's post-pass semantics
    /// (lines 2211-2440 pre-refactor).</para>
    /// <para>Each stage emits a discrete telemetry event so the Build
    /// tab in <c>BuildPane.tsx</c> can render a per-stage timeline.
    /// Event shape preserves today's keys (<c>task.build-gate.*</c> /
    /// <c>task.test-gate.*</c>) plus a new <c>gateStage</c> discriminator
    /// (<c>"build"|"quality"|"test"</c>) so the UI can group ticks.</para>
    /// <para>Runner exceptions are caught per stage and emitted as
    /// <c>skipped</c> events rather than failing the chain — matches
    /// today's defensive shape (lines 3772-3786 pre-refactor) so a
    /// transient toolchain glitch doesn't burn the consecutive-failure
    /// streak.</para>
    /// </summary>
    /// <param name="stagePrefix">Either <c>"per-iter"</c> or
    /// <c>"post-pass"</c>; flows into telemetry so the UI can
    /// distinguish per-iter ticks from the pass-validator's single
    /// tick.</param>
    private async Task<GateFailure?> RunGateChainAsync(
        string workItemId,
        string workspace,
        int iter,
        string stagePrefix,
        string? buildCommand,
        IReadOnlyList<string>? qualityCommands,
        string? testCommand,
        string? agentId,
        string? crewId,
        CancellationToken ct,
        // Phase 59 Stage A — when set, the test gate runs the impacted-
        // test resolver before invoking the test command, narrowing
        // `dotnet test` to the test projects whose code (or whose
        // ProjectReference graph) was touched by changes since
        // testBaseRef. When testImpactedOnly is false OR testBaseRef is
        // null/empty, the gate runs the original testCommand verbatim
        // (the legacy whole-suite behaviour).
        string? testBaseRef = null,
        bool testImpactedOnly = false,
        // 2026-05-21 — per-repo gate wall-clock budgets. buildTimeout
        // governs the build + quality stages (repo.BuildTimeoutSec),
        // testTimeout governs the test stage (repo.TestTimeoutSec).
        // Null on either falls back to StoryBuildRunner.DefaultTimeout
        // (8 min). Before this the gate hardcoded DefaultTimeout for
        // every stage and silently ignored the operator's Edit-Repo
        // timeout fields, so a long integration suite was killed at 8
        // min regardless of the configured TestTimeoutSec.
        TimeSpan? buildTimeout = null,
        TimeSpan? testTimeout = null,
        // 2026-05-22 — repo setup commands (init.cmd, restore) that must
        // run in the SAME shell session as each stage's build/test command
        // (RepoRow.InitCommand / RestoreCommand). Null/empty = direct exec.
        string? initCommand = null,
        string? restoreCommand = null,
        // 2026-05-26 — when true (repo.RunTestsElevated), the TEST stage is
        // marshalled to the elevated broker (Forge.Api runs non-elevated).
        // Build / quality stages always run locally non-elevated. If the
        // broker is unreachable the test stage throws
        // ElevatedBrokerUnavailableException → the workstream stalls.
        bool runTestsElevated = false)
    {
        // init → restore prologue, prepended to each gate stage's command
        // so they share one cmd.exe session (empty = direct exec).
        var sessionSetup = BuildSessionSetup(initCommand, restoreCommand);

        // ----- BUILD stage -----
        if (string.IsNullOrWhiteSpace(buildCommand))
        {
            await this.taskChannel.EmitAsync(workItemId, "task.build-gate.skipped", new
            {
                stage = stagePrefix,
                gateStage = "build",
                iter,
                reason = "no build command resolved (set repo.BuildCommand to enable)",
                workspace,
            }, agentId: agentId, crewId: crewId, ct: ct);
        }
        else
        {
            // Phase 59 Stage B — pre-compute the log file path so
            // StoryBuildRunner can stream stdout+stderr to disk while
            // the process runs. logUrl flows into the SignalR event
            // payload so TranscriptLine.tsx can render the 📎 chip.
            var (buildLogPath, buildLogRel) = GateLogPaths.For(workspace, "build", iter);
            var buildLogUrl = BuildLogUrl(workItemId, buildLogRel);
            StoryBuildResult? build = null;
            try
            {
                if (GoWorkspaceGate.IsGoCommand(buildCommand)
                    && GoWorkspaceGate.TryReadWorkspaceModules(workspace) is { Count: > 0 } goBuildModules)
                {
                    // go.work multi-module repo: build runs in EVERY module
                    // (a clean compile is module-independent, so no fail-open
                    // risk — unlike the test stage, which scopes to impacted
                    // modules). `go build ./...` from the root would otherwise
                    // compile only the root module. See GoWorkspaceGate.
                    build = await StoryBuildRunner.RunGoWorkspaceAsync(
                        workspace, goBuildModules,
                        GoWorkspaceGate.ScopeCommandToModule(buildCommand!), ct,
                        timeout: buildTimeout ?? StoryBuildRunner.DefaultTimeout,
                        logFilePath: buildLogPath,
                        logRelPath: buildLogRel,
                        sessionSetupCommands: sessionSetup);
                }
                else
                {
                    build = await StoryBuildRunner.RunBuildAsync(
                        workspace, ct, buildTimeout ?? StoryBuildRunner.DefaultTimeout,
                        buildCommand: buildCommand,
                        logFilePath: buildLogPath,
                        logRelPath: buildLogRel,
                        sessionSetupCommands: sessionSetup);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "{Prefix} build check threw for {Workspace}; skipping this iter's build gate.",
                    stagePrefix, workspace);
                await this.taskChannel.EmitAsync(workItemId, "task.build-gate.skipped", new
                {
                    stage = stagePrefix,
                    gateStage = "build",
                    iter,
                    reason = $"build runner threw: {ex.GetType().Name}: {ex.Message}",
                    buildCommand,
                    workspace,
                }, agentId: agentId, crewId: crewId, ct: ct);
            }

            if (build is not null)
            {
                if (!build.Success)
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.build-gate.failed", new
                    {
                        stage = stagePrefix,
                        gateStage = "build",
                        iter,
                        exitCode = build.ExitCode,
                        errorCount = build.ErrorCount,
                        warningCount = build.WarningCount,
                        durationSec = build.Duration.TotalSeconds,
                        timedOut = build.TimedOut,
                        errorSummary = build.ErrorSummary,
                        buildCommand,
                        workspace,
                        logUrl = buildLogUrl,
                        logRelPath = build.LogRelPath,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    return new GateFailure("build", buildCommand, build);
                }

                await this.taskChannel.EmitAsync(workItemId, "task.build-gate.passed", new
                {
                    stage = stagePrefix,
                    gateStage = "build",
                    iter,
                    durationSec = build.Duration.TotalSeconds,
                    warningCount = build.WarningCount,
                    buildCommand,
                    workspace,
                    logUrl = buildLogUrl,
                    logRelPath = build.LogRelPath,
                }, agentId: agentId, crewId: crewId, ct: ct);
                // Phase 59.9 — prune older build logs to retention cap.
                // Best-effort; never blocks the gate.
                try { GateLogPaths.PruneToRetention(workspace); } catch { /* swallow */ }
            }
        }

        // ----- QUALITY stage(s) -----
        if (qualityCommands is { Count: > 0 })
        {
            for (var i = 0; i < qualityCommands.Count; i++)
            {
                var qc = qualityCommands[i];
                if (string.IsNullOrWhiteSpace(qc)) continue;

                var (qualityLogPath, qualityLogRel) = GateLogPaths.For(workspace, $"quality-{i}", iter);
                var qualityLogUrl = BuildLogUrl(workItemId, qualityLogRel);
                StoryBuildResult? q = null;
                try
                {
                    q = await StoryBuildRunner.RunBuildAsync(
                        workspace, ct, buildTimeout ?? StoryBuildRunner.DefaultTimeout,
                        buildCommand: qc,
                        logFilePath: qualityLogPath,
                        logRelPath: qualityLogRel,
                        sessionSetupCommands: sessionSetup);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "{Prefix} quality check `{Qc}` threw for {Workspace}; skipping.",
                        stagePrefix, qc, workspace);
                    await this.taskChannel.EmitAsync(workItemId, "task.build-gate.skipped", new
                    {
                        stage = stagePrefix,
                        gateStage = "quality",
                        iter,
                        qualityCommand = qc,
                        qualityCommandIndex = i,
                        reason = $"quality runner threw: {ex.GetType().Name}: {ex.Message}",
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    continue;
                }

                if (q is null) continue;

                if (!q.Success)
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.build-gate.failed", new
                    {
                        stage = stagePrefix,
                        gateStage = "quality",
                        iter,
                        qualityCommand = qc,
                        qualityCommandIndex = i,
                        exitCode = q.ExitCode,
                        errorCount = q.ErrorCount,
                        warningCount = q.WarningCount,
                        durationSec = q.Duration.TotalSeconds,
                        timedOut = q.TimedOut,
                        errorSummary = q.ErrorSummary,
                        workspace,
                        logUrl = qualityLogUrl,
                        logRelPath = q.LogRelPath,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    return new GateFailure("quality", qc, q);
                }

                await this.taskChannel.EmitAsync(workItemId, "task.build-gate.passed", new
                {
                    stage = stagePrefix,
                    gateStage = "quality",
                    iter,
                    qualityCommand = qc,
                    qualityCommandIndex = i,
                    durationSec = q.Duration.TotalSeconds,
                    warningCount = q.WarningCount,
                    workspace,
                    logUrl = qualityLogUrl,
                    logRelPath = q.LogRelPath,
                }, agentId: agentId, crewId: crewId, ct: ct);
            }
        }

        // ----- TEST stage -----
        if (string.IsNullOrWhiteSpace(testCommand))
        {
            await this.taskChannel.EmitAsync(workItemId, "task.test-gate.skipped", new
            {
                stage = stagePrefix,
                gateStage = "test",
                iter,
                reason = "no test command resolved (set repo.TestCommand to enable)",
                workspace,
            }, agentId: agentId, crewId: crewId, ct: ct);
        }
        else
        {
            // Docs-only short-circuit (2026-06-24): a change that touches
            // ONLY documentation / non-compilable asset files can't affect
            // any test outcome, so skip the (often very slow) test suite.
            // Runs before, and independently of, the impacted-test
            // resolver below, because that resolver is bypassed entirely
            // when a repo sets TestImpactedOnly=false (e.g. the `update`
            // repo), which made the docs-only stage-1.1 audit run the full
            // net472 integration suite on every per-iter gate, post-pass,
            // and flake-retry (local:UPDATE-NET10-MIGRATI, 2026-06-24). It
            // needs a base ref to diff; when absent (or on a git hiccup) it
            // falls through to the normal gate (never skips on uncertainty).
            if (!string.IsNullOrWhiteSpace(testBaseRef))
            {
                IReadOnlyList<string> docOnlyCheckFiles;
                try
                {
                    docOnlyCheckFiles = await ImpactedTestResolver.DiscoverChangedFilesAsync(
                        workspace, testBaseRef!, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    docOnlyCheckFiles = Array.Empty<string>();
                }

                if (docOnlyCheckFiles.Count > 0
                    && ImpactedTestResolver.IsDocumentationOnlyChange(docOnlyCheckFiles))
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.skipped", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        reason = "documentation-only change (no compilable source touched)",
                        changedFileCount = docOnlyCheckFiles.Count,
                        impactedMode = "docs-only",
                        testCommand,
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    this.logger?.LogInformation(
                        "{Prefix} test gate skipped: documentation-only change ({Count} file(s)) (iter {Iter}).",
                        stagePrefix, docOnlyCheckFiles.Count, iter);
                    return null;
                }
            }

            // Phase 59 Stage A — when impacted-test selection is enabled
            // and we have a base ref to diff against, narrow `dotnet
            // test` to the test projects whose code (or transitive
            // ProjectReference graph) was touched. Resolver is pure: a
            // no-op when the command isn't `dotnet test`, when it
            // already names an explicit project / --filter (operator
            // override wins), or when no test projects exist in the
            // workspace. Skipped (empty impacted set) short-circuits
            // with a task.test-gate.skipped event so the wall-clock
            // stays flat on doc-only Stages.
            //
            // Stage C — the resolver is now an
            // ImpactedTestResolverChain (Roslyn → Heuristic). The
            // chain's Mode field tells operators which provider
            // answered (stamped on the gate-event payload as
            // `impactedMode`). When the strategy was constructed
            // without a chain (legacy test-double path), fall back
            // to the static heuristic facade so behaviour matches
            // pre-Stage-C.
            var effectiveTestCommand = testCommand;
            IReadOnlyList<string> impactedProjects = Array.Empty<string>();
            var wholeSuiteFallback = false;
            var impactedRewritten = false;
            var allTestProjectCount = 0;
            var impactedMode = "heuristic";
            // A multi-module (go.work) `go test` command is scoped by the
            // go.work branch at the run site below — skip the .NET-shaped
            // resolver chain for it (the chain can't narrow a cross-module
            // `go test ./...` and may report "no impacted projects" → wrongly
            // skip the whole gate). Single-module Go still flows through the
            // resolver so its gopls (LSP) narrowing is preserved.
            var isGoWorkspaceCommand = GoWorkspaceGate.IsGoCommand(testCommand)
                && GoWorkspaceGate.TryReadWorkspaceModules(workspace) is not null;
            if (testImpactedOnly && !string.IsNullOrWhiteSpace(testBaseRef)
                && !isGoWorkspaceCommand)
            {
                // Time the impacted-test resolution and bracket it with telemetry.
                // The resolver chain bounds each provider with a wall-clock budget
                // (ImpactedTestResolverChain.PerProviderTimeout) and falls through to
                // the heuristic on timeout; the resolving→resolved event pair makes
                // that observable in the live-exec trace and feeds the per-iter
                // "resolve" sparkline in iteration insights. (Root cause 2026-05-26:
                // a hung csharp-ls textDocument/references with no deadline wedged a
                // workstream for hours with zero events between build-gate-pass and
                // the test stage — this instrumentation surfaces that window.)
                var resolveSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var changedFiles = await ImpactedTestResolver.DiscoverChangedFilesAsync(
                        workspace, testBaseRef!, ct);
                    // 2026-05-26 — drop analyzer-only Directory.Build.props/.targets
                    // changes so a build-time-only analyzer-wiring change (e.g. a
                    // BannedApiAnalyzer + BannedSymbols.txt) doesn't trip the
                    // global-impact whole-suite trigger. The build gate already
                    // validates the analyzer; forcing the entire (flaky) suite just
                    // stranded common-infra stage-1.3. Real source files in the diff
                    // still drive narrowing. Conservative — keeps the file on any doubt.
                    changedFiles = await ImpactedTestResolver.FilterCompilationNeutralGlobalFilesAsync(
                        workspace, testBaseRef!, changedFiles, ct);

                    // 2026-05-26 — pull the repo's STANDING `--filter` (a category
                    // exclude like `TestCategory!~CTQ&TestCategory!~Env`) out of the
                    // command BEFORE resolution, then AND-merge it back AFTER. Every
                    // provider (Roslyn / LSP / heuristic) treats a bare `--filter` as
                    // an operator pin and refuses to narrow — that's why csharp-ls
                    // (LSP) used to be tried first for repos with a standing filter,
                    // and why that flaky server wedged stage-1.3. Resolving the
                    // filter-free command lets the native Roslyn provider narrow
                    // these repos directly; re-merging preserves the exclude.
                    // Mirrors the global Forge:Test:ExcludeFilter handling below.
                    var (resolveBaseCommand, standingFilter) = SplitStandingFilter(testCommand);

                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.resolving", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        changedFileCount = changedFiles.Count,
                        providerTimeoutSec = this.impactedTestChain?.PerProviderTimeout.TotalSeconds,
                        standingFilter,
                        testBaseRef,
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);

                    ImpactedTestResolverResult resolution;
                    if (this.impactedTestChain is not null)
                    {
                        resolution = await this.impactedTestChain.ResolveAsync(
                            workspace, changedFiles, resolveBaseCommand, ct);
                    }
                    else
                    {
                        resolution = ImpactedTestResolver.Resolve(
                            workspace, changedFiles, resolveBaseCommand, impactedOnly: true);
                    }
                    resolveSw.Stop();

                    // gap #1 (2026-05-27) — multi-project narrowing. The resolver
                    // matched >1 impacted test project under a Traversal `.proj`
                    // anchor; `dotnet test` takes one target, so synthesize a SCOPED
                    // Traversal proj referencing only the impacted projects and run
                    // THAT instead of the whole-suite build.proj. Fails safe: any
                    // generation problem flips back to the resolver's whole-suite
                    // command, so this never makes the gate worse than before.
                    if (resolution.RequiresScopedTraversal
                        && resolution.ImpactedProjects.Count > 0
                        && !string.IsNullOrEmpty(resolution.TraversalProjPath))
                    {
                        var scopedRel = await this.TryWriteScopedTraversalProjAsync(
                            workspace, resolution.TraversalProjPath!, resolution.ImpactedProjects, ct);
                        if (!string.IsNullOrEmpty(scopedRel))
                        {
                            resolution = resolution with
                            {
                                Command = ReplaceCommandToken(resolution.Command, resolution.TraversalProjPath!, scopedRel!),
                                Rewritten = true,
                            };
                            await this.taskChannel.EmitAsync(workItemId, "task.test-gate.scoped-traversal", new
                            {
                                stage = stagePrefix,
                                gateStage = "test",
                                iter,
                                scopedProj = scopedRel,
                                impactedProjectCount = resolution.ImpactedProjects.Count,
                                allTestProjectCount = resolution.AllTestProjectCount,
                            }, agentId: agentId, crewId: crewId, ct: ct);
                        }
                        else
                        {
                            // Couldn't synthesize the scoped proj — fall back to the
                            // whole suite (the resolver's Command is the original).
                            resolution = resolution with { WholeSuiteFallback = true };
                        }
                    }

                    // Re-merge the standing category filter onto whatever the resolver
                    // produced (narrowed command OR whole-suite fallback). ApplyTest-
                    // ExcludeFilter AND-merges with any --filter the resolver added, so
                    // a narrowed run becomes `(FQN~…)&(TestCategory!~…)`.
                    effectiveTestCommand = ApplyTestExcludeFilter(resolution.Command, standingFilter);
                    impactedProjects = resolution.ImpactedProjects;
                    wholeSuiteFallback = resolution.WholeSuiteFallback;
                    impactedRewritten = resolution.Rewritten;
                    allTestProjectCount = resolution.AllTestProjectCount;
                    impactedMode = resolution.Mode;

                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.resolved", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        impactedMode = resolution.Mode,
                        durationMs = resolveSw.ElapsedMilliseconds,
                        impactedProjectCount = resolution.ImpactedProjects.Count,
                        allTestProjectCount = resolution.AllTestProjectCount,
                        rewritten = resolution.Rewritten,
                        wholeSuiteFallback = resolution.WholeSuiteFallback,
                        skipped = resolution.Skipped,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    this.logger?.LogInformation(
                        "{Prefix} impacted-test resolution finished in {Ms}ms: mode={Mode} impacted={Impacted}/{All} rewritten={Rewritten} skipped={Skipped} (iter {Iter}).",
                        stagePrefix, resolveSw.ElapsedMilliseconds, resolution.Mode,
                        resolution.ImpactedProjects.Count, resolution.AllTestProjectCount,
                        resolution.Rewritten, resolution.Skipped, iter);

                    if (resolution.Skipped)
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.test-gate.skipped", new
                        {
                            stage = stagePrefix,
                            gateStage = "test",
                            iter,
                            reason = "no impacted test projects",
                            testCommand,
                            workspace,
                            impactedOnly = true,
                            allTestProjectCount,
                            impactedMode,
                        }, agentId: agentId, crewId: crewId, ct: ct);
                        return null;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Resolver should never throw under normal conditions (it
                    // swallows git failures + IO errors and the chain falls through
                    // on a provider timeout), but defend against the surprise so a
                    // bug here doesn't take down the gate chain. Emit a failure
                    // event so the fallback is visible in the trace + insights.
                    resolveSw.Stop();
                    this.logger?.LogWarning(ex,
                        "{Prefix} impacted-test resolver threw after {Ms}ms for {Workspace}; falling back to whole-suite command.",
                        stagePrefix, resolveSw.ElapsedMilliseconds, workspace);
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.resolve-failed", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        durationMs = resolveSw.ElapsedMilliseconds,
                        error = $"{ex.GetType().Name}: {ex.Message}",
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    effectiveTestCommand = testCommand;
                    impactedProjects = Array.Empty<string>();
                    wholeSuiteFallback = false;
                    impactedRewritten = false;
                    allTestProjectCount = 0;
                    impactedMode = "heuristic";
                }
            }

            // 2026-05-21 — exclude known chronic-flaky tests from the
            // gate via the Forge:Test:ExcludeFilter config key (a vstest
            // --filter sub-expression, e.g.
            // `FullyQualifiedName!~CloudVsrDiscovery&FullyQualifiedName!~PostUpdateOperation`).
            // Applied AFTER impacted resolution so it composes with the
            // resolver's project / method narrowing (the Roslyn provider
            // can already emit a `--filter`, so we AND-merge rather than
            // clobber). No-op when the key is unset or the command isn't
            // `dotnet test`. Lets a suite with a chronic 5-minute flake
            // (which otherwise burns the whole gate budget → exit -1)
            // run to a real pass/fail instead of a timeout kill.
            var excludeFilter = this.config.GetValue<string?>("Forge:Test:ExcludeFilter");
            if (!string.IsNullOrWhiteSpace(excludeFilter))
            {
                effectiveTestCommand = ApplyTestExcludeFilter(effectiveTestCommand, excludeFilter!);
            }

            // Phase 59 Stage B — per-attempt log path for the test
            // gate. Shared between RunBuildAsync (writer) and the
            // task.test-gate.{passed,failed} payload (reader's URL).
            var (testLogPath, testLogRel) = GateLogPaths.For(workspace, "test", iter);
            var testLogUrl = BuildLogUrl(workItemId, testLogRel);
            // 2026-05-25 — stable scope tag for the per-iter TRX copy dir
            // so a re-run of the same iter doesn't collide with the prior
            // attempt's captured TRX.
            var testTrxTag = $"test-{iter}-{DateTime.UtcNow.Ticks}";

            // 2026-06-02 — hoist the go.work module resolution OUT of the test
            // run so it executes exactly once (it does a git diff + emits
            // task.test-gate.resolved), then run the test through a per-attempt
            // local function so a single flaky failure can be retried. Root
            // cause (code-intelligence PR-INGESTER stage 5.3, stuck ~1 day): a
            // module-wide `go test` flaked intermittently under heavy concurrent
            // load (postgres advisory locks / ports / CPU); each independent
            // flake counted toward BuildFailThreshold=3 → permanent stall, and a
            // failed test gate injects "correctness=0 minimum" into the
            // evaluator prompt → the SAME commit oscillated 88→35→88. One
            // bounded retry distinguishes a real regression (fails twice) from a
            // flake (passes on retry) without masking genuine breakage.
            var isGoWorkspaceRun = false;
            IReadOnlyList<string> goModulesResolved = Array.Empty<string>();
            var scopedGoCmd = effectiveTestCommand;
            var perModuleGoCmds = new List<string>();
            var goResolutionFailed = false;
            if (!runTestsElevated
                && GoWorkspaceGate.IsGoCommand(effectiveTestCommand)
                && GoWorkspaceGate.TryReadWorkspaceModules(workspace) is { Count: > 0 } goTestModules)
            {
                // go.work multi-module repo: `go test ./...` from the worktree
                // root matches ZERO packages (./... doesn't cross module
                // boundaries), so run it inside each module the diff touched.
                // Guarded: a git/IO failure here must mirror the original
                // generic-catch behaviour (emit skipped, skip the test stage)
                // rather than throwing into the orchestrator's generic stall.
                // OperationCanceledException still propagates (real shutdown).
                try
                {
                    IReadOnlyList<string> goChanged = Array.Empty<string>();
                    if (!string.IsNullOrWhiteSpace(testBaseRef))
                    {
                        try { goChanged = await ImpactedTestResolver.DiscoverChangedFilesAsync(workspace, testBaseRef!, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { goChanged = Array.Empty<string>(); }
                    }
                    var goModules = GoWorkspaceGate.SelectImpactedModules(goTestModules, goChanged);
                    scopedGoCmd = GoWorkspaceGate.ScopeCommandToModule(effectiveTestCommand!);
                    // Narrow the TEST stage one level below the module: run only
                    // the packages the diff actually touched, per module. This
                    // keeps an impacted module's slow/flaky unrelated packages
                    // (e.g. agent-memory's ~19 embedded-postgres integration
                    // packages) out of the per-iter gate, where their failures
                    // would be blamed on a diff that never touched them.
                    perModuleGoCmds = goModules
                        .Select(m => GoWorkspaceGate.ScopeTestCommandToPackages(effectiveTestCommand!, m, goTestModules, goChanged))
                        .ToList();
                    goModulesResolved = goModules;
                    effectiveTestCommand = scopedGoCmd;
                    impactedMode = "go-workspace";
                    isGoWorkspaceRun = true;
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.resolved", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        impactedMode,
                        goModules,
                        changedFileCount = goChanged.Count,
                        scopedCommand = scopedGoCmd,
                        scopedByModule = goModules
                            .Select((m, idx) => new { module = m, command = perModuleGoCmds[idx] })
                            .ToList(),
                    }, agentId: agentId, crewId: crewId, ct: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "{Prefix} go.work test resolution threw for {Workspace}; skipping.",
                        stagePrefix, workspace);
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.skipped", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        reason = $"go.work resolution threw: {ex.GetType().Name}: {ex.Message}",
                        testCommand,
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    goResolutionFailed = true;
                }
            }

            // Runs one test attempt against the supplied (per-attempt) log path
            // and TRX tag. Returns null when the run threw a non-fatal error
            // (skipped). The elevated-broker-unavailable case rethrows as
            // BuildFailedException (a RunTestsElevated repo must never silently
            // run its tests non-elevated) and is therefore never retried.
            async Task<StoryBuildResult?> ExecTestAttemptAsync(string logPath, string logRel, string trxTag)
            {
                try
                {
                    if (runTestsElevated)
                    {
                        // RunTestsElevated repo: the test command (its tests write
                        // HKLM / bind HttpListener) must run as admin, but Forge.Api
                        // itself is non-elevated. Marshal JUST this command to the
                        // elevated broker.
                        return await Forge.Api.Agents.ElevatedCommandBroker.RunBuildAsync(
                            new Forge.Api.Agents.ElevatedBuildRequest(
                                Workspace: workspace,
                                TimeoutMs: (long)(testTimeout ?? StoryBuildRunner.DefaultTimeout).TotalMilliseconds,
                                BuildCommand: effectiveTestCommand,
                                LogFilePath: logPath,
                                LogRelPath: logRel,
                                SessionSetupCommands: sessionSetup,
                                CollectTestFailures: true,
                                TrxScopeTag: trxTag),
                            ct);
                    }

                    if (isGoWorkspaceRun)
                    {
                        return await StoryBuildRunner.RunGoWorkspaceAsync(
                            workspace, goModulesResolved, scopedGoCmd!, ct,
                            timeout: testTimeout ?? StoryBuildRunner.DefaultTimeout,
                            logFilePath: logPath,
                            logRelPath: logRel,
                            sessionSetupCommands: sessionSetup,
                            collectTestFailures: true,
                            trxScopeTag: trxTag,
                            perModuleCommands: perModuleGoCmds);
                    }

                    return await StoryBuildRunner.RunBuildAsync(
                        workspace, ct, testTimeout ?? StoryBuildRunner.DefaultTimeout,
                        buildCommand: effectiveTestCommand,
                        logFilePath: logPath,
                        logRelPath: logRel,
                        sessionSetupCommands: sessionSetup,
                        // Test gate ONLY: scan for *.trx files written during
                        // the run, copy them under .forge/logs/trx/, and
                        // prepend a "FAILED TESTS" block to ErrorSummary so
                        // the next-iter generator prompt names the failing
                        // tests by FQTN. See CrewStoryArtifactStrategy ~line
                        // 5611 ("TEST FAILURE FROM PRIOR ITERATION") for
                        // where this lands in the prompt.
                        collectTestFailures: true,
                        trxScopeTag: trxTag);
                }
                catch (Forge.Api.Agents.ElevatedBrokerUnavailableException ex)
                {
                    // Operator's chosen fallback: STALL the workstream. Convert to
                    // the BuildFailedException the orchestrator already routes to a
                    // stall (DriveImplementAsync → ws.Status=stalled). Do NOT
                    // downgrade to a skipped/iterate gate — a RunTestsElevated repo
                    // must never run its tests non-elevated.
                    await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.aborted", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        reason = "elevated-test-broker-unavailable",
                    }, agentId: agentId, crewId: crewId, ct: ct);

                    throw new BuildFailedException(
                        "Elevated test runner unavailable — this repo is flagged RunTestsElevated, but the "
                        + "elevated test broker could not be reached. Start it via scripts\\run-forge-ui.ps1 "
                        + $"(it launches the broker elevated). {ex.Message}")
                    {
                        Stage = "test",
                        Command = effectiveTestCommand ?? testCommand ?? string.Empty,
                        ErrorSummary = ex.Message,
                        ExitCode = -1,
                        LastIteration = iter,
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "{Prefix} test check threw for {Workspace}; skipping.",
                        stagePrefix, workspace);
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.skipped", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        reason = $"test runner threw: {ex.GetType().Name}: {ex.Message}",
                        testCommand,
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    return null;
                }
            }

            StoryBuildResult? t = null;
            if (!goResolutionFailed)
            {
                t = await ExecTestAttemptAsync(testLogPath, testLogRel, testTrxTag);

                // Single flake-retry: only when attempt 1 produced a concrete
                // NON-timeout failure AND it failed FAST. A timeout is not
                // retried (the budget is already generous; a second full run
                // risks doubling a genuinely slow suite's wall-clock). A failure
                // that already burned more than half the test budget is also not
                // retried, so a slow deterministic failure (likely a real break)
                // can't double per-iter wall-clock and create NEW budget-pressure
                // stalls across workstreams. A null result (runner threw →
                // skipped) is not retried either. Attempt 2 writes to a FRESH log
                // path + TRX tag so attempt 1's evidence is preserved.
                var retryBudgetCeiling = TimeSpan.FromTicks(
                    (testTimeout ?? StoryBuildRunner.DefaultTimeout).Ticks / 2);
                if (t is { Success: false, TimedOut: false } && t.Duration < retryBudgetCeiling)
                {
                    var (retryLogPath, retryLogRel) = GateLogPaths.For(workspace, "test-retry", iter);
                    var retryLogUrl = BuildLogUrl(workItemId, retryLogRel);
                    var retryTrxTag = $"test-retry-{iter}-{DateTime.UtcNow.Ticks}";
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.flake-retry", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        reason = "attempt-1 failed; running one retry to distinguish flake from regression",
                        attempt1ExitCode = t.ExitCode,
                        attempt1DurationSec = t.Duration.TotalSeconds,
                        attempt1ErrorSummary = t.ErrorSummary,
                        attempt1LogUrl = testLogUrl,
                        attempt1LogRelPath = t.LogRelPath,
                        attempt1TrxRelPaths = t.TrxRelPaths,
                        effectiveTestCommand,
                        impactedMode,
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);

                    var t2 = await ExecTestAttemptAsync(retryLogPath, retryLogRel, retryTrxTag);
                    if (t2 is not null)
                    {
                        if (t2.Success)
                        {
                            // Recovered: attempt 2 passed where attempt 1 failed —
                            // a flake. Surface BOTH logs so operators can confirm
                            // the failure was non-deterministic (and find the
                            // flaky package). The gate proceeds as a pass.
                            await this.taskChannel.EmitAsync(workItemId, "task.test-gate.flake-recovered", new
                            {
                                stage = stagePrefix,
                                gateStage = "test",
                                iter,
                                attempt1LogUrl = testLogUrl,
                                attempt1ErrorSummary = t.ErrorSummary,
                                attempt2LogUrl = retryLogUrl,
                                attempt2DurationSec = t2.Duration.TotalSeconds,
                                effectiveTestCommand,
                                impactedMode,
                                workspace,
                            }, agentId: agentId, crewId: crewId, ct: ct);
                        }

                        // Attempt 2 is authoritative (pass OR a confirmed
                        // second failure). Point the pass/fail emission below at
                        // attempt 2's log + result. When attempt 2 itself threw
                        // (t2 == null), keep attempt 1's failure so the gate
                        // still fails rather than masking a real break.
                        testLogPath = retryLogPath;
                        testLogRel = retryLogRel;
                        testLogUrl = retryLogUrl;
                        t = t2;
                    }
                }
            }

            if (t is not null)
            {
                if (!t.Success)
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.test-gate.failed", new
                    {
                        stage = stagePrefix,
                        gateStage = "test",
                        iter,
                        exitCode = t.ExitCode,
                        errorCount = t.ErrorCount,
                        warningCount = t.WarningCount,
                        durationSec = t.Duration.TotalSeconds,
                        timedOut = t.TimedOut,
                        errorSummary = t.ErrorSummary,
                        testCommand,
                        effectiveTestCommand,
                        impactedRewritten,
                        impactedProjects,
                        wholeSuiteFallback,
                        allTestProjectCount,
                        impactedMode,
                        workspace,
                        logUrl = testLogUrl,
                        logRelPath = t.LogRelPath,
                        trxRelPaths = t.TrxRelPaths,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    return new GateFailure("test", effectiveTestCommand, t);
                }

                await this.taskChannel.EmitAsync(workItemId, "task.test-gate.passed", new
                {
                    stage = stagePrefix,
                    gateStage = "test",
                    iter,
                    durationSec = t.Duration.TotalSeconds,
                    warningCount = t.WarningCount,
                    testCommand,
                    effectiveTestCommand,
                    impactedRewritten,
                    impactedProjects,
                    wholeSuiteFallback,
                    allTestProjectCount,
                    impactedMode,
                    workspace,
                    logUrl = testLogUrl,
                    logRelPath = t.LogRelPath,
                    trxRelPaths = t.TrxRelPaths,
                }, agentId: agentId, crewId: crewId, ct: ct);
                // Phase 59.9 — prune older test logs after a green
                // gate. Quality and build prune in their own
                // branches; doing it here keeps the .forge/logs
                // directory bounded per workstream lifecycle.
                try { GateLogPaths.PruneToRetention(workspace); } catch { /* swallow */ }
            }
        }

        return null;
    }

    /// <summary>
    /// Phase 92 — persist the latest build / test gate outcome onto the durable
    /// <see cref="WorkstreamRow.LastBuildGate"/> / <see cref="WorkstreamRow.LastTestGate"/>
    /// columns so the swarm phase-board BUILD / TEST badges survive transcript-event
    /// eviction (the per-iter <c>task.{build,test}-gate.*</c> events are pruned for
    /// older / completed workstreams). Outcomes derive from the gate chain's
    /// <paramref name="failure"/> (null ⇒ the chain ran clean; <c>.Stage</c> names the
    /// failing stage). A stage whose command is empty, or that the chain never reached
    /// (an earlier stage failed first), is left unchanged so a skipped gate never
    /// clobbers a real prior result.
    /// </summary>
    private async Task PersistGateOutcomeAsync(
        string workItemId, string? buildCommand, string? testCommand, GateFailure? failure, CancellationToken ct)
    {
        string? build = string.IsNullOrWhiteSpace(buildCommand)
            ? null
            : failure?.Stage == "build" ? "failed" : "passed";
        // The test stage only ran if the chain reached it — i.e. build + quality
        // both passed. A build/quality failure short-circuits before test, so we
        // leave LastTestGate untouched rather than record a result that never ran.
        string? test = string.IsNullOrWhiteSpace(testCommand)
            ? null
            : failure is null ? "passed"
            : failure.Stage == "test" ? "failed"
            : null;
        if (build is null && test is null) return;

        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (ws is null) return;
            if (build is not null) ws.LastBuildGate = build;
            if (test is not null) ws.LastTestGate = test;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "PersistGateOutcomeAsync failed for wi={Wi} (non-fatal).", workItemId);
        }
    }

    /// <summary>
    /// Phase 56 — per-iter wrapper around <see cref="RunGateChainAsync"/>
    /// with <c>stagePrefix="per-iter"</c>. Kept as a thin shim so the
    /// GeneratorDelegate call site reads cleanly.
    /// </summary>
    private Task<GateFailure?> RunPerIterGateChainAsync(
        string workItemId,
        string workspace,
        int iter,
        string? buildCommand,
        IReadOnlyList<string>? qualityCommands,
        string? testCommand,
        string? agentId,
        string? crewId,
        CancellationToken ct,
        string? testBaseRef = null,
        bool testImpactedOnly = false,
        TimeSpan? buildTimeout = null,
        TimeSpan? testTimeout = null,
        string? initCommand = null,
        string? restoreCommand = null,
        bool runTestsElevated = false) =>
        this.RunGateChainAsync(
            workItemId, workspace, iter, "per-iter",
            buildCommand, qualityCommands, testCommand,
            agentId, crewId, ct,
            testBaseRef: testBaseRef,
            testImpactedOnly: testImpactedOnly,
            buildTimeout: buildTimeout,
            testTimeout: testTimeout,
            initCommand: initCommand,
            restoreCommand: restoreCommand,
            runTestsElevated: runTestsElevated);

    /// <summary>
    /// Phase 59 Stage B — build the URL that <c>TranscriptLine.tsx</c>
    /// renders as a 📎 view-log chip. Returns null when the runner
    /// didn't produce a log (the caller emits a payload WITHOUT the
    /// <c>logUrl</c> field so the UI skips chip rendering). The
    /// matching endpoint is
    /// <c>GET /api/workstreams/{wsId}/logs/{*relPath}</c>.
    /// </summary>
    internal static string? BuildLogUrl(string workItemId, string? logRelPath)
    {
        if (string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(logRelPath))
        {
            return null;
        }
        return $"/api/workstreams/{Uri.EscapeDataString(workItemId)}/logs/{logRelPath}";
    }

    private async Task<(IReadOnlyList<string> Files, IReadOnlyList<string> PrIds)> GetMergedPrChangedFilesFallbackAsync(
        string workItemId,
        string workspace,
        CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var linkedPrIds = new List<string>();
        var ws = await db.Workstreams.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.PrsJson })
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(ws?.PrsJson))
        {
            try
            {
                linkedPrIds.AddRange(JsonSerializer.Deserialize<List<string>>(ws.PrsJson) ?? new List<string>());
            }
            catch (JsonException ex)
            {
                this.logger?.LogWarning(ex,
                    "Merged-PR diff fallback: failed to parse Workstream.PrsJson for {WorkItemId}; falling back to PrRow.Workstream links.",
                    workItemId);
            }
        }

        var candidates = new Dictionary<string, PrRow>(StringComparer.OrdinalIgnoreCase);
        var byWorkstream = await db.Prs.AsNoTracking()
            .Where(p => p.Workstream == workItemId)
            .ToListAsync(ct);
        foreach (var pr in byWorkstream)
        {
            candidates[pr.Id] = pr;
        }

        if (linkedPrIds.Count > 0)
        {
            var distinctIds = linkedPrIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctIds.Count > 0)
            {
                var byJson = await db.Prs.AsNoTracking()
                    .Where(p => distinctIds.Contains(p.Id))
                    .ToListAsync(ct);
                foreach (var pr in byJson)
                {
                    candidates[pr.Id] = pr;
                }
            }
        }

        var orderedMerged = candidates.Values
            .Where(p => string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.UpdatedAt ?? p.LastSyncAt ?? p.CreatedAt ?? string.Empty)
            .ToList();
        if (orderedMerged.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prIds = new List<string>();
        foreach (var pr in orderedMerged)
        {
            IReadOnlyList<string> prFiles;
            try
            {
                prFiles = await this.git.GetChangedFilesFromMergedPrAsync(
                    repo: null,
                    worktreePath: workspace,
                    mergeCommitSha: pr.MergeCommitSha,
                    mergeBaseSha: pr.MergeBaseSha,
                    mergeHeadSha: pr.MergeHeadSha,
                    ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "Merged-PR diff fallback failed for {WorkItemId} {PrId}; continuing with other linked merged PRs.",
                    workItemId, pr.Id);
                continue;
            }

            if (prFiles.Count == 0)
            {
                continue;
            }

            prIds.Add(pr.Id);
            foreach (var path in prFiles)
            {
                if (seen.Add(path)) files.Add(path);
            }
        }

        return (files, prIds);
    }

    /// <summary>
    /// Phase 59 Stage A — resolve the effective impacted-test setting
    /// for a repo. Per-repo override wins; null falls through to the
    /// global <c>Forge:Test:ImpactedOnly</c> config key (default true).
    /// </summary>
    internal static bool ResolveImpactedTestOnly(RepoRow? repo, IConfiguration config)
    {
        if (repo?.TestImpactedOnly is bool overrideValue)
        {
            return overrideValue;
        }
        return config.GetValue<bool?>("Forge:Test:ImpactedOnly") ?? true;
    }

    /// <summary>
    /// Phase 94 — resolve the effective qa-e2e test-style for a repo. Per-repo
    /// <see cref="RepoRow.E2eUseBddPattern"/> override wins; null falls through
    /// to the global <c>Forge:Stories:E2eUseBddPattern</c> config key (default
    /// <c>true</c> = BDD/Gherkin). Mirrors <see cref="ResolveImpactedTestOnly"/>.
    /// </summary>
    internal static bool ResolveE2eUseBddPattern(RepoRow? repo, IConfiguration config)
    {
        if (repo?.E2eUseBddPattern is bool overrideValue)
        {
            return overrideValue;
        }
        return config.GetValue<bool?>("Forge:Stories:E2eUseBddPattern") ?? true;
    }

    /// <summary>
    /// 2026-05-21 — convert a nullable per-repo timeout (seconds) into a
    /// <see cref="TimeSpan"/>. Returns null for null / non-positive values
    /// so the gate falls back to <see cref="StoryBuildRunner.DefaultTimeout"/>.
    /// </summary>
    private static TimeSpan? TimeoutFromSeconds(int? seconds) =>
        seconds is int s && s > 0 ? TimeSpan.FromSeconds(s) : null;

    /// <summary>
    /// 2026-05-21 — merge a vstest <c>--filter</c> exclusion expression
    /// (<paramref name="excludeFilter"/>, e.g.
    /// <c>FullyQualifiedName!~CloudVsrDiscovery&amp;FullyQualifiedName!~PostUpdateOperation</c>)
    /// into a resolved <c>dotnet test</c> command.
    /// <list type="bullet">
    ///   <item>No-op when the command is empty, the exclusion is empty, or
    ///         the command isn't a <c>dotnet test</c> invocation (cargo / go /
    ///         npm / pytest use different filter flags).</item>
    ///   <item>When the command already carries a <c>--filter</c> (operator
    ///         override or the Roslyn provider's method-level narrowing), the
    ///         existing expression is AND-merged: <c>--filter "(EXISTING)&amp;(EXCLUDE)"</c>.</item>
    ///   <item>Otherwise the exclusion is appended as a fresh <c>--filter "EXCLUDE"</c>.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Split a repo test command into its body and any standing <c>--filter</c>
    /// expression (the quoted or bare value after the flag). Returns the command
    /// with the <c>--filter &lt;expr&gt;</c> removed plus the extracted expression
    /// (null when absent). Used to feed the impacted-test resolver a filter-free
    /// command — every provider treats a bare <c>--filter</c> as an operator pin
    /// and refuses to narrow — then re-merge the filter via
    /// <see cref="ApplyTestExcludeFilter"/>. Inverse of that merge.
    /// </summary>
    /// <summary>
    /// gap #1 (2026-05-27) — synthesize a SCOPED <c>Microsoft.Build.Traversal</c>
    /// project under <c>{worktree}/.forge/</c> that references only the impacted
    /// test projects, so a multi-project change runs just those instead of the
    /// whole-suite <c>build.proj</c>. Clones the SDK declaration from the original
    /// traversal proj (<paramref name="traversalProjRel"/>) so the SDK version
    /// matches the repo. Returns the worktree-relative path of the generated proj,
    /// or <c>null</c> on ANY problem (missing/unreadable original, non-Traversal
    /// SDK, IO error) so the caller falls back to the whole suite — never worse
    /// than today's behaviour. The <c>.forge/</c> dir is git-excluded so the
    /// generated proj never leaks into a PR.
    /// </summary>
    private async Task<string?> TryWriteScopedTraversalProjAsync(
        string workspace, string traversalProjRel, IReadOnlyList<string> impactedProjectsRel, CancellationToken ct)
    {
        try
        {
            var origPath = Path.Combine(workspace, traversalProjRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(origPath)) return null;
            var origText = await File.ReadAllTextAsync(origPath, ct);
            var m = Regex.Match(origText, @"<Project\b[^>]*\bSdk\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var sdk = m.Groups[1].Value;
            // Only the Traversal SDK runs `test` across <ProjectReference> items; for
            // any other SDK a synthesized multi-ref proj wouldn't behave like a test
            // host, so keep the whole-suite fallback instead.
            if (sdk.IndexOf("Microsoft.Build.Traversal", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"<Project Sdk=\"{sdk}\">");
            sb.AppendLine("  <!-- Auto-generated by Forge impacted-test narrowing (gap #1). Scopes `dotnet test` to the impacted test projects so a multi-project change does not run the whole suite. Regenerated per gate run; safe to delete. -->");
            sb.AppendLine("  <ItemGroup>");
            foreach (var rel in impactedProjectsRel)
            {
                var abs = Path.Combine(workspace, rel.Replace('/', Path.DirectorySeparatorChar));
                sb.AppendLine($"    <ProjectReference Include=\"{abs}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");

            var forgeDir = Path.Combine(workspace, ".forge");
            Directory.CreateDirectory(forgeDir);
            await TryAddGitInfoExcludeAsync(workspace, ".forge/", ct);
            await File.WriteAllTextAsync(Path.Combine(forgeDir, "forge-impacted-tests.proj"), sb.ToString(), ct);
            return ".forge/forge-impacted-tests.proj";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex,
                "Scoped-traversal generation failed for {Workspace}; falling back to whole suite.", workspace);
            return null;
        }
    }

    /// <summary>
    /// Replace the single command token equal to <paramref name="oldToken"/> with
    /// <paramref name="newToken"/> (e.g. swap the Traversal target <c>build.proj</c>
    /// for the generated scoped proj). Token-aware so it never matches a substring;
    /// re-quotes any token containing whitespace.
    /// </summary>
    internal static string ReplaceCommandToken(string command, string oldToken, string newToken)
    {
        var tokens = ImpactedTestResolver.ParseCommandLine(command);
        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var t = string.Equals(tokens[i], oldToken, StringComparison.OrdinalIgnoreCase) ? newToken : tokens[i];
            sb.Append(t.IndexOf(' ') >= 0 ? $"\"{t}\"" : t);
        }
        return sb.ToString();
    }

    internal static (string Command, string? Filter) SplitStandingFilter(string testCommand)
    {
        if (string.IsNullOrWhiteSpace(testCommand)) return (testCommand, null);
        // `--filter` is vstest syntax — only split for `dotnet test`. A non-.NET
        // command (cargo / go / npm) that happens to carry `--filter` must keep
        // it, since ApplyTestExcludeFilter (the re-merge) only touches dotnet test
        // and would otherwise drop it.
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                testCommand, @"\bdotnet\s+test\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return (testCommand, null);
        }
        var m = System.Text.RegularExpressions.Regex.Match(
            testCommand, "\\s*--filter\\s+(?:\"(?<q>[^\"]*)\"|(?<u>\\S+))");
        if (!m.Success) return (testCommand, null);
        var filter = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
        var stripped = testCommand[..m.Index] + testCommand[(m.Index + m.Length)..];
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "\\s{2,}", " ").Trim();
        return (stripped, string.IsNullOrWhiteSpace(filter) ? null : filter);
    }

    internal static string ApplyTestExcludeFilter(string testCommand, string? excludeFilter)
    {
        if (string.IsNullOrWhiteSpace(testCommand) || string.IsNullOrWhiteSpace(excludeFilter))
        {
            return testCommand;
        }
        // vstest --filter syntax only applies to `dotnet test`.
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                testCommand, @"\bdotnet\s+test\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return testCommand;
        }

        var exclude = excludeFilter!.Trim();
        // Find an existing --filter <value> (quoted expression or bare token).
        var m = System.Text.RegularExpressions.Regex.Match(
            testCommand,
            "--filter\\s+(?:\"(?<q>[^\"]*)\"|(?<u>\\S+))");
        if (m.Success)
        {
            var existing = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
            var merged = $"--filter \"({existing})&({exclude})\"";
            return testCommand[..m.Index] + merged + testCommand[(m.Index + m.Length)..];
        }
        return $"{testCommand} --filter \"{exclude}\"";
    }

    /// <summary>
    /// Phase 56 — format a <see cref="GateFailure"/> into the multi-line
    /// note returned by <c>OnPassValidate</c>. The note ends up on the
    /// deliverable row's <c>Notes</c> (rejected pass) and flows into the
    /// next iter's evaluator chain via <c>FormatPriorChain</c>.
    /// </summary>
    private static string BuildGateRejectionNote(GateFailure failure, int attempt, int maxAttempts)
    {
        var stageBanner = failure.Stage switch
        {
            "quality" => "POST-PASS QUALITY GATE REJECTED PASS",
            "test" => "POST-PASS TEST GATE REJECTED PASS",
            _ => "POST-PASS BUILD GATE REJECTED PASS",
        };
        var sb = new StringBuilder();
        sb.AppendLine($"{stageBanner} (recovery attempt {attempt}/{maxAttempts})");
        sb.AppendLine();
        sb.AppendLine($"Stage    : {failure.Stage}");
        sb.AppendLine($"Command  : {failure.Command}");
        sb.AppendLine($"Exit code: {failure.Result.ExitCode}");
        sb.AppendLine($"Errors   : {failure.Result.ErrorCount}");
        sb.AppendLine($"Warnings : {failure.Result.WarningCount}");
        sb.AppendLine($"Duration : {failure.Result.Duration.TotalSeconds:F1}s{(failure.Result.TimedOut ? " (TIMED OUT)" : string.Empty)}");
        if (!string.IsNullOrWhiteSpace(failure.Result.ErrorSummary))
        {
            sb.AppendLine();
            sb.AppendLine("Error summary:");
            sb.AppendLine("```");
            var summary = failure.Result.ErrorSummary;
            if (summary.Length > 4096)
            {
                summary = summary[..4096] + "\n…(truncated)";
            }
            sb.AppendLine(summary);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine(
            "The evaluator scored this iteration at pass, but the post-pass gate suite rejected the diff before the PR could open. The next iteration MUST fix the failing gate before pass is allowed again. The PR will not open until the post-pass gate suite is green.");
        return sb.ToString();
    }

    /// <summary>
    /// Phase 49 follow-up -- deserialize <see cref="WorkstreamRow.AgentsJson"/>
    /// into a non-null list. Used at the impl call site to read the
    /// generator + evaluator pair the orchestrator picked at claim time
    /// so RunGenEvalLoopAsync runs against THOSE agents, not the crew's
    /// global defaults.
    /// </summary>
    private static List<string> ParseAgentsList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    // #(b) — keep only declared target paths whose directory actually exists in
    // the worktree. A hallucinated path (e.g. a .NET-style `src/CodeIntelligence`
    // in a Go repo) is dropped so it can't define a phantom off-task scope.
    // Returns the input unchanged when the worktree itself is missing (can't
    // verify → don't silently disable the guard). New-file targets survive when
    // their parent directory exists; module-relative paths that don't resolve
    // under the worktree root are dropped (→ fail-open, the safe direction).
    internal static IReadOnlyList<string> FilterTargetsToExisting(string? workspace, IReadOnlyList<string>? targets)
    {
        if (targets is null || targets.Count == 0
            || string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            return targets ?? Array.Empty<string>();
        }

        var kept = new List<string>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            try
            {
                var norm = t.Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar)
                            .TrimStart(Path.DirectorySeparatorChar);
                var full = Path.Combine(workspace, norm);
                var dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)))
                {
                    kept.Add(t);
                }
            }
            catch
            {
                // malformed path — drop it
            }
        }

        return kept;
    }

    /// <summary>
    /// Load the same-spawn workstream set and run
    /// <see cref="WorkstreamWorktreeSlug.Compute"/> to produce the canonical
    /// worktree directory name (<c>stage-{phaseNum}.{stageNum}-{stageTitle}</c>
    /// for plan-anchored rows, <c>ws-{sanitizedId}</c> otherwise). Always
    /// returns a non-empty slug — handcrafted ad-hoc workstreams (no
    /// <see cref="WorkstreamRow.StorySpawnId"/>) hit the
    /// <see cref="WorkstreamWorktreeSlug"/> fallback branch.
    /// </summary>
    private async Task<string> ComputeWorkstreamWorktreeSlugAsync(
        WorkstreamRow workstream, CancellationToken ct)
    {
        if (workstream is null) throw new ArgumentNullException(nameof(workstream));

        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await WorkstreamWorktreeSlug.ComputeAsync(workstream, control, db, ct);
    }

    /// <summary>
    /// Loads distinct architect crew agents for round-robin assignment.
    /// Returns up to <paramref name="count"/> agent IDs from the day-shift
    /// architect crew, excluding the evaluator default so gen ≠ eval.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadArchitectRosterAsync(int count, CancellationToken ct)
    {
        var (gens, _) = await this.LoadArchitectAndEvaluatorRostersAsync(count, ct);
        return gens;
    }

    /// <summary>
    /// Phase 47 -- load TWO distinct rosters (generators + evaluators) so
    /// the parallel plan-doc fan-out (4 docs running concurrently via
    /// <c>Task.WhenAll</c>) can assign a different (generator, evaluator)
    /// pair to each doc. Without this, every doc's evaluator side is
    /// pinned to <c>crew.DefaultEvaluatorAgentId</c> and the parallel
    /// invocations serialise behind a single agent.
    ///
    /// <para>Phase 50 fix (2026-05-11) -- when the crew has fewer agents
    /// than docs, the previous algorithm collapsed the evaluator pool to
    /// <c>{defaultEval}</c> only, so overflow docs (idx ≥ pool.Count)
    /// landed on a null override → all fell back to
    /// <c>crew.DefaultEvaluatorAgentId</c>. Operator-reported: same
    /// evaluator picked across all 4 plan-docs even on a 3-4 agent crew.
    /// The new algorithm rotates through the full agent list with a
    /// modulo offset, enforcing <c>gen != eval</c> per doc. The
    /// generator pool and evaluator pool may now share agents -- that's
    /// fine; the per-agent runner queue serialises concurrent claims on
    /// the same agent without blocking the orchestrator. With M agents
    /// the algorithm produces M distinct gen/eval pairs that cycle
    /// across N docs.</para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Generators, IReadOnlyList<string> Evaluators)>
        LoadArchitectAndEvaluatorRostersAsync(int count, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var crew = await control.Crews.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == "crew-arch", ct);
        if (crew is null)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var defaultGen = crew.DefaultGeneratorAgentId;
        var defaultEval = crew.DefaultEvaluatorAgentId;

        var allAgents = await control.Agents.AsNoTracking()
            .Where(a => a.CrewId == "crew-arch")
            .OrderBy(a => a.Sort)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (allAgents.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        // Generator order: defaultGen first, then everyone else in
        // stable sort order. Unlike pre-Phase-50, we DO include the
        // defaultEval in the generator pool -- the pairing step below
        // enforces gen != eval per doc, so disjoint pools aren't
        // required upstream.
        var genOrder = allAgents.ToList();
        if (!string.IsNullOrEmpty(defaultGen))
        {
            genOrder.Remove(defaultGen);
            genOrder.Insert(0, defaultGen);
        }

        // Evaluator order: defaultEval first, then everyone else.
        var evalOrder = allAgents.ToList();
        if (!string.IsNullOrEmpty(defaultEval))
        {
            evalOrder.Remove(defaultEval);
            evalOrder.Insert(0, defaultEval);
        }

        // Per-doc pairing -- rotate the generator pool, and pick the
        // evaluator at a wrap-around offset that guarantees gen != eval
        // when at least 2 agents exist. Single-agent crews fall through
        // with eval=null (caller's crew.DefaultEval fallback kicks in;
        // gen == eval is unavoidable but flagged via empty override).
        var generators = new List<string>(count);
        var evaluators = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var gen = genOrder[i % genOrder.Count];
            generators.Add(gen);

            string? eval = null;
            for (var j = 0; j < evalOrder.Count; j++)
            {
                // Start scanning at offset (i+1) so consecutive docs use
                // different (gen, eval) pairings. j=0..count walks the
                // roster wrapping around; first non-gen wins.
                var candidate = evalOrder[(i + 1 + j) % evalOrder.Count];
                if (!string.Equals(candidate, gen, StringComparison.OrdinalIgnoreCase))
                {
                    eval = candidate;
                    break;
                }
            }
            // eval stays null when the crew has only one agent -- the
            // pairing is impossible. The caller passes the null override
            // through to InvokeAsync which falls back to crew.DefaultEval
            // (which equals gen in that degenerate case -- unavoidable).
            evaluators.Add(eval ?? string.Empty);
        }

        return (generators, evaluators);
    }

    /// <summary>
    /// Phase 53 follow-up -- resolve the Phase-47 architect/evaluator roster
    /// slot for a single sequential plan doc. Sequential mode invokes one doc
    /// per tick, but the assignment must stay keyed to the canonical four-doc
    /// order so architecture, tech-spec, implementation-plan, and e2e-scenarios
    /// do not all collapse to roster slot 0.
    /// </summary>
    internal async Task<(string? GeneratorAgentId, string? EvaluatorAgentId)> LoadPlanDocAgentPairAsync(
        string docKey,
        CancellationToken ct)
    {
        var docIndex = PlanDocStateHelpers.PlanDocIndexOf(docKey);
        if (docIndex < 0)
        {
            throw new ArgumentException($"Unknown plan-doc key '{docKey}'.", nameof(docKey));
        }

        var (generatorAgentIds, evaluatorAgentIds) =
            await this.LoadArchitectAndEvaluatorRostersAsync(PlanDocStateHelpers.PlanDocOrder.Count, ct);

        var generatorAgentId = generatorAgentIds.Count > docIndex
            ? NullIfEmpty(generatorAgentIds[docIndex])
            : null;
        var evaluatorAgentId = evaluatorAgentIds.Count > docIndex
            ? NullIfEmpty(evaluatorAgentIds[docIndex])
            : null;

        return (generatorAgentId, evaluatorAgentId);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// 2026-05-20 — per-plan-doc production lock, keyed by the synthetic
    /// <c>plan-doc:{spawnId}:{file}</c> work-item id. Acquired at the top of
    /// <see cref="ProducePlanFileAsync"/> — before the fresh-start cleanup
    /// that deletes the on-disk doc — so an operator Restart's fire-and-forget
    /// resume and the <c>SpawnDependencyScheduler</c> tick can't both produce
    /// the same doc at once (the resume path bypasses the orchestrator's
    /// <c>planDocInFlight</c> guard, so that guard alone doesn't cover it).
    /// The loser fails fast with <see cref="PlanDocInFlightException"/> BEFORE
    /// deleting anything, so it neither stomps the winner's file nor halts the
    /// orchestrator (both the orchestrator catch and the resume wrapper treat
    /// it as "retry next tick"). Broader than DeliverableLoop's own
    /// per-workItemId mutex, which only guards the gen-eval loop body — this
    /// also covers the cleanup + worktree steps that run before it.
    /// </summary>
    /// <summary>
    /// 2026-06-23 — per-doc production lock with a progress heartbeat. The
    /// holder refreshes <see cref="HeartbeatTicks"/> at the start of every
    /// gen-eval iteration (see <see cref="RefreshPlanDocLockHeartbeat"/>), so a
    /// contending drive can tell a HEALTHY long run (heartbeat fresh) from a
    /// HUNG one (heartbeat stale) and force-recover the latter instead of
    /// skipping "in-flight" forever.
    /// </summary>
    internal sealed class PlanDocLock
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public long HeartbeatTicks;

        // 2026-06-23 — the holder's drive cancellation. Linked to the launch ct,
        // bounded by MaxPlanDocDriveWallClock, and Cancel()'d when a contender
        // force-recovers (steals) this lock — so a HUNG holder actually STOPS
        // (its awaits throw, the drive exits, the SDK session tears down)
        // instead of running on after it has been bypassed.
        public CancellationTokenSource? Cts;
    }

    private static readonly ConcurrentDictionary<string, PlanDocLock> PlanDocProductionLocks =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 2026-06-23 — absolute wall-clock backstop for a single plan-doc drive.
    /// The PRIMARY stop mechanism is steal-cancellation (a contender that
    /// force-recovers a stale lock <c>Cancel()</c>s the hung holder's CTS, which
    /// the orchestrator's normal re-ticks trigger within
    /// <see cref="PlanDocLockStaleAfter"/> of a hang). This is the last resort
    /// for a drive that somehow never gets contended (e.g. the spawn is paused).
    /// Set far above any healthy drive so it never false-kills live work.
    /// </summary>
    private static readonly TimeSpan MaxPlanDocDriveWallClock = TimeSpan.FromHours(4);

    /// <summary>
    /// 2026-06-23 — staleness threshold for <see cref="PlanDocProductionLocks"/>.
    /// Because the heartbeat refreshes EACH iteration, a healthy multi-iter
    /// drive keeps it fresh; only a hung iteration (no progress for this long)
    /// trips recovery. Set well above any single healthy iteration's wall-clock
    /// so a live drive is never stolen from. Without this, a hung drive (e.g. a
    /// runaway Copilot child the inactivity watchdog can't see, on a background
    /// drive launched with <c>CancellationToken.None</c>) held the lock until a
    /// manual server restart — every orchestrator tick logged "skipped
    /// (in-flight)" and the doc never re-dispatched (observed 2026-06-23 on
    /// code-intelligence:ENABLE-MCP architecture.md).
    /// </summary>
    private static readonly TimeSpan PlanDocLockStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Proof-of-progress for a held plan-doc production lock: refreshes the
    /// holder's heartbeat so a contending drive doesn't mistake a long-but-
    /// healthy run for a hang. Called at the start of each gen-eval iteration.
    /// No-op for non-plan-doc work items (the key is absent).
    /// </summary>
    internal static void RefreshPlanDocLockHeartbeat(string workItemId)
    {
        if (!string.IsNullOrEmpty(workItemId)
            && PlanDocProductionLocks.TryGetValue(workItemId, out var held))
        {
            Interlocked.Exchange(ref held.HeartbeatTicks, DateTimeOffset.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// 2026-06-23 — single-producer guard for a plan-doc with hung-holder
    /// recovery. Returns the held lock entry (caller wraps it in a
    /// <see cref="PlanDocLockReleaser"/>). Throws
    /// <see cref="PlanDocInFlightException"/> when a LIVE drive owns it (fresh
    /// heartbeat, or just-acquired with no heartbeat yet). When the current
    /// holder's heartbeat is stale beyond <paramref name="staleAfter"/> the
    /// prior drive is presumed hung: the entry is replaced with a fresh lock and
    /// taken, so the orchestrator can re-drive instead of skipping "in-flight"
    /// forever. The hung drive's eventual <c>Release</c> lands on the orphaned
    /// semaphore (harmless — <see cref="PlanDocLockReleaser"/> swallows it).
    /// </summary>
    internal static async Task<PlanDocLock> AcquirePlanDocProductionLockAsync(
        string docWorkItemId,
        string repoPathForError,
        TimeSpan staleAfter,
        TimeSpan maxDriveWallClock,
        ILogger? logger,
        CancellationToken ct)
    {
        var lockEntry = PlanDocProductionLocks.GetOrAdd(docWorkItemId, _ => new PlanDocLock());
        if (!await lockEntry.Sem.WaitAsync(TimeSpan.Zero, ct))
        {
            var hb = Interlocked.Read(ref lockEntry.HeartbeatTicks);
            var sinceBeat = hb == 0
                ? TimeSpan.Zero
                : DateTimeOffset.UtcNow - new DateTimeOffset(hb, TimeSpan.Zero);
            if (hb == 0 || sinceBeat < staleAfter)
            {
                throw new PlanDocInFlightException(repoPathForError, docWorkItemId);
            }

            logger?.LogWarning(
                "Plan doc '{Doc}' production lock stale — no progress for {Since:g} (> {Max:g}); "
                + "presuming the prior drive hung — cancelling it and replacing the lock so the "
                + "orchestrator can re-drive.",
                docWorkItemId, sinceBeat, staleAfter);

            // Cancel the hung holder's drive so it actually STOPS (its awaits
            // throw, ProducePlanFileAsync exits, the SDK session tears down)
            // rather than running on after we bypass it.
            try { lockEntry.Cts?.Cancel(); }
            catch (ObjectDisposedException) { /* holder already finished */ }

            var fresh = new PlanDocLock();
            await fresh.Sem.WaitAsync(ct);  // we now own the fresh lock
            PlanDocProductionLocks[docWorkItemId] = fresh;
            lockEntry = fresh;
        }

        // Bound this drive: linked to the launch ct, with a wall-clock backstop.
        // The caller threads lockEntry.Cts.Token through the whole drive.
        lockEntry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lockEntry.Cts.CancelAfter(maxDriveWallClock);
        Interlocked.Exchange(ref lockEntry.HeartbeatTicks, DateTimeOffset.UtcNow.Ticks);
        return lockEntry;
    }

    /// <summary>Test hook — drop all production locks so a test starts clean.</summary>
    internal static void ResetPlanDocProductionLocksForTesting() => PlanDocProductionLocks.Clear();

    /// <summary>
    /// Releases a held plan-doc production lock on dispose, so
    /// <see cref="ProducePlanFileAsync"/> can hold it for its whole lifetime
    /// via <c>using var</c> instead of wrapping its ~750-line body in a manual
    /// try/finally.
    /// </summary>
    private readonly struct PlanDocLockReleaser : IDisposable
    {
        private readonly SemaphoreSlim semaphore;
        private readonly CancellationTokenSource? cts;

        // Captures THIS drive's semaphore + CTS at construction (not the live
        // dictionary entry, which a stale-recovery may replace) so dispose acts
        // on its own lock, never a successor's.
        public PlanDocLockReleaser(PlanDocLock entry)
        {
            this.semaphore = entry.Sem;
            this.cts = entry.Cts;
        }

        public void Dispose()
        {
            try { this.semaphore.Release(); }
            catch (SemaphoreFullException) { /* already released — defensive */ }
            catch (ObjectDisposedException) { /* lock was replaced after a stale-recovery — defensive */ }
            try { this.cts?.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    private async Task<string> ProducePlanFileAsync(
        StorySpawnRow spawn, WorkItemRow story,
        (string FileName, string Brief, string RepoPath) spec,
        TimeSpan budget, bool forceResume,
        string? generatorAgentOverride,
        string? evaluatorAgentOverride,
        string[] siblingSpecs,
        string worktreePath,
        RepoRow? repo,
        string planBranch,
        string? attachmentsJson,
        CancellationToken ct,
        bool restartFromScratch = false)
    {
        // Phase 42 -- agent's working dir IS the git worktree root. Doc lives
        // at its real repo path inside the worktree so the agent's tools see
        // both the doc AND the rest of the codebase (siblings, source files,
        // prior docs) at their real locations.
        var diskPath = Path.Combine(worktreePath, spec.RepoPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);

        var docWorkItemId = $"plan-doc:{spawn.Id}:{spec.FileName}";

        // 2026-05-20 — single-producer guard. Acquire the per-doc production
        // lock BEFORE the fresh-start cleanup below deletes the on-disk doc.
        // If another run already owns this doc (an operator Restart resume
        // racing the scheduler tick), bail immediately with a transient
        // signal — without touching the file, worktree, or DB — so the
        // in-flight winner produces the doc and the orchestrator retries on
        // its next tick. Held for the whole method via `using var`.
        var lockEntry = await AcquirePlanDocProductionLockAsync(
            docWorkItemId, spec.RepoPath, PlanDocLockStaleAfter, MaxPlanDocDriveWallClock, this.logger, ct);
        using var productionLockHold = new PlanDocLockReleaser(lockEntry);

        // Thread this drive's bounded cancellation through the rest of the
        // method (RunGenEvalLoopAsync + its per-iter closures) so a hung drive
        // that has its lock stolen — or that simply outlives
        // MaxPlanDocDriveWallClock — actually STOPS rather than leaking the lock
        // and a runaway Copilot child. Linked to the original ct, so caller
        // cancellation still propagates.
        ct = lockEntry.Cts!.Token;

        // Phase 45 follow-up -- pass-verdict guard. If the latest deliverable
        // for this doc already evaluated to `pass`, do NOT spin up another
        // LLM iteration. The previous file content is already in the worktree;
        // returning it short-circuits the loop. forceResume (operator clicked
        // Retry on a stalled doc) bypasses; Restart works because it deletes
        // deliverables first, so the check finds none. Without this guard,
        // any path that re-enters the strategy (Resume click, scheduler tick,
        // base-branch-change replay, boot recovery) re-iterates a doc that
        // was already done -- burning tokens and confusing the UI's status.
        List<DeliverableRow> existingDeliverables;
        using (var passScope = this.scopeFactory.CreateScope())
        {
            var passDb = passScope.ServiceProvider.GetRequiredService<ProductDbContext>();
            existingDeliverables = await passDb.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == docWorkItemId)
                .ToListAsync(ct);
        }

        if (!forceResume)
        {
            var latestVerdict = existingDeliverables
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault()?.Verdict;
            if (string.Equals(latestVerdict, "pass", StringComparison.OrdinalIgnoreCase))
            {
                var cachedFromDisk = File.Exists(diskPath)
                    ? await File.ReadAllTextAsync(diskPath, ct)
                    : string.Empty;
                this.logger?.LogInformation(
                    "Plan file '{File}' (workItem {WorkItem}) latest verdict already 'pass'; skipping architect invocation.",
                    spec.FileName, docWorkItemId);
                // A replay of an already-passed doc also repairs a badge an
                // earlier sweep left on Failed.
                await this.MarkPlanDocCompletedAsync(docWorkItemId, ct);
                return cachedFromDisk;
            }
        }

        // Phase 50 -- fresh-start cleanup. Three distinct leak vectors all
        // surface here, and the fix is the same: clear the file off disk
        // (after EnsureWorktreeAsync has re-checked it out from the branch
        // tip) and purge any stale DB rows BEFORE the architect runs, so
        // the agent generates from scratch instead of editing the prior
        // run's content incrementally.
        //
        //   (a) Re-spawn after a merged plan PR. The previous spawn's
        //       plan PR merged `docs/stories/{storyId}/{file}.md` into
        //       the base branch. EnsureBranchFreshAsync resets the new
        //       plan branch tip to base -- which still carries those
        //       docs -- so the new architect's worktree boots up with
        //       the OLD plan-doc already on disk. Without this cleanup
        //       the architect sees an "existing file" and edits it
        //       incrementally instead of regenerating from the user's
        //       new story description.
        //
        //   (b) DeleteSpawn race. Spawn.Id is deterministic
        //       (`spawn-{safeStoryId}`), so deliverable WorkItemIds
        //       collide across spawn lifecycles. If a previous
        //       DeleteSpawn missed any deliverables (in-flight agent
        //       writing while the cancel propagated), they'd live on
        //       under the new spawn's identity. Defensively delete
        //       every deliverable + transcript + queue row keyed by
        //       this docWorkItemId so the new architect starts at
        //       iter 1 with a clean slate even if DeleteSpawn was
        //       imperfect.
        //
        //   (c) Phase 50 follow-up -- operator-initiated Restart. The
        //       RestartArtifact endpoint wipes deliverables/transcripts/
        //       queue rows and the architect scratch dir, then fires
        //       ResumeSingleArtifactAsync with `restartFromScratch=true`.
        //       Previously this cleanup block was gated by
        //       `if (!forceResume)`, so Restart (forceResume=true)
        //       bypassed it -- leaving the prior 56-iter architecture.md
        //       on disk for the new "iter 1" to read and incrementally
        //       edit. Now `restartFromScratch=true` forces the cleanup
        //       regardless of forceResume; the OR-fallback on
        //       `existingDeliverables.Count == 0` is belt-and-suspenders
        //       for the boot-recovery edge case (process killed after
        //       the Restart endpoint wiped deliverables but before the
        //       fire-and-forget Resume reached this point --
        //       StoryResumeRecoveryService doesn't know the resume
        //       originated from a Restart and re-fires with the default
        //       `restartFromScratch=false`).
        //
        // Trigger condition: `restartFromScratch` (explicit operator signal)
        // OR `!forceResume && existingDeliverables.Count == 0` (fresh-spawn).
        // For ordinary Resume (forceResume=true, restartFromScratch=false,
        // deliverables.Count > 0 because the resume is mid-iter), the
        // cleanup correctly does NOT trigger -- the gen/eval loop's own
        // resume logic in DeliverableLoop picks up from lastPersisted.Iter.
        var isFreshFirstIteration = existingDeliverables.Count == 0;
        var shouldCleanup = restartFromScratch || (!forceResume && isFreshFirstIteration);

        // Phase 53 — resolve this doc's position in the canonical sequence so
        // both the fresh-start cleanup AND the evaluator prompt can split
        // siblings into upstream (authoritative) vs downstream (stale legacy
        // residue, ignore). Declared here at the top of the routine because
        // both the cleanup block AND the evaluator prompt builder consume it.
        var docKey = Path.GetFileNameWithoutExtension(spec.FileName).ToLowerInvariant();
        var docOrder = PlanDocStateHelpers.PlanDocOrder;
        var docIdx = -1;
        for (var i = 0; i < docOrder.Count; i++)
        {
            if (string.Equals(docOrder[i], docKey, StringComparison.OrdinalIgnoreCase))
            {
                docIdx = i;
                break;
            }
        }
        var isSequential = PlanDocStateHelpers.IsSequentialMode(spawn);
        var docDir = (Path.GetDirectoryName(spec.RepoPath) ?? string.Empty).Replace('\\', '/');

        if (shouldCleanup)
        {
            await EnsureFreshDocWorkspaceAsync(
                this.scopeFactory, this.logger, diskPath, docWorkItemId, restartFromScratch, ct);

            // Phase 53 follow-up — wipe stale DOWNSTREAM sibling docs from
            // disk on fresh-start under sequential mode. They get there
            // because the previous end-to-end spawn merged all four plan
            // docs into the base branch; the new per-doc branch is forked
            // from base, so the architect's worktree boots up with the
            // OLD downstream docs already on disk. Without this sweep the
            // generator references them and the evaluator scores down for
            // stale-text contradictions — neither has any way to fix
            // downstream files in this PR (cross-branch edits are
            // reverted by the worktree's checkout of the doc's branch).
            //
            // Best-effort: file delete failures are logged but do not
            // throw. The evaluator prompt (sequentialDocPolicy) also
            // tells gpt-5.5 to ignore downstream contradictions, so a
            // failed sweep is degraded but not broken.
            if (isSequential && docIdx >= 0)
            {
                var downstreamKeysForCleanup = new List<string>();
                for (var i = docIdx + 1; i < docOrder.Count; i++)
                {
                    downstreamKeysForCleanup.Add(docOrder[i]);
                }
                foreach (var k in downstreamKeysForCleanup)
                {
                    var siblingFile = PlanDocStateHelpers.PlanDocFileName[k];
                    var siblingDiskPath = Path.Combine(
                        worktreePath,
                        docDir.Replace('/', Path.DirectorySeparatorChar),
                        siblingFile);
                    if (!File.Exists(siblingDiskPath)) continue;
                    try
                    {
                        File.Delete(siblingDiskPath);
                        this.logger?.LogInformation(
                            "Phase 53 fresh-start cleanup: removed stale downstream sibling {Sibling} for architect run on {Doc} (workItem {WorkItem}).",
                            siblingFile, spec.FileName, docWorkItemId);
                    }
                    catch (Exception ex)
                    {
                        this.logger?.LogWarning(ex,
                            "Phase 53 fresh-start cleanup: could not delete stale downstream sibling {Sibling}; evaluator may see contradictory text (mitigated by sequentialDocPolicy prompt guidance).",
                            siblingDiskPath);
                    }
                }
            }
        }

        // Resume case -- historical guard from before the Phase 45 pass-verdict
        // check above. The pre-Phase-45 logic skipped the gen-eval loop whenever
        // the file existed AND any deliverable existed, regardless of whether
        // the latest verdict was a terminal `pass`. That misfired on the common
        // resume path: process restart (or in-process loop crash) mid-iteration
        // leaves the file on disk + N `iterate`-verdict deliverables, and the
        // 15s SpawnDependencyScheduler tick re-enters DrivePlanAsync →
        // ProducePlanArtifactsAsync → here. With the old `hasDeliverables`
        // short-circuit the doc was silently abandoned (observed
        // 2026-05-09 on failover-cluster:XRAFT architecture.md iter 7 -- the
        // generator started, the process was restarted, and no further iters
        // ever ran because every subsequent tick returned the cached file
        // content). The Phase 45 guard above now correctly handles the
        // already-passed case, so we no longer need a second short-circuit
        // here. Falling through means the gen-eval loop's own resume logic
        // (DeliverableLoop picks up at the next iter via lastPersisted.Iter)
        // takes over and the doc continues toward `pass`.

        // Phase 43 -- build attachment prompt section if the story has uploaded reference docs.
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        // Phase 42 -- sibling docs live at their real repo paths in the
        // shared worktree (no copy-into-siblings/ trick anymore). Some may
        // not exist yet on iter 1 of parallel execution; the agent reads
        // whichever are present at the time.
        //
        // Phase 50 follow-up -- we briefly experimented with freezing
        // siblings via a per-drive snapshot, but operator feedback was
        // explicit: the convergence problem is per-doc attention (the
        // agent doesn't address its own prior feedback), not cross-doc
        // moving targets. The other three plan docs converge fine
        // against live siblings; only architecture.md gets stuck. The
        // snapshot approach was abandoned.
        var siblingPathsList = string.Join("\n", siblingSpecs.Select(p => $"  - `{p}`"));

        // Phase 53 — split sibling docs into UPSTREAM (already-merged → authoritative
        // source-of-truth) and DOWNSTREAM (not yet written; on-disk copies are stale
        // residues from a prior spawn that will be regenerated against THIS doc once
        // it merges). Under sequential mode the evaluator must score on upstream
        // consistency only — flagging downstream contradictions wastes an iteration
        // because the architect agent has no path to fix downstream files in this PR.
        // Under legacy parallel mode (PlanDocPrsJson is null) all four docs ship in
        // one PR, so siblings are peers and the original cross-doc check applies.
        // (docKey, docOrder, docIdx, isSequential, docDir declared above the
        //  cleanup block; reused here to build the evaluator's policy section.)
        string sequentialDocPolicy = string.Empty;
        string upstreamPathsList = string.Empty;
        if (isSequential && docIdx >= 0)
        {
            var upstreamKeys = new List<string>();
            for (var i = 0; i < docIdx; i++) upstreamKeys.Add(docOrder[i]);
            var downstreamKeys = new List<string>();
            for (var i = docIdx + 1; i < docOrder.Count; i++) downstreamKeys.Add(docOrder[i]);

            upstreamPathsList = upstreamKeys.Count > 0
                ? string.Join("\n", upstreamKeys.Select(k =>
                    $"  - `{docDir}/{PlanDocStateHelpers.PlanDocFileName[k]}` ({k})"))
                : "  _(none — this is the first doc in the sequence)_";
            var downstreamPathsList = downstreamKeys.Count > 0
                ? string.Join("\n", downstreamKeys.Select(k =>
                    $"  - `{docDir}/{PlanDocStateHelpers.PlanDocFileName[k]}` ({k})"))
                : "  _(none — this is the last doc in the sequence)_";

            sequentialDocPolicy = $$"""

                ## Sequential plan-doc mode (Phase 53) — READ CAREFULLY
                The four plan docs ship in a strict sequence, ONE PR per doc:
                `architecture → tech-spec → implementation-plan → e2e-scenarios`.
                This run scores **`{{spec.FileName}}`** only. The other docs split
                into two categories:

                **Upstream (already merged, authoritative source-of-truth):**
                {{upstreamPathsList}}

                **Downstream (will be regenerated AFTER this PR merges — IGNORE for scoring):**
                {{downstreamPathsList}}

                ### What this means for your scoring
                - DO score down for contradictions between this doc and any
                  UPSTREAM doc above — those are merged, immutable, and the
                  architect MUST anchor to them.
                - DO **NOT** score down for contradictions between this doc and
                  any DOWNSTREAM doc. Downstream files on disk are stale
                  residues from a prior end-to-end spawn that pre-dates the
                  current architecture. The orchestrator will regenerate every
                  downstream doc against THIS merged file in a subsequent
                  spawn. Flagging a downstream-doc mismatch wastes an
                  iteration — the architect agent CANNOT fix downstream
                  files in this PR (the worktree is scoped to this doc's
                  branch; cross-branch edits would be reverted).
                - DO confirm internal consistency *within* this doc, and
                  anchoring to the actual source under `src/`.

                If you grep for verification, scope the search to this file
                + upstream docs only. Treat any downstream-doc hit as
                pre-existing legacy text, not as a bug in this draft.

                """;
        }

        var generatorPrompt = $$"""
            You are the Architect agent for Forge story `{{spawn.StoryId}}`.

            ## Story title
            {{story.Title}}

            ## Story description (operator-authored prompt)
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Story points
            {{spawn.StoryPoints}}

            ## Your task
            Produce ONE planning artifact at `{{spec.RepoPath}}` (relative to
            your working directory, which is the repo root). Real,
            production-quality markdown -- not a placeholder, not an outline.
            Anchor every section on the story description's concrete details.

            ### Brief for this file
            {{spec.Brief}}

            ## Repository context -- IMPORTANT
            Your working directory is a git worktree of the actual repo on
            branch `{{planBranch}}`. You can READ ANY FILE in this repo with
            your filesystem tools. Use that to:
              - Read existing source code so the plan reflects what's already
                there (don't invent module names that don't exist).
              - Read prior docs under `docs/` for decisions already made.
              - Reference real package names, component boundaries, and
                module structure when describing what to build.
            A plan written in isolation drifts from the codebase. A plan
            written WITH the codebase under your tools stays anchored.
            {{attachmentPromptSection}}
            ## Sibling plan documents
            The other plan docs in this story are written in PARALLEL by
            sibling architects. They live at:
            {{siblingPathsList}}

            Read whichever already exist (some may not exist yet on iter 1).
            Don't duplicate their content -- reference shared concepts by
            name. If you spot an inconsistency, note it in your Iteration
            Summary so the next iteration of EITHER doc can align.

            ## How to deliver -- CRITICAL
            You MUST invoke your filesystem write tool to save the file at
            `{{spec.RepoPath}}` (relative to working directory -- no leading
            slash, no extra path components). Content typed in your chat
            reply is NOT saved -- at end of turn the workspace is checked
            and an empty file fails this iteration with score 0.

            Concretely:
            1. For large files, prefer incremental edits (edit_file / replace)
               over rewriting the entire file. Full-file create_file calls on
               files >5 KB risk hitting output limits and producing empty files.
               Use create_file for the initial draft; use edit_file for
               subsequent iterations.
            2. Verify with your read/view tool that the file is on disk and
               its byte count matches what you intended.
            3. ONLY THEN reply with the summary below. Your chat reply is
               for the summary; the FILE is the deliverable.
            4. NEVER delete `{{spec.RepoPath}}` -- if you need to restructure,
               edit in place. Deleting the file causes the evaluator to
               score 0 and the iteration is wasted.

            End your chat reply with a `## Iteration Summary` section that
            lists (a) the path you wrote to and the byte count, (b) which
            parts of the story description the file covers, (c) any open
            questions for the next iteration. The orchestrator extracts
            this into the per-iteration UI summary.

            ## How to handle PRIOR EVALUATOR FEEDBACK -- CRITICAL
            When you're iterating on a file that already exists (iter ≥ 2),
            the evaluator's previous `## What still needs work` list is
            prepended to this prompt under "## LATEST evaluator feedback".
            Each item in that list is now formatted as a markdown
            CHECKBOX (`- [ ] N. <description>`). Score regression most
            commonly happens when you fix a symptom locally without
            auditing the doc-wide blast radius, OR when you silently
            skip items. To avoid both:

            1. **Copy every checkbox into your Iteration Summary and check
               each one off.** Add an explicit `### Prior feedback
               resolution` subsection that mirrors EVERY `- [ ]` item from
               the evaluator's prior list. For each item, replace `- [ ]`
               with `- [x]` AND append one of:
                 - `FIXED -- <section/file ref> -- <one-line description of the fix>`
                 - `DEFERRED -- <reason it's deferred and which iter / pin / question will handle it>`
                   (only when you genuinely cannot fix it this iter)
               Every checkbox MUST be marked `[x]`. An unchecked `- [ ]`
               in your summary signals you skipped the item -- the
               evaluator will hold you below pass and the convergence
               detector will fire after 3-5 iters of skipping, exiting
               the loop with `stalled-no-convergence`.

               Example:
                 Evaluator said:  `- [ ] 1. §3.1 has markdown structure error: …`
                 Your summary:    `- [x] 1. FIXED -- §3.1 -- split MessengerMessage out into its own H4 block; OperatorBinding table now sits directly under its heading. grep-checked for orphan refs.`

            2. **Doc-wide audit with LITERAL grep -- show actual output.**
               If your edit removed or renamed a phrase, you MUST verify
               by running `grep -nF "<EXACT phrase from the BEFORE state>"`
               against `{{spec.RepoPath}}` and PASTE THE ACTUAL OUTPUT
               in your `[x] FIXED` line -- NOT a regex pattern you invent,
               NOT a paraphrase, NOT a summary. The literal `grep -F`
               with the exact pre-edit phrase is the only verification
               that doesn't produce false positives.

               Required format:
                 `[x] N. FIXED -- §X.Y -- <description>. Verification:`
                 ```
                 $ grep -nF "resolve HumanAction.Value from the stored AgentQuestion JSON" docs/stories/<id>/architecture.md
                 (empty -- phrase removed)
                 ```

               Or, when SOME hits remain by design (e.g. a `> Note:`
               callout explaining the retention), paste the hits too:
                 ```
                 $ grep -nF "..." docs/stories/<id>/architecture.md
                 1234:> Note: this section intentionally retains the old term because …
                 ```

               Why this matters (qq:TELEGRAM-MESSENGER-S iter 56 case study,
               score regressed because of this exact failure mode):
                 Agent's claim: `grep-checked: no remaining deserializes.*AgentQuestion.*finds the`
                 The pattern `deserializes.*AgentQuestion.*finds the` is
                 a regex the agent INVENTED -- it doesn't match the actual
                 unfixed text "resolve `HumanAction.Value` from the stored
                 `AgentQuestion` JSON" at §3.1 line 238. The grep returned
                 empty for the invented pattern, the agent declared success,
                 and the evaluator caught the unfixed phrase next iter.
                 The literal `grep -F` of the pre-edit phrase would have
                 caught the unfixed reference immediately.

               Rules:
                 - Use `grep -F` (fixed-string), never `grep` (regex) or `grep -E`.
                 - The phrase is the EXACT string from the file BEFORE your
                   edit -- not a paraphrase, not your own description.
                 - Run against the file you edited AND its sibling plan docs
                   under `docs/stories/{{StoryPaths.Safe(spawn.StoryId)}}/`.
                 - Paste the actual output verbatim. Empty output = `(empty)`.
                 - When you legitimately retain SOME occurrences, paste the
                   surviving hits and explain inline.
                 - Example failure (qq:TELEGRAM-MESSENGER-S iter 43):
                   the agent renamed `AlertType/Summary` → `Title/Body`
                   in §4.6 but missed `alertType=Error` in §5.6's example.
                   The evaluator caught it on the next iter and the score
                   regressed from 88 → 85.

            3. **Don't introduce structural bugs while patching content.**
               When inserting a new section between existing headings,
               verify that no existing field table or block ends up
               visually under the wrong heading. After inserting a new
               `#### Foo` block, scroll past it to confirm the
               immediately-following field table still belongs to its
               original heading (not your new one).

            4. **If a checkbox you marked `[x] FIXED` in iter N re-appears
               as `- [ ]` in iter N+1's evaluator feedback**, your prior
               fix did NOT work. Stop trying the same edit shape. Try a
               structural change instead: split the section, move the
               field into a different table, escalate to the operator as
               an Open Question, or ask the operator to pin a decision.
               Three consecutive iters with the same checkbox flipping
               back to `[ ]` will trip the convergence detector and exit
               the loop.

            5. **ASCII-clean output -- NO garbled / box-drawing characters.**
               The plan doc MUST contain only printable ASCII plus standard
               markdown punctuation (`` ` `` , `*`, `#`, `-`, `>`, `|`, etc.).
               NEVER copy box-drawing characters (`─`, `│`, `┌`, `└`, `├`,
               `╔`, `║`, etc. U+2500–U+257F), BOM marks, or other non-ASCII
               decorative glyphs from source code into the plan. They render
               as gibberish (`Γöé`, `ΓöÅΓöéΓöÇ`) when the doc passes through
               Windows codepage boundaries. If source code uses box-drawing
               diagrams, redraw them with ASCII art (`+`, `-`, `|`, `\`, `/`)
               or describe the structure in prose. After writing the file,
               run: `grep -Pn '[^\x00-\x7E]' {{spec.RepoPath}}` — if ANY
               line matches, replace the non-ASCII characters before
               finishing. The evaluator will deduct points for garbled text.

            ## Open questions -- structured (REQUIRED when you have any)
            If the story description left ambiguity that you had to guess on,
            do NOT just mention it in prose. Emit a fenced JSON block right
            after `## Iteration Summary` so the operator can answer via the
            wizard and the next iteration gets a definitive answer:

            ```json open-questions
            { "openQuestions": [
                { "id": "<short-stable-slug>", "text": "<question>", "type": "choice", "choices": ["A","B","C"],
                  "explanation": "<2-4 sentence plain-language explanation of the question + any domain terms/acronyms>",
                  "choiceAnalysis": [
                    { "choice": "A", "pros": ["<short pro>", "..."], "cons": ["<short con>", "..."] },
                    { "choice": "B", "pros": ["..."], "cons": ["..."] },
                    { "choice": "C", "pros": ["..."], "cons": ["..."] }
                  ],
                  "recommendation": "A",
                  "recommendationRationale": "<why you recommend A, 1-3 sentences>" }
            ] }
            ```
            For EVERY question ALSO include `explanation` (define the terms — the
            operator may lack your context), `recommendation` (one of your
            `choices`, verbatim), `recommendationRationale`, and `choiceAnalysis`
            — ONE entry per option with concise `pros` and `cons` arrays (one
            short phrase each, not paragraphs). These power the operator's
            "Explain" popup (rendered as a pros/cons table), so make them
            self-contained — do not assume the operator has read the diff.

            Rules:
            {{QuestionOptionsRule}}
            - Re-emit the same `id` if a previous answer wasn't sufficient.
            - Omit the block entirely when you have no questions.
            - The evaluator WILL hold you below pass while any unanswered
              question exists for this file -- surfacing them is how you
              unblock yourself, not a sign of weakness.

            Reply `DONE` after the summary and stop.
            """;

        // docs/forge/e2e.md Stage 6.2 — for the e2e-scenarios.md doc,
        // inject the linter's hard requirements into the evaluator
        // prompt so the LLM has the closed-set Setup contract in-context.
        // The linter is also exposed via `E2eScenariosLinter.Validate`
        // so a future server-side gate can refuse pass on the committed
        // file regardless of the evaluator's score.
        var e2eScenariosLinterRules =
            string.Equals(spec.FileName, "e2e-scenarios.md", StringComparison.OrdinalIgnoreCase)
                ? "\n" + E2eScenariosLinter.BuildEvaluatorRulesSection() + "\n"
                : string.Empty;

        // 2026-06-04 — cross-stage explicit-constraint check for
        // implementation-plan.md. Same pattern as the e2e linter
        // injection above. v1 covers migration range pins (the
        // pri-postgres incident class); for iter ≥ 2 the linter is
        // invoked against the actual committed draft on disk so its
        // "Current violations" sub-section surfaces the live blockers
        // in-prompt. Best-effort: on first iter (no file yet) we still
        // inject the rules block so the generator sees the criteria
        // before authoring.
        var planConstraintLinterRules = string.Empty;
        if (string.Equals(spec.FileName, "implementation-plan.md", StringComparison.OrdinalIgnoreCase))
        {
            PlanConstraintLinter.LintResult? planLintResult = null;
            try
            {
                var docOnDisk = System.IO.Path.Combine(worktreePath, spec.RepoPath);
                if (System.IO.File.Exists(docOnDisk))
                {
                    var docText = await System.IO.File.ReadAllTextAsync(docOnDisk, ct);
                    planLintResult = PlanConstraintLinter.Validate(docText, workspacePath: worktreePath);
                }
            }
            catch
            {
                // best-effort: read/parse failure falls back to
                // rules-only injection rather than aborting prompt build.
            }
            planConstraintLinterRules = "\n" + PlanConstraintLinter.BuildEvaluatorRulesSection(planLintResult) + "\n";
        }

        var evaluatorPrompt = $$"""
            You are the Architect-reviewer scoring another architect's draft of
            `{{spec.RepoPath}}` for story `{{spawn.StoryId}}`.
            {{e2eScenariosLinterRules}}
            {{planConstraintLinterRules}}

            ## Story title
            {{story.Title}}

            ## Story description (operator-authored prompt)
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## File brief
            {{spec.Brief}}

            ## Repository context
            Your working directory is a git worktree of the actual repo on
            branch `{{planBranch}}`. The draft you're scoring lives at
            `{{spec.RepoPath}}`. You can also READ source files / prior
            docs / sibling plan docs to verify the draft anchors to real
            code structure (vs invented module names).
            {{sequentialDocPolicy}}
            ## Your job
            Read the draft at `{{spec.RepoPath}}` using your read-only tools
            (view / ls / grep). Do NOT modify the file. Score on:
            - Anchored to the story description AND to the real repo (no
              invented packages / modules / files; references resolve).
            - Production-quality markdown (no placeholders, no outline-only
              sections, no "TODO" markers).
            - Coverage of the file's stated brief.
            - Internal consistency (no contradictions between sections
              of THIS doc). Cross-doc consistency rules are governed by
              the "Sequential plan-doc mode" section above — read it
              and apply ONLY the upstream-doc check, never a downstream
              one.
            - CROSS-STAGE EXPLICIT CONSTRAINTS — apply the
              "CROSS-STAGE CONSTRAINT CHECK — REFUSAL CRITERIA
              (PlanConstraintLinter)" rules section injected at the
              top of this prompt. Walk every range pin in the doc and
              verify against (a) every other pin in the doc, (b) every
              `migrations/NNNN_<name>.sql` step in the doc, and (c) the
              `services/**/migrations/NNNN_*.sql` files on disk under
              your worktree. The "Current violations" sub-section of
              that rules block (when present) lists the live failures
              the mechanical linter found — every one MUST appear as a
              checkbox in "What still needs work" with the violating
              pin quoted verbatim. Generator did NOT see the live
              violations; you are the only safety net before the doc
              hits the engineer.
            - PROOF & ENVIRONMENT CONTRACT — refuse pass when:
              (a) `architecture.md` has no `## Test & Environment Contract`, or it omits an
              acceptance area that asserts persisted/external state; (b) an
              `implementation-plan.md` Test Scenario asserting persisted/external state
              ("byte-identical", "same rows", a live DB, a DSN env-var) lacks a
              `[proof: …; deps: …]` tag, tags it `in-process`, or contradicts architecture's
              contract; (c) an `e2e-scenarios.md` phase's Setup `Type` contradicts the
              proof/deps the UPSTREAM `implementation-plan.md` declared for the same scenario
              (e.g. `inline` for a scenario the plan marked `service:postgres`). Apply the
              e2e check ONLY against upstream docs.

            ## VERIFY THE GENERATOR'S `[x] FIXED` CLAIMS -- REQUIRED FOR ITER ≥ 2
            DO NOT TRUST the generator's `### Prior feedback resolution`
            block at face value. For each `[x] N. FIXED -- … grep -F "<phrase>" …`
            claim in the generator's reply, you MUST independently verify
            against the actual file:

            1. Extract the literal phrase from the generator's claim
               (the quoted string after `grep -F`).
            2. Run `grep -nF "<phrase>"` (via your shell) against
               `{{spec.RepoPath}}` and the UPSTREAM plan docs listed in
               "Sequential plan-doc mode" above. Do NOT grep downstream
               docs — hits there are pre-existing legacy text, not a
               regression in this draft.
            3. If your grep returns any hits the generator did NOT
               explicitly acknowledge, the claim is FALSE-VERIFIED.
            4. For every falsely-verified `[x]` claim, add a NEW
               checkbox to your "What still needs work" list with the
               literal grep evidence:

                 `- [ ] N. UNVERIFIED CLAIM -- generator marked item N as
                  [x] FIXED with grep-checked: "<their pattern>", but
                  actual grep -nF "<phrase>" shows: <paste the hit
                  line(s)>. Fix the missed reference and re-verify.`

            Why this matters (qq:TELEGRAM-MESSENGER-S iter 56 case study):
            the generator claimed `grep-checked: no remaining
            deserializes.*AgentQuestion.*finds the` -- a regex it invented
            that didn't match the actual unfixed phrase "resolve
            HumanAction.Value from the stored AgentQuestion JSON" at
            §3.1 line 238. Without independent re-verification by you,
            the loop would have iterated on the same phantom-fixed
            phrase indefinitely.

            Structure your `notes` field (inside the JSON block below) into
            three labeled parts in this order:
            1. **What improved this iteration** -- one bullet list. Include
               any `[x] FIXED` claims you INDEPENDENTLY VERIFIED with your
               own grep.
            2. **What still needs work** -- emit a markdown CHECKBOX list,
               one item per line, in this exact shape:
                 `- [ ] 1. <atomic issue + section/line citation>`
                 `- [ ] 2. <atomic issue + section/line citation>`
                 `- [ ] 3. …`
               Each checkbox starts `unchecked` (`[ ]`). The generator
               echoes these checkboxes into its next-iteration Iteration
               Summary and flips each to `[x]` with a FIXED / DEFERRED
               note. Keep items atomic (one issue per checkbox) -- never
               combine two issues into one line, or the generator can't
               cleanly mark just one of them as fixed.

               Include `UNVERIFIED CLAIM` items here whenever your
               re-grep contradicts the generator's claim. Cite the
               specific phrase and line(s) where your grep found hits.

               ALSO: when scoring iteration N+1, compare the prior
               iteration's checkboxes against what the generator
               claimed `[x] FIXED`. Items the generator marked FIXED but
               are STILL present in the file as you see it MUST appear
               unchecked again in your new list (cite the file evidence
               briefly so the loop's convergence detector can see the
               regression). Items the generator marked `[x] DEFERRED`
               are acceptable to omit from your next list ONLY when the
               deferral rationale is sound; otherwise re-include them.

            3. **Why this score** -- two sentences max.

            ## Garbled / non-ASCII character check
            Scan the draft for garbled or corrupted characters — sequences
            like `Γöé`, `Γöñ`, `ΓöÅ`, `Γöî`, box-drawing glyphs (`─│┌└├╔║`),
            or any multi-byte sequences that render as mojibake. These appear
            when the generator copies content through a Windows codepage
            boundary. If you find ANY, add a checkbox:
              `- [ ] N. Garbled characters at §X.Y lines NNN-NNN — replace
               box-drawing / corrupted sequences with ASCII equivalents.`
            This is a quality gate — do NOT pass a doc with garbled text.

            ## Open-questions hard gate
            If the generator surfaced open questions in its
            `## Open Questions` JSON block AND the operator hasn't answered
            them yet, you MUST return verdict `iterate` regardless of doc
            quality. The orchestrator will block pass anyway via the
            server-side gate, but your verdict needs to match so the
            iteration history reads correctly.

            ## Output

            Output **only** a fenced JSON block -- no prose outside it. Put
            your full review (including the three sections above) inside
            the `notes` field. Example shape:

            ```json
            {
              "score": 87,
              "verdict": "iterate",
              "notes": "What improved: ...  What still needs work: ...  Why this score: ..."
            }
            ```

            STRICT JSON: double-quoted keys (`"score"`, not `score`), no
            trailing commas, no `//` or `/* */` comments. If your JSON is
            malformed your entire review is discarded and the iteration is
            wasted.

            score >= 90 = pass, 70-89 = iterate (be specific in notes),
            < 70 = reject.
            """;

        // Phase 38 (Layer B.3) -- read per-doc points from the real
        // WorkItemRow created by the orchestrator before this call. Falls
        // back to story.points/4 (consistent with EnsurePlanDocWorkItemsAsync)
        // when the row is missing (older spawns until Phase38Migration runs).
        var docPoints = await this.LookupWorkItemPointsAsync(
            docWorkItemId, fallback: Math.Max(1, spawn.StoryPoints), ct);

        // Recompute budget from docPoints so the agent timeout matches
        // the work item's actual size, not the story-level points which
        // may be 0 for locally-created stories.
        var docBudget = BudgetForPhase(docPoints, $"plan:{spec.FileName}");

        // Phase 65 — track the branch HEAD seen at each iter boundary so
        // the OnIterationCommit callback can compute file-change counts
        // by diffing against the prior iter's recorded SHA. The dictionary
        // is captured by closure, scoped to this single plan-doc loop,
        // so concurrent producers (parallel-mode 4-doc fan-out) each have
        // their own. Seeded with `null` for the iter-1 baseline; first
        // call returns 0 counts but records the HEAD SHA, second call
        // diffs against it, and so on.
        string? prevIterSha = null;

        var skippedConcurrent = await this.RunGenEvalLoopAsync(
            team: "Architect",
            workItemId: docWorkItemId,
            phaseTag: $"plan:{spec.FileName}",
            generatorPrompt: generatorPrompt,
            evaluatorPrompt: evaluatorPrompt,
            workspace: worktreePath,
            budget: docBudget,
            storyPoints: docPoints,
            generatorAgentOverride: generatorAgentOverride,
            evaluatorAgentOverride: evaluatorAgentOverride,
            // Phase 41 -- revoke shell access. The architect's job is file I/O.
            disallowedTools: ArchitectDisallowedTools,
            // Phase 41 -- restore from snapshot if the generator destroys the file.
            protectedFile: diskPath,
            // Phase 42 -- commit the doc per iteration so the Worktree tab
            // shows real progress. CommitPathInWorktreeAsync is no-op when
            // content didn't change since last commit. Per-branch lock
            // serialises concurrent architects against the git index.
            onIterationCompleted: async (iter, verdict) =>
            {
                if (repo is null) return;
                await SafeCommitPlanDocIterAsync(
                    () => this.git.CommitPathInWorktreeAsync(
                        repo, planBranch, spec.RepoPath,
                        $"plan({spec.FileName}): iter {iter} ({verdict})", ct),
                    this.logger, spec.FileName, iter);

                // Phase 42 -- when the implementation-plan committed, kick the
                // reconciler so workstream rows + workstreams.json track the
                // latest plan structure (cancel obsolete, add new, re-prompt
                // in-flight). Best-effort: a reconcile failure must not break
                // the gen/eval loop; the scheduler tick will retry shortly.
                if (string.Equals(spec.FileName, "implementation-plan.md", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var rscope = this.scopeFactory.CreateScope();
                        var reconciler = rscope.ServiceProvider.GetService<PlanReconciler>();
                        if (reconciler is not null)
                        {
                            await reconciler.ReconcileAsync(spawn.Id, ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger?.LogWarning(ex,
                            "Per-iter reconcile failed for spawn {SpawnId} iter {Iter}; loop continues.",
                            spawn.Id, iter);
                    }
                }
            },
            // Phase 65 — snapshot HEAD + numstat against the prior iter's
            // SHA so the deliverable row records real file counts. Runs
            // AFTER onIterationCompleted (per DeliverableLoop ordering),
            // so CommitPathInWorktreeAsync has already landed this iter's
            // commit (if any) before we snapshot.
            onIterationCommit: async (iter, deliverableId, snapCt) =>
            {
                if (repo is null) return null;
                var snap = await this.git.SnapshotBranchHeadAsync(repo, planBranch, prevIterSha, snapCt);
                if (snap is null) return null;
                prevIterSha = snap.Sha;
                return new IterCommitInfo(
                    CommitSha: snap.Sha,
                    CommitMessage: snap.Subject,
                    FilesAdded: snap.Added,
                    FilesModified: snap.Modified,
                    FilesDeleted: snap.Deleted);
            },
            ct: ct);

        // 2026-05-20 — Phase 74 follow-up. When the gen-eval loop was a
        // no-op because another RunAsync already owns this workItemId (e.g.
        // an operator Restart racing the SpawnDependencyScheduler tick),
        // this call produced nothing. A fresh-start Restart has just
        // deleted the on-disk doc above, so the File.Exists check below
        // would throw "Architect did not produce" and HALT the orchestrator
        // on a purely transient race — even though the winning in-flight run
        // is producing the file right now (observed 2026-05-20 on
        // code-intelligence:CLEAN-CODE architecture.md immediately after a
        // Restart). Surface a typed transient signal instead; the
        // orchestrator treats it as "retry next tick", not a failure.
        if (skippedConcurrent)
        {
            throw new PlanDocInFlightException(spec.RepoPath, docWorkItemId);
        }

        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Architect did not produce '{spec.RepoPath}'. Worktree: {worktreePath}");
        }
        return await File.ReadAllTextAsync(diskPath, ct);
    }

    /// <summary>
    /// Phase 41 -- Claude-Code-SDK tool names the architect MUST NOT use when
    /// producing plan docs. Today: <c>Bash</c> only (the agent has no
    /// legitimate need for a shell when writing a markdown spec, and
    /// permitting it produced an observed delete-then-forget-to-rewrite
    /// failure mode). Reused by all four plan-doc loops; resume calls
    /// flow through <see cref="ProducePlanFileAsync"/> too.
    /// </summary>
    private static readonly string[] ArchitectDisallowedTools = new[] { "Bash" };

    /// <summary>
    /// Phase 50 -- clear the on-disk plan-doc file and purge any stale per-doc
    /// DB rows so the architect's next architect invocation regenerates the
    /// doc from scratch instead of inheriting prior content. Shared by:
    /// fresh-spawn iter 1 (count==0 + !forceResume), operator-initiated
    /// Restart (restartFromScratch=true), and boot-recovery edge cases.
    /// Extracted from <see cref="ProducePlanFileAsync"/> so the cleanup
    /// contract has a single source of truth and can be unit-tested
    /// without spinning up the full strategy + agent runner stack.
    ///
    /// <para>This runs AFTER <see cref="IStoryGitOps.EnsureWorktreeAsync"/>
    /// has set up (or restored) the worktree -- otherwise the file deletion
    /// would be undone by the worktree's <c>git checkout</c> re-pulling the
    /// file from the plan-branch tip. Best-effort: file delete failures are
    /// logged at warning level but do not throw, so a transient FS lock
    /// doesn't strand the loop; the agent's iter 1 will then edit the
    /// stale file instead of regenerating (degraded but not broken).</para>
    /// </summary>
    /// <param name="scopeFactory">DI scope factory for the one-shot purge transaction.</param>
    /// <param name="logger">Optional logger for diagnostics; helper still functions when null.</param>
    /// <param name="diskPath">Absolute path to the plan doc inside the worktree.</param>
    /// <param name="docWorkItemId">Synthetic work-item id (<c>plan-doc:{spawn.Id}:{fileName}</c>) used as the DB key for all per-doc rows.</param>
    /// <param name="restartFromScratch">When true, log as Restart; when false, log as fresh-spawn.</param>
    /// <param name="ct">Cancellation token.</param>
    // ──────────────────────────────────────────────────────────────────────
    // Phase 81/83 — per-iter commit + iteration-callback guards.
    //
    // Both swallow transient I/O exceptions so a single failed iter doesn't
    // tank the gen-eval loop, but propagate GitIgnoredPathException so a
    // misconfigured repo (e.g. OneBranch '/*' + '!/src/' allowlist routing
    // plan docs into the default docs/stories/... path) halts the orchestrator
    // at iter 1 instead of burning N iterations of agent work that can never
    // be committed. The orchestrator's outer try/catch surfaces this as an
    // 'ORCHESTRATOR HALTED' banner so the operator sees the configuration
    // error and fixes repo.PlanDocBaseDir.
    // ──────────────────────────────────────────────────────────────────────

    internal static async Task SafeCommitPlanDocIterAsync(
        Func<Task> commit, ILogger? logger, string fileName, int iter)
    {
        try
        {
            await commit();
        }
        catch (GitIgnoredPathException)
        {
            // Configuration error -- propagate so the orchestrator halts
            // BEFORE burning more iterations the operator can't recover from.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Per-iter commit failed for {File} iter {Iter}; loop continues.",
                fileName, iter);
        }
    }

    internal static async Task SafeInvokeIterationCallbackAsync(
        Func<Task> callback, ILogger? logger, string workItemId, int iter)
    {
        try
        {
            await callback();
        }
        catch (GitIgnoredPathException)
        {
            // Configuration error -- propagate. The inner SafeCommit guard
            // also re-throws this; without re-throwing here, the outer
            // wrapper would re-swallow it and the bug would persist.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "onIterationCompleted callback threw at iter {Iter} for {WorkItemId}; loop continues.",
                iter, workItemId);
        }
    }

    internal static async Task EnsureFreshDocWorkspaceAsync(
        IServiceScopeFactory scopeFactory,
        ILogger? logger,
        string diskPath,
        string docWorkItemId,
        bool restartFromScratch,
        CancellationToken ct)
    {
        var reason = restartFromScratch ? "Restart" : "fresh-spawn";
        if (File.Exists(diskPath))
        {
            try
            {
                File.Delete(diskPath);
                logger?.LogInformation(
                    "Phase 50 {Reason} cleanup: removed stale plan doc at {DiskPath} (workItem {WorkItem}) before architect invocation.",
                    reason, diskPath, docWorkItemId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Phase 50 {Reason} cleanup: could not delete stale plan doc at {DiskPath}; architect may inherit prior content.",
                    reason, diskPath);
            }
        }

        // Defensive DB sweep of anything keyed by this docWorkItemId that
        // survived an upstream cleanup (DeleteSpawn race with in-flight
        // agent, RestartArtifact endpoint racing with an agent finishing
        // mid-cancel, etc.). Everything we remove was genuinely "left
        // over" rather than freshly produced -- this runs BEFORE iter 1
        // starts, so nothing the new architect produces could land in
        // these tables yet.
        using var purgeScope = scopeFactory.CreateScope();
        var purgeDb = purgeScope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var staleDeliverables = await purgeDb.Deliverables.Where(d => d.WorkItemId == docWorkItemId).ToListAsync(ct);
        purgeDb.Deliverables.RemoveRange(staleDeliverables);
        var staleQuestions = await purgeDb.DeliverableQuestions.Where(q => q.WorkItemId == docWorkItemId).ToListAsync(ct);
        purgeDb.DeliverableQuestions.RemoveRange(staleQuestions);
        var staleTranscripts = await purgeDb.TaskTranscriptEvents.Where(e => e.WorkItemId == docWorkItemId).ToListAsync(ct);
        purgeDb.TaskTranscriptEvents.RemoveRange(staleTranscripts);
        var staleQueue = await purgeDb.TaskQueue.Where(q => q.WorkItemId == docWorkItemId).ToListAsync(ct);
        var staleQueueIds = staleQueue.Select(q => q.Id).ToList();
        if (staleQueueIds.Count > 0)
        {
            var staleQueueHist = await purgeDb.TaskQueueHistory.Where(h => staleQueueIds.Contains(h.QueueId)).ToListAsync(ct);
            purgeDb.TaskQueueHistory.RemoveRange(staleQueueHist);
        }
        purgeDb.TaskQueue.RemoveRange(staleQueue);
        if (staleDeliverables.Count > 0 || staleTranscripts.Count > 0 || staleQueue.Count > 0)
        {
            await purgeDb.SaveChangesAsync(ct);
            logger?.LogInformation(
                "Phase 50 {Reason} cleanup: purged {D} deliverables, {Q} questions, {T} transcripts, {QR} queue rows for {WorkItem}.",
                reason, staleDeliverables.Count, staleQuestions.Count, staleTranscripts.Count, staleQueue.Count, docWorkItemId);
        }
    }

    public async Task ResumeSingleArtifactAsync(
        StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
    {
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var docDir = StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId);
        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        var specs = BuildPlanDocSpecs(docDir);

        if (!specs.TryGetValue(key, out var spec))
        {
            throw new InvalidOperationException($"Unknown artifact key '{key}'. Expected one of: {string.Join(", ", specs.Keys)}");
        }

        var siblingPaths = specs.Values
            .Where(s => !string.Equals(s.FileName, spec.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.RepoPath)
            .ToArray();

        // Assign a stable generator per doc key -- uses the roster index
        // so each doc gets a different agent even on resume. The key index
        // is deterministic: architecture=0, tech-spec=1, etc.
        // Phase 47 -- load BOTH rosters (gen + eval) so a single-doc resume
        // doesn't accidentally re-pin the original (potentially still busy)
        // evaluator. The doc keeps its canonical position in both rosters so
        // a Restart of doc-3 picks the same gen+eval pair the full run would
        // have assigned to doc-3.
        var (generatorOverride, evaluatorOverride) =
            await this.LoadPlanDocAgentPairAsync(key, ct);

        // Phase 42 -- same git-worktree plumbing as the full plan run.
        // Phase 62 (operator-reported 2026-05-14 on
        // local:UPDATE-HISTORY-IN-CL → architecture.md halted at iter 11
        // with `spawn.PlanBranch` NULL but 23 iter commits on the legacy
        // shared `plan` branch) -- delegate branch resolution to
        // PlanDocStateHelpers.FindDocBranch so sequential-mode spawns
        // commit to their canonical per-doc branch
        // `story/{id}/plan-{key}` (matching what the orchestrator's
        // RunPlanDocAsync uses), not the legacy `spawn.PlanBranch ??
        // "story/{id}/plan"` shared branch. Pre-Phase-62 the worktree
        // slug was canonical (`plan-architecture`) but the branch was
        // legacy shared (`plan`), so the architect's commits landed on
        // a branch the UI / PR-open path / accept-artifact flow couldn't
        // see at the per-doc key. Legacy parallel-mode spawns
        // (PlanDocPrsJson == null) still get the shared branch via
        // FindDocBranch's legacy fallback.
        //
        // Also seen 2026-05-14 on code-intelligence:AGENT-MEMORY →
        // architecture Resume, where the legacy `story/{id}/plan` fallback
        // surfaced as `git checkout -B story/.../plan origin/story/.../plan`
        // failing with exit 128 (`fatal: 'origin/story/.../plan' is not a
        // commit`) because that combined branch never existed on origin
        // for a sequential spawn.
        var planBranch = PlanDocStateHelpers.FindDocBranch(spawn, key);
        // Phase 45 -- root the worktree at the spawn's chosen base branch
        // (defaults to main). Plan branch was branched off this base, so
        // worktree create here mirrors that ancestry.
        var worktreeBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        // Phase 55 follow-up (operator-reported 2026-05-13 on
        // local:UPDATE-HISTORY-IN-CL → architecture.md stalled at iter 13 /
        // Sequential-mode plan docs use the per-doc `plan-{key}` slug to
        // match what StorySpawnOrchestrator.RunPlanDocAsync uses; legacy
        // parallel-mode spawns share one `plan` bucket across all four
        // docs. Passing the canonical slug here makes Restart/Resume reuse
        // the exact worktree the orchestrator owns, so no migration runs.
        var worktreeSlug = PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(key)
            ? PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, key)
            : PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug();
        var worktreePath = await this.git.EnsureWorktreeAsync(
            repo, planBranch, worktreeBase, ct, worktreeSlug: worktreeSlug);

        // Phase 43 -- copy operator-uploaded attachments into the worktree.
        // Refresh from DB so attachments uploaded since the orchestrator
        // loaded `spawn` are reflected in this resume run.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(
            attachmentsJson, worktreePath, ct, stateDir: this.dbPaths.Folder);

        await this.ProducePlanFileAsync(
            spawn, story, spec, budget, forceResume: true,
            generatorAgentOverride: generatorOverride,
            evaluatorAgentOverride: evaluatorOverride,
            siblingSpecs: siblingPaths,
            worktreePath: worktreePath,
            repo: repo,
            planBranch: planBranch,
            attachmentsJson: attachmentsJson,
            ct: ct,
            restartFromScratch: restartFromScratch);
    }

    public async Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        // Phase 42 -- derive the decomposition directly from the merged
        // implementation-plan.md. The plan is the contract; the architect's
        // earlier "make up 2-6 workstreams" prompt produced output that
        // could disagree with the plan. Now we read the plan, parse its
        // Phase/Stage tree, and emit one workstream per Stage. The
        // PlanReconciler uses the same canonicalisation, so initial decomp
        // and any later reconcile pass produce identical workstream sets
        // for the same plan.
        // Phase 62 — sequential-mode spawns persist implementation-plan.md
        // on its per-doc branch `story/{id}/plan-implementation-plan`,
        // not the legacy shared `plan` branch. FindDocBranch returns the
        // right branch for both sequential and legacy modes.
        var planBranch = PlanDocStateHelpers.FindDocBranch(spawn, "implementation-plan");

        RepoRow? repo = null;
        if (!string.IsNullOrEmpty(spawn.RepoId))
        {
            using var lookup = this.scopeFactory.CreateScope();
            var control = lookup.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = lookup.ServiceProvider.GetRequiredService<ProductDbContext>();
            repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);
        }
        var planPath = $"{StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId)}/implementation-plan.md";

        string? planMd = null;
        if (repo is not null)
        {
            try { planMd = await this.git.ReadFileAsync(repo, planBranch, planPath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "ProduceDecomposition: failed to read {Path} from {Branch}; falling back to LLM-only path.",
                    planPath, planBranch);
            }
        }

        var canonical = PlanParser.Parse(planMd);
        var stages = canonical.AllStages().ToList();

        if (stages.Count > 0)
        {
            // Phase 46 -- refuse decomposition when the parser surfaced a
            // structural violation (subsection order, duplicate title).
            // The orchestrator's exception handler captures this in the
            // spawn's LastError so the operator sees the concrete code.
            if (canonical.ParseErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Plan refused at decomposition: {string.Join(", ", canonical.ParseErrors)}");
            }

            // Phase 46 -- resolve every stage's dep refs (explicit stage
            // anchors + phase-anchor cross-product expansion + title
            // fallback) into a flat set of upstream titles. Cycles in the
            // effective DAG throw plan.dep_cycle, surfaced by the caller
            // as the spawn's LastError.
            var resolved = DecompositionDepsResolver.ResolveAll(canonical);

            // Operator gate (default on): make each impl stage wait for the
            // prior stage's e2e (not just its impl PR) by rewriting impl deps
            // D → "D — E2E". The PlanReconciler applies the same post-pass so
            // re-derived DepsJson stays in lockstep. Off-switch:
            // Forge:Stories:GateImplOnPriorStageE2e=false (legacy Phase 58
            // parallel behaviour, e2e trails and gates only story completion).
            if (this.config.GetValue<bool?>("Forge:Stories:GateImplOnPriorStageE2e") ?? true)
            {
                resolved = DecompositionDepsResolver.GateImplDepsOnPriorStageE2e(canonical, resolved);
            }

            return BuildDecompositionFromPlan(canonical, resolved);
        }

        // Fallback -- plan file is missing or unparseable. Keep the original
        // LLM prompt path so the orchestrator still makes forward progress
        // (the reconciler will re-align the rows once the plan lands).
        // Phase 46 -- artefact is workstreams.yaml; legacy workstreams.json
        // files in the same workspace are migrated in place on first read.
        var workspace = this.PrepareWorkspace(spawn.Id, "decompose");
        var prompt = $$"""
            You are the Architect agent decomposing story `{{spawn.StoryId}}`
            into parallel-executable workstreams.

            ## Story title
            {{story.Title}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Your task
            Split the work into 2–6 concrete workstreams that can be assigned to
            independent engineers. Each workstream must be small enough to land
            as one PR. Set `deps` only when a workstream literally cannot start
            until another's PR has merged.

            ## How to deliver
            Use your filesystem tools to write a SINGLE file named
            `workstreams.yaml` inside your working directory. Its content must
            be a YAML sequence of mappings conforming exactly to this schema
            (camelCase keys, no nested objects beyond the top-level array):

            - title: "short imperative title"
              description: "1-3 sentences of scope"
              acceptanceCriteria:
                - "bullet"
              deps:
                - "title of upstream workstream"
              deliverables:
                - "file or artifact"
              timeline: "e.g. day 1-2"
              estimatedEffort: "e.g. S | M | L or hours"

            Do NOT write any other files. Do NOT create subdirectories. Reply
            `DONE` after writing the file and stop.
            """;

        await this.InvokeAsync("Architect", spawn.Id, "decompose", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "decompose"), ct,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        var yamlPath = Path.Combine(workspace, "workstreams.yaml");
        var legacyJsonPath = Path.Combine(workspace, "workstreams.json");

        // Phase 46 -- legacy migration. If the architect (or a previous run)
        // wrote workstreams.json, read it once, write the YAML equivalent
        // alongside, and use that going forward. Idempotent: a subsequent
        // run sees the YAML and skips JSON entirely.
        if (!File.Exists(yamlPath) && File.Exists(legacyJsonPath))
        {
            await MigrateLegacyJsonToYamlAsync(legacyJsonPath, yamlPath, spawn.Id, ct);
        }

        if (!File.Exists(yamlPath))
        {
            throw new InvalidOperationException(
                $"Architect did not write workstreams.yaml. Workspace: {workspace}");
        }
        var yamlText = await File.ReadAllTextAsync(yamlPath, ct);
        try
        {
            var rows = DecompositionYamlSerializer.Deserialize(yamlText);
            if (rows is null || rows.Count == 0)
            {
                throw new InvalidOperationException("Architect produced an empty decomposition.");
            }
            return rows.Select(r => new DecompositionWorkstream(
                Title: NonEmpty(r.Title, "untitled"),
                Description: r.Description ?? string.Empty,
                AcceptanceCriteria: (IReadOnlyList<string>)(r.AcceptanceCriteria ?? new List<string>()),
                Deps: (IReadOnlyList<string>)(r.Deps ?? new List<string>()),
                Deliverables: (IReadOnlyList<string>)(r.Deliverables ?? new List<string>()),
                Timeline: r.Timeline ?? string.Empty,
                EstimatedEffort: r.EstimatedEffort ?? string.Empty,
                // docs/forge/e2e.md Stage 1.2 — honour the architect-emitted
                // `category: qa-e2e` on the YAML row; null / impl / unknown
                // collapses to the default (impl) via CategoryFromYaml.
                // Stage 3.1 will add the trailing-qa-e2e emission to the
                // plan-anchored path above; this LLM-fallback path inherits
                // whatever the architect put in the YAML.
                Category: DecompositionYamlSerializer.CategoryFromYaml(r.Category))).ToList();
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Architect workstreams.yaml failed to parse: {ex.Message}. Workspace: {workspace}", ex);
        }
    }

    /// <summary>
    /// Phase 46 -- read a legacy <c>workstreams.json</c> and emit the
    /// equivalent <c>workstreams.yaml</c>. Records an audit row so the
    /// migration is visible in <c>/audit</c>. Best-effort: a parse
    /// failure leaves both files in place and the caller throws on the
    /// missing YAML check.
    /// </summary>
    private async Task MigrateLegacyJsonToYamlAsync(
        string legacyJsonPath, string yamlPath, string spawnId, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(legacyJsonPath, ct);
            var rows = JsonSerializer.Deserialize<List<DecomposeJsonRow>>(json, JsonOpts);
            if (rows is null || rows.Count == 0) return;

            var yaml = DecompositionYamlSerializer.Serialize(rows);
            await File.WriteAllTextAsync(yamlPath, yaml, ct);

            using var scope = this.scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Audit.Add(new AuditRow
            {
                At = DateTimeOffset.UtcNow,
                User = "system",
                Method = "MIGRATE",
                Path = $"/spawn/{spawnId}/decompose/migrate-workstreams-yaml",
                Status = 200,
                RequestBodySnippet = JsonSerializer.Serialize(
                    new { spawnId, rowCount = rows.Count, legacyJsonPath },
                    JsonOpts),
            });
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "Phase 46 -- failed to migrate legacy workstreams.json at {Path}; the YAML check that follows will throw if the architect didn't write the YAML.",
                legacyJsonPath);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Phase 45 follow-up -- engineer agent works directly in the
        // workstream branch's git worktree (not a scratch sandbox under
        // src/.story-artifacts/). This lets the agent edit existing repo
        // files in place, run real builds against the actual codebase, and
        // have its changes commit at their real repo paths. Mirrors the
        // plan-doc strategy's setup at line ~150.
        //
        // The previous design used a scratch dir + a `src/.story-artifacts/{spawn}/{ws}/`
        // path-rewrite, which made it impossible for the agent to fix
        // existing code (it could only scaffold new files in a sandbox).
        // Operator hit this on 2026-05-05: agent claimed "DONE" with code
        // changes + passing tests, evaluator scored 86 because the changes
        // never made it back to real repo paths.
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;

        // Compute the canonical engineer-workstream slug
        // (`stage-{phaseNum}.{stageNum}-{stageTitle}` for plan-anchored
        // rows, `ws-{sanitizedId}` for handcrafted ad-hoc rows). Short
        // names keep the directory under Windows MAX_PATH once cargo/
        // npm/dotnet build artifacts land underneath.
        var worktreeSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        var workspace = await this.git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch, ct, worktreeSlug: worktreeSlug);

        // Phase 55 (docs/forge/sloppy_guard.md Phase A) -- register
        // `.forge/` in the worktree-local git exclude so the agent's
        // working-notes file (`.forge/iter-notes.md`) and any other
        // scratch we write there never lands in the PR diff. Idempotent;
        // safe to call on every drive.
        await TryAddGitInfoExcludeAsync(workspace, ".forge/", ct);

        // Scrub any already-tracked `.forge/` files from the index.
        // info/exclude only prevents NEW files from being staged; files
        // committed in a prior iteration (before the exclude existed)
        // remain tracked. This `git rm --cached` unmarks them so the
        // agent's own `git add -A && git commit` won't re-include them.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git",
                "rm -r --cached --ignore-unmatch -- .forge/")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync(ct);
        }
        catch { /* best-effort */ }

        // Phase 49 follow-up -- bring the workstream branch up to date with
        // the spawn's base branch BEFORE the agent runs. Without this, a
        // workstream whose branch was created before a sibling workstream
        // merged into base inherits a stale snapshot -- observed
        // 2026-05-10 on failover-cluster:XRAFT: stage 1.2 worktree was at
        // the plan-PR commit (3acba76), stage 1.1's PR had since merged
        // into feature/xraft adding the xraft-core crate, and the
        // engineer agent saw only README + docs, not the scaffolding it
        // was supposed to extend (stalled iter 1 with score 3). The
        // post-pass merge at the orchestrator (line ~1570) ran AFTER
        // the agent finished, so the agent never saw the sibling's
        // commits.
        //
        // MergeUpstreamIntoBranchAsync is idempotent (no-op when already
        // up to date), handles fast-forward, falls back to merge --no-ff
        // with -X ours conflict resolution, and pushes the merge commit
        // back so origin/{branch} also catches up. `force: false` skips
        // the "nudge empty commit" path since no PR exists yet --
        // there's nothing to make mergeable. Best-effort: a merge
        // failure logs + continues so the agent at least runs against
        // the stale-but-known state rather than blocking the claim.
        try
        {
            await this.git.MergeUpstreamIntoBranchAsync(repo, workstream.Branch, baseBranch, force: false, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "Pre-impl base merge failed for {WsId} (branch {Branch} ← {Base}); "
                + "agent will run against the pre-merge worktree state.",
                workstream.Id, workstream.Branch, baseBranch);
        }

        // Phase 43 (extended) -- materialise operator-uploaded reference
        // documents (spec.md, design.md, acs.md, …) into the workstream
        // worktree at `.forge-attachments/` so the engineer agent's file
        // tools can read them. Refresh from DB so attachments uploaded
        // after the orchestrator's outer tick loaded `spawn` show up on
        // the next impl drive. The helper writes the exclude pattern to
        // the worktree's local `.git/info/exclude`, not the tracked
        // `.gitignore`, so the workstream PR diff stays free of unrelated
        // gitignore churn.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(
            attachmentsJson, workspace, ct, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        // Look up the work item's description -- the operator may have edited
        // it with more specific requirements (e.g. "use C# not TypeScript").
        // Falls back to workstream.Summary when no work item row exists.
        string workItemDescription;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var wi = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workstream.Id, ct);
            workItemDescription = !string.IsNullOrEmpty(wi?.DescriptionMd)
                ? wi!.DescriptionMd
                : workstream.Summary ?? string.Empty;
        }

        // Phase 45 follow-up -- bake target file paths into the prompt so the
        // engineer doesn't have to discover them in a multi-thousand-file
        // enterprise repo before running out of wall budget.
        //
        // Two passes:
        //   1) regex-extract paths from the workstream brief + summary +
        //      relevant section of the merged implementation-plan.md
        //      (cheap, deterministic, usually enough for well-decomposed
        //      stages whose plan body already names the files to touch).
        //   2) if (1) yields nothing, fall back to a small read-only
        //      architect agent that reads the same context and writes a
        //      targets.json -- DiscoverEngineerTargetsAsync. One extra LLM
        //      call before the engineer iter, but vastly faster than
        //      letting the engineer flail through ls/grep for 6 minutes.
        //
        // Targets are advisory -- the engineer can edit other files when the
        // implementation legitimately needs them -- but giving it a concrete
        // starting set converts "explore a 1000-file repo" into "open these
        // files and start editing", which fits inside the per-iter budget.
        var planStageBody = await this.ReadPlanStageBodyAsync(spawn, workstream, ct);
        var targetsFromText = ExtractTargetPaths(
            workItemDescription,
            workstream.Summary,
            planStageBody);
        IReadOnlyList<string> targets = targetsFromText;
        if (targets.Count == 0)
        {
            try
            {
                targets = await this.DiscoverEngineerTargetsAsync(
                    spawn, workstream, workItemDescription, planStageBody, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "DiscoverEngineerTargetsAsync failed for workstream {WsId}; engineer will run without target hints.",
                    workstream.Id);
                targets = Array.Empty<string>();
            }
        }
        var targetsBlock = targets.Count == 0
            ? "_No specific targets identified -- locate the relevant files via `grep`/`ls` against the workstream summary above before editing._"
            : string.Join("\n", targets.Select(t => $"- `{t}`"));

        var generatorPrompt = $$"""
            You are the Engineer agent implementing workstream `{{workstream.Id}}`
            for story `{{spawn.StoryId}}`.

            ## Story
            {{spawn.StoryTitle}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription)}}

            ## Workstream
            **{{workstream.Name}}** -- {{workstream.Summary}}
            Branch: `{{workstream.Branch}}` (branched from `{{baseBranch}}`)

            ## Detailed requirements
            {{workItemDescription}}
            {{attachmentPromptSection}}
            ## Target files (start here)
            These are the repo-relative paths the workstream's brief and the
            implementation-plan stage explicitly call out. Open them first;
            only branch out to other files when the implementation genuinely
            requires it.

            {{targetsBlock}}

            ## Repository context -- IMPORTANT
            Your working directory IS a git worktree of the actual repo, on
            branch `{{workstream.Branch}}`. You can READ AND WRITE ANY FILE in
            this repo using your filesystem tools, AND you have a real shell
            via `bash` / `run_command`. To implement this workstream:
              - **Edit existing source files in place** at the target paths
                listed above. If the workstream is "Stop body buffering in
                HttpMetricsHandler", open `HttpMetricsHandler.cs` and modify
                it directly -- do not scaffold a parallel copy.
              - Read the merged plan artifacts under
                `docs/stories/{{StoryPaths.Safe(spawn.StoryId)}}/` for context
                (architecture / tech-spec / implementation-plan / e2e-scenarios).
              - Use `grep -r 'symbol' src/` (via your shell) to locate
                additional files when the targets list isn't sufficient --
                this is far faster than walking the tree with `ls`.
              - Add new files only when the implementation genuinely needs
                a new file (e.g. a new test class, a new helper).
              - Run real builds (`dotnet build {project}.csproj`) and tests
                (`dotnet test {testproj}.csproj`) against the actual project
                files via your shell to verify your changes.

            DO NOT scaffold a sandbox copy under `src/.story-artifacts/` --
            the previous version of this prompt did that and the changes
            never landed against the real codebase. Edit real files at
            their real paths.

            ## DO NOT DELETE PRODUCTION CODE
            Files outside this workstream's stated targets MUST NOT be
            deleted. Forge enforces a hard `dotnet build` gate after every
            iteration: if your diff fails to compile (including breaks
            caused by removing types that other code references) the
            workstream is moved to `blocked` and the operator must triage.
            Three consecutive broken-build iters trips the gate early.

            Specifically:
              - DO NOT use `Remove-Item`, `rm`, `del`, `git rm`, or any
                tool that removes files under `src/`, `src/ast/`, or any
                production-code path unless the workstream brief
                **explicitly** says to delete those files.
              - If you believe an existing file is in your way, raise it
                as an Open Question at the end of your output and stop --
                do NOT act unilaterally.
              - For *integration-test* / *e2e-test* / *qa-test* / *test*
                workstreams (this one's name contains those words if so),
                your edits should land **primarily under the test project**
                (e.g. `src/CodeIntelligence.IntegrationTests/`,
                `src/*.UnitTests/`, `src/*.IntegrationTests/`). Refactoring
                production code under `src/ast/` or similar to make a test
                pass is out of scope -- propose it as a follow-up workstream
                via Open Questions.
              - The build gate applies to ALL workstreams, not just test
                stages: every iter's diff must compile.

            ## Migrating / refactoring an existing file -- EDIT IN PLACE
            When the task is to migrate, port, refactor, or rewrite a file
            that ALREADY EXISTS (most "Migrate ..."/"Refactor ..." stages),
            you MUST transform it in place with targeted `str_replace` edits.
            **NEVER delete-then-recreate an existing file.** The
            delete-first pattern is the single biggest cause of lost work
            here: if the recreate step then fails or gets cut off, the file
            is gone and the whole iteration is a destructive no-op (observed
            on `migrate-cachecontrollerintegrationtests`: the 957-line test
            was deleted, the recreate failed repeatedly, and the iteration
            ended having only deleted the file).
              - Keep the original file on disk. Change the class declaration,
                swap fields, replace inline setup, etc. via successive
                `str_replace` calls. Each edit is small and lands reliably.
              - Do NOT `Remove-Item`/`rm`/`git rm` the file first "to start
                clean". Edit it where it sits.

            ## Writing a LARGE file -- you cannot emit it in one call
            A single tool call cannot carry an entire large file as one
            argument: the model's per-turn output is capped, so a `create`
            (or a shell here-string) whose content exceeds the cap is
            **truncated** -- the content argument is dropped and the write
            fails with no error you can fix by retrying the same way.
            If you genuinely must create a NEW large file (no original to
            edit):
              1. `create` it with a FIRST CHUNK only (e.g. the header +
                 first ~150-200 lines), then
              2. APPEND the remaining sections with successive `str_replace`
                 / insert edits, ~150-200 lines per call, until complete.
              Verify with a final read that the file is whole before `DONE`.
            If a `create`/write tool call ever returns an error or you find
            the file missing/empty after writing, STOP retrying the
            identical call -- switch to the chunked approach above (or, for
            a migration, restore the original via `git checkout -- <path>`
            and edit it in place).

            ## You MUST verify your work compiles before claiming DONE
            Before replying `DONE`, run `dotnet build --nologo --verbosity minimal`
            from the worktree root and confirm exit 0. If the build fails,
            FIX it before responding -- do not claim DONE on broken code.
            The Forge build gate will reject and block the workstream
            otherwise.

            ## Don't run git yourself
            DO NOT run `git add`, `git commit`, `git push`, `git stash`,
            `git reset`, or any other git command that mutates state.
            Forge stages, commits, and pushes your changes for you after
            the evaluator scores the work. Just leave your edits as
            uncommitted changes in the worktree -- that's all Forge needs.
            (You may use read-only git commands like `git log`, `git diff`,
            `git blame` if they help you understand existing code.)

            ## Add a `.gitignore` if this is a from-scratch project
            If this workstream is bootstrapping a new project (creating
            `Cargo.toml` / `package.json` / `pyproject.toml` / `*.csproj`
            in a previously empty repo), also write a `.gitignore` at the
            repo root covering the toolchain's generated output (e.g.
            `target/` for Rust, `node_modules/` and `dist/` for Node,
            `bin/` and `obj/` for .NET, `__pycache__/` and `.venv/` for
            Python). Forge skips a small builtin list of known artifact
            dirs as a safety net, but a real `.gitignore` is the source
            of truth and keeps the PR diff focused on real source.

            ## Your task
            Produce the source-code changes that implement this workstream per
            the detailed requirements above. Use real implementation -- types,
            methods, tests -- not placeholders. Follow the repository's
            existing conventions, naming, and project layout.

            ## How to deliver
            Edit and create files in place inside your working directory
            (which is the git worktree root). Forge picks up every changed
            file via `git status` after you finish, so you don't need to
            "copy results to a workspace" -- there is no separate workspace.
            Reply `DONE` when finished.

            ## Waiting on long background work
            When you start work that runs longer than your turn (a harness-gate
            lane, a long build or test run) and have nothing else to change
            until it ends, do NOT end turns just to check on it: a turn that
            changes no file counts toward the no-progress stop. Instead, before
            `DONE`, write `.forge/waiting.json` naming each job:
            ```json
            {
              "reason": "harness-gate lanes at <sha>",
              "maxMinutes": 90,
              "jobs": [
                { "name": "windows lane", "pid": 32036 },
                { "name": "wsl lane", "log": "\\\\wsl.localhost\\Ubuntu\\home\\me\\lane.log", "doneWhen": "^exit: \\d+" }
              ]
            }
            ```
            A job is done when its Windows `pid` has exited, or when its `log`
            (absolute, or relative to the worktree) has a line matching the
            `doneWhen` regex. Forge then holds the next iteration until every
            job is done (at most `maxMinutes`, default 90, capped at 180) and
            the turn does not count as no progress. Next iteration, read the
            results and act on them; delete `.forge/waiting.json` once nothing
            is running. A declaration whose jobs are all done, or one that does
            not parse, is ignored.

            ## Working notes -- REQUIRED before `DONE`
            Before replying `DONE`, write a markdown file at the relative
            path `.forge/iter-notes.md` capturing THIS iteration's
            reflection. Each iter runs in a fresh agent session with no
            memory of what you read, edited, or decided last iter; this
            file is how you talk to your future self.

            **Forge keeps a history for you.** After every iter, Forge
            archives the current file to `.forge/notes/iter-{N}.md` and
            on the next iter prepends the FULL HISTORY (newest first,
            with `### Iteration N` headers, capped at ~16 KB) into your
            prompt under `## Notes from prior iteration(s)`. You do NOT
            need to re-paste prior decisions or dead ends into this
            iter's file -- Forge stitches them in automatically. Just
            overwrite `.forge/iter-notes.md` with what's NEW or CHANGED
            this turn; never try to "carry forward" prior content
            manually (it'll be duplicated by Forge).

            Keep it terse, bullets not prose, ~50 lines max. Cover what
            actually happened THIS iter:
              - **Files touched this iter**: paths + one-line "what changed".
              - **Decisions made this iter**: e.g. "chose
                `Mutex<RaftState>` over `RwLock` because writes dominate";
                why each non-obvious choice. Reference earlier-iter
                decisions only when you REVISED them.
              - **Dead ends tried this iter**: approaches that did not
                work this turn. (Prior iters' dead ends are visible to
                you under "Notes from prior iterations" -- don't repeat
                them; just add new ones.)
              - **Open questions surfaced this iter**: anything new you
                needed but could not determine.
              - **What's still left**: short next-iter to-do list so the
                next session can pick up where this one ended.

            The `.forge/` dir is excluded from the worktree's git index --
            these notes never land in commits.

            ## How to handle PRIOR EVALUATOR / REVIEW FEEDBACK -- CRITICAL
            When you're iterating on a workstream (iter ≥ 2), the
            evaluator's previous `## Still needs improvement` list is
            prepended to this prompt under "## LATEST evaluator feedback".
            Each item in that list is formatted as a markdown CHECKBOX
            (`- [ ] N. <description>`). Score regression and "fix one
            place, break another" failures most commonly come from
            localized edits that miss doc-wide blast radius OR from
            silently skipping items. To avoid both:

            1. **Copy every checkbox into your Change Summary and check
               each one off.** Add an explicit `### Prior feedback
               resolution` subsection in your `## Change Summary` that
               mirrors EVERY `- [ ]` item from the evaluator's prior
               list. For each item, replace `- [ ]` with `- [x]` AND
               append one of:
                 - `FIXED -- <commit / file:line / test name> -- <one-line description of the fix>`
                 - `DEFERRED -- <reason it's deferred and which workstream / iter will handle it>`
                   (only when you genuinely can't fix it this iter)
               Every checkbox MUST be marked `[x]`. An unchecked `- [ ]`
               in your summary signals you skipped the item. The
               evaluator will hold you below pass and the convergence
               detector exits the loop after 3-5 iters of skipping.

               Example:
                 Evaluator said:  `- [ ] 1. HttpMetricsHandler.cs:42 still buffers; should stream`
                 Your summary:    `- [x] 1. FIXED -- HttpMetricsHandler.cs:42 -- replaced ReadAsync into MemoryStream with CopyToAsync; added test ServerSentEventsStreamingTest.NoBuffering`

            2. **Repo-wide audit with LITERAL grep -- show actual output.**
               When your edit renames a type, method, field, config key,
               or feature flag, you MUST verify by running
               `grep -rnF "<EXACT symbol from the BEFORE state>" src/ tests/`
               and PASTE THE ACTUAL OUTPUT in your `[x] FIXED` line --
               NOT a regex pattern you invent, NOT a paraphrase. The
               literal `grep -F` with the exact pre-edit symbol is the
               only verification that doesn't produce false positives.

               Required format inside the resolution checklist:
                 `[x] N. FIXED -- <file:line> -- <description>. Verification:`
                 ```
                 $ grep -rnF "OldSymbolName" src/ tests/
                 (empty -- symbol fully removed)
                 ```

               When SOME hits remain by design (e.g. a backward-compat
               adapter), paste them and explain:
                 ```
                 $ grep -rnF "OldSymbolName" src/ tests/
                 src/Compat.cs:23: // legacy alias kept for downstream consumers
                 ```

               Rules:
                 - Use `grep -rnF` (fixed-string, recursive, with line numbers),
                   never plain `grep` or `grep -E`.
                 - The symbol/string is the EXACT identifier from the
                   code BEFORE your edit.
                 - Paste actual output verbatim. Empty output = `(empty)`.
                 - The build gate catches compile failures but NOT silent
                   stale references (e.g. a test asserting the old name
                   in a string literal, a YAML config still pointing at
                   the old key) -- only an exhaustive `grep -F` catches those.

            3. **Verify the whole test suite passes, not just the file
               you edited.** Run the workstream's project tests
               (`dotnet test {project}.csproj`) before DONE. A change
               that fixes the immediate ask but breaks a sibling test
               regresses the workstream's score (and trips the
               build/test gate).

            4. **If a checkbox you marked `[x] FIXED` in iter N re-appears
               as `- [ ]` in iter N+1's evaluator feedback**, your prior
               fix did NOT work. Stop trying the same edit shape. Try a
               structural change instead: split the change across files,
               raise it as an Open Question for operator clarification,
               or ask the operator to pin a decision. Three consecutive
               iters of the same checkbox flipping back to `[ ]` trips
               the convergence detector and stalls the workstream.

            ## Open questions -- structured (REQUIRED when you have any)
            If the workstream brief left ambiguity that forced you to guess
            (API shape, dependency choice, error-handling convention, naming),
            emit a fenced JSON block before `DONE` so the operator can answer
            via the wizard and the next iteration gets a definitive answer:

            ```json open-questions
            { "openQuestions": [
                { "id": "<short-stable-slug>", "text": "<question>", "type": "choice", "choices": ["A","B","C"],
                  "explanation": "<2-4 sentence plain-language explanation of the question + any domain terms/acronyms>",
                  "choiceAnalysis": [
                    { "choice": "A", "pros": ["<short pro>", "..."], "cons": ["<short con>", "..."] },
                    { "choice": "B", "pros": ["..."], "cons": ["..."] },
                    { "choice": "C", "pros": ["..."], "cons": ["..."] }
                  ],
                  "recommendation": "A",
                  "recommendationRationale": "<why you recommend A, 1-3 sentences>" }
            ] }
            ```
            For EVERY question ALSO include `explanation` (define the terms — the
            operator may lack your context), `recommendation` (one of your
            `choices`, verbatim), `recommendationRationale`, and `choiceAnalysis`
            — ONE entry per option with concise `pros` and `cons` arrays (one
            short phrase each, not paragraphs). These power the operator's
            "Explain" popup (rendered as a pros/cons table), so make them
            self-contained — do not assume the operator has read the diff.

            Rules:
            {{QuestionOptionsRule}}
            {{QuestionParkRule}}
            - The evaluator will hold you below pass while any unanswered
              question exists for this workstream.
            """;

        var evaluatorPrompt = $$"""
            You are scoring the Engineer agent's implementation of workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Workstream
            **{{workstream.Name}}** -- {{workstream.Summary}}

            ## Detailed requirements
            {{workItemDescription}}
            {{attachmentPromptSection}}
            ## Story
            {{spawn.StoryTitle}}
            {{NonEmpty(spawn.StoryDescription)}}

            ## Your job
            Read every file the engineer wrote inside the working directory
            (use your read-only tools -- view, ls, grep). Do NOT modify
            anything; do NOT commit; do NOT push.

            Score on:
            - Real implementation vs placeholder/scaffolding (types, methods,
              tests must be substantive).
            - Adherence to the detailed requirements above (language, contracts,
              naming conventions, deliverables specified).
            - Coverage of the workstream's stated goals + acceptance criteria.
            - Internal consistency with the story description and the merged
              planning artifacts on `main` (architecture.md / implementation-plan.md).
            - Obvious bugs, missing error paths, untested edge cases.

            The `.forge/` directory (`iter-notes.md`, `memory/*`) and an empty
            `.forge-attachments/` are private iteration scratch -- NOT
            requirements, deliverables, operator artifacts, or workstream state.
            Never raise a missing-file, missing-artifact, or "state failed"
            finding from them. Score only the changed source and test files.

            ## VERIFY THE GENERATOR'S `[x] FIXED` CLAIMS -- REQUIRED FOR ITER ≥ 2
            DO NOT TRUST the generator's `### Prior feedback resolution`
            block at face value. For each `[x] N. FIXED -- … grep -rnF "<phrase>"`
            claim in the generator's reply, you MUST independently verify
            against the actual code:

            1. Extract the literal phrase from the generator's claim
               (the quoted string after `grep -rnF`).
            2. Run `grep -rnF "<phrase>" src/ tests/` (via your shell).
            3. If your grep returns any hits the generator did NOT
               explicitly acknowledge, the claim is FALSE-VERIFIED.
            4. For every falsely-verified `[x]` claim, add a NEW
               checkbox to your "Still needs improvement" list with the
               literal grep evidence:

                 `- [ ] N. UNVERIFIED CLAIM -- generator marked item N as
                  [x] FIXED with grep-checked: "<their pattern>", but
                  actual grep -rnF "<phrase>" src/ tests/ shows: <paste
                  the hit line(s)>. Fix the missed reference and re-verify.`

            The build gate catches compile failures but NOT silent stale
            references (e.g. a test asserting the old name in a string
            literal, a YAML config still pointing at the old key). Your
            independent re-grep is the only way to catch these.

            ## Open-questions hard gate
            If the engineer surfaced open questions in their
            `## Open Questions` JSON block AND the operator hasn't answered
            them yet, you MUST score below pass and return verdict `iterate`.
            Server-side gate enforces this regardless; your verdict needs to
            match so the iteration history reads correctly.

            ## CONSTRAINT FRESHNESS CHECK BEFORE FLAGGING DRIFT
            Before flagging the impl as "drifts from the plan / spec /
            requirements", verify the **plan constraint is still consistent
            with the workspace's current reality**. The plan is a SNAPSHOT
            in time; sibling stages of the same story may have moved the
            ground truth past it (new migrations, new ENUM values, new
            tables) without the plan being updated. When that happens, the
            engineer cannot satisfy both "honor the literal pin" AND "make
            real tests pass" — and the right escalation is to stop the
            iteration and ask the operator, not to keep iterating against
            a broken contract.

            Apply this check for every plan-derived constraint you would
            cite as a "Still needs improvement" item:

            1. **Identify the plan constraint.** Quote it verbatim from
               the plan doc (e.g. *"`implementation-plan.md` Stage 6.4
               step 2 — `applies migrations 0001..0034`"*).
            2. **Verify against workspace reality.** Use your read-only
               tools to check the actual state. For migration pins:
               `ls services/*/migrations/ | sort | tail -5` (or the
               repo-appropriate equivalent). For closed sets / ENUM
               assertions: grep the actual ENUM declarations. For
               enumerations ("every `pri_*` knob"): list the actual set
               in code.
            3. **If reality EXCEEDS the plan constraint** (e.g. disk has
               migration `0038` but the pin says `0001..0034`; ENUM has
               a value the plan's closed-set doesn't list; the actual
               `pri_*` knob set is larger than the plan enumerates), the
               constraint is **STALE**. DO NOT add the resulting impl
               deviation to "Still needs improvement" as if it were the
               engineer's bug. Instead emit a structured open question
               so the orchestrator routes it to the operator:

               ```json open-questions
               { "openQuestions": [
                   {
                     "id": "stale-plan-constraint-<short-slug>",
                     "text": "Plan constraint `<verbatim pin>` in `<doc>` <section> appears stale: workspace evidence shows <counter-evidence with file path>. Should the plan be updated (operator-approved) to <proposed new value>, OR should the impl narrow its scope to honor the original pin?",
                     "type": "choice",
                     "choices": ["Update the plan to <proposed value>", "Keep the original pin; narrow the impl"],
                     "explanation": "<explain the stale pin, the counter-evidence, and what each option means — define any terms the operator may not know>",
                     "choiceAnalysis": [
                       { "choice": "Update the plan to <proposed value>", "pros": ["<short pro>"], "cons": ["<short con>"] },
                       { "choice": "Keep the original pin; narrow the impl", "pros": ["<short pro>"], "cons": ["<short con>"] }
                     ],
                     "recommendation": "<the option you recommend, verbatim>",
                     "recommendationRationale": "<why, citing the evidence>"
                   }
               ] }
               ```

               Your verdict in this iteration MUST be `iterate` (not
               `reject`) and `score` MUST stay ≥ 50 — the engineer is
               correctly blocked on an external decision and shouldn't
               be penalised as if they wrote bad code. Once the operator
               answers, a future iter sees the resolved question and
               proceeds.
            4. **If reality MATCHES the plan constraint**, then the impl
               IS drifting from a valid pin — keep the "Still needs
               improvement" item and score normally. The engineer is at
               fault.

            This check exists because of the 2026-06-04 pri-postgres
            stall: `implementation-plan.md` Stage 6.4 pinned migrations
            at `0001..0034`, but sibling stages of the same story had
            already added migrations `0035..0038` to the worktree before
            this workstream started. The engineer extended the gate to
            head (correct against reality) and the evaluator looped for
            5+ iterations flagging "contract drift" — which was real
            against the literal plan text but unsatisfiable because the
            plan text was stale. With this check, the evaluator would
            have escalated on iter 1 instead of burning the workstream's
            budget.

            ## Output

            Output **only** a fenced JSON block -- no prose outside it. Put
            your full technical feedback inside the `notes` field. Structure
            the `notes` content in this order:
            1. **Improvements this iteration** -- bullet list. Include any
               `[x] FIXED` claims you INDEPENDENTLY VERIFIED with your own
               grep.
            2. **Still needs improvement** -- emit a markdown CHECKBOX list,
               one item per line, in this exact shape:
                 `- [ ] 1. <atomic issue + file:line / function / test ref>`
                 `- [ ] 2. <atomic issue + file:line / function / test ref>`
                 `- [ ] 3. …`
               Each checkbox starts `unchecked` (`[ ]`). The generator
               echoes these checkboxes into its next-iteration Change
               Summary and flips each to `[x]` with a FIXED / DEFERRED
               note. Keep items atomic -- one issue per checkbox -- so the
               generator can cleanly mark just one of them as fixed
               without ambiguity.

               Include `UNVERIFIED CLAIM` items here whenever your
               re-grep contradicts the generator's claim. Cite the
               specific phrase and line(s) where your grep found hits.

               When scoring iteration N+1, compare the prior iteration's
               checkboxes against the actual code/tests as you see them.
               Items the generator marked `[x] FIXED` but that are STILL
               broken in the code MUST re-appear unchecked in your new
               list with a brief evidence citation so the convergence
               detector can see the regression.

            3. **Why this score** -- two sentences max.

            Example shape:

            ```json
            {
              "score": 87,
              "dims": { "correctness": 35, "quality": 22, "tests": 13, "scope": 9, "risk": 8 },
              "verdict": "iterate",
              "notes": "Improvements this iteration: ...  Still needs improvement: ...  Why this score: ..."
            }
            ```

            Dimension caps (each dim must stay within these bounds -- values
            outside the cap are silently clamped, which signals rubric drift
            and reduces your authority):
            - correctness: 0..40
            - quality:     0..25
            - tests:       0..15
            - scope:       0..10
            - risk:        0..10
            The five dim caps sum to 100; `score` must equal their sum and
            stay in 0..100.

            STRICT JSON: double-quoted keys (`"score"`, not `score`), no
            trailing commas, no `//` or `/* */` comments. If your JSON is
            malformed your entire review is discarded and the iteration is
            wasted.

            score >= 90 = pass, 70-89 = iterate (be specific in notes),
            < 70 = reject.
            """;

        // Phase 38 (Layer B.3) -- read per-workstream points from the real
        // WorkItemRow inserted by DriveDecomposeAsync. Falls back to the
        // story-level points when the row is missing (legacy spawns). Use
        // the same wsPoints for BOTH iter cap and wall-clock budget so a
        // points bump on the workstream's WI propagates to the agent's hard
        // timeout, not just the iter cap.
        var wsPoints = await this.LookupWorkItemPointsAsync(
            workstream.Id, fallback: Math.Max(1, spawn.StoryPoints), ct);

        // Phase 49 follow-up -- read the agent pair the orchestrator picked
        // at claim time (StorySpawnOrchestrator.ResolveImplPairAsync ran
        // PairDiversityResolver against the live busy set). The legacy
        // call to RunGenEvalLoopAsync without overrides made
        // workstream.AgentsJson a UI-only display field -- the actual
        // generator/evaluator always came from crew.DefaultGeneratorAgentId
        // / DefaultEvaluatorAgentId, defeating the diversity picker.
        // Pass agents[0] as the generator and agents[1] as the evaluator
        // so the orchestrator's per-WS assignment actually takes effect.
        var assignedAgents = ParseAgentsList(workstream.AgentsJson);
        string? assignedGen = assignedAgents.Count > 0 ? assignedAgents[0] : null;
        string? assignedEval = assignedAgents.Count > 1 ? assignedAgents[1] : null;

        // 2026-05-16 — for the workstream/engineer path we snapshot the
        // worktree's cumulative delta vs baseBranch each iter (see the
        // OnIterationCommit comment block below for why
        // SnapshotBranchHeadAsync was wrong here). No prevIterSha state
        // needed — each call is self-contained against baseBranch.

        var skippedConcurrent = await this.RunGenEvalLoopAsync(
            team: "Engineer",
            workItemId: workstream.Id,
            phaseTag: $"impl:{workstream.Id}",
            generatorPrompt: generatorPrompt,
            evaluatorPrompt: evaluatorPrompt,
            workspace: workspace,
            budget: BudgetForPhase(wsPoints, "impl"),
            storyPoints: wsPoints,
            generatorAgentOverride: assignedGen,
            evaluatorAgentOverride: assignedEval,
            // Phase 49 -- pass the resolved build command so the per-iter
            // gate runs the right tool for the repo's ecosystem (cargo /
            // npm / go / mvn / dotnet) instead of always invoking
            // `dotnet build`. Null = no detectable build system, gate
            // is skipped per-iter (mirrors the post-pass skip).
            buildCommand: ResolveBuildCommand(repo, workspace),
            // Phase 56 -- per-iter quality + test gates chained after
            // the build gate. Empty / null resolvers fall through to
            // skipped events. The same resolvers feed the post-pass
            // gate suite (via OnPassValidate) so per-iter and post-pass
            // run the IDENTICAL chain -- no chance of post-pass
            // discovering a failure the per-iter chain didn't catch.
            qualityCommands: ResolveQualityCommands(repo, workspace),
            testCommand: ResolveTestCommand(repo, workspace),
            // 2026-05-16 — snapshot the WORKTREE'S current delta vs
            // baseBranch (committed + uncommitted + untracked), not the
            // branch HEAD's commit-delta vs prev iter. The workstream
            // path commits ONCE at pass-time via
            // ReadCachedWorkstreamFilesAsync → CommitFilesAsync, so
            // SnapshotBranchHeadAsync would return 0,0,0 for every iter
            // (HEAD stays at baseBranch until the final pass commit) and
            // the operator sees `0f` on every iter chip even though the
            // engineer is editing 20+ files. SnapshotWorktreeChangesAsync
            // reads the live working tree and surfaces the cumulative
            // WIP per iter (iter 1: ~3 files staged so far, iter 5: 22
            // files — matching the eventual PR delta).
            onIterationCommit: async (iter, deliverableId, snapCt) =>
            {
                if (repo is null || string.IsNullOrEmpty(workspace)) return null;
                var snap = await this.git.SnapshotWorktreeChangesAsync(repo, workspace, baseBranch, snapCt);
                if (snap is null) return null;
                return new IterCommitInfo(
                    CommitSha: snap.Sha,
                    CommitMessage: snap.Subject,
                    FilesAdded: snap.Added,
                    FilesModified: snap.Modified,
                    FilesDeleted: snap.Deleted);
            },
            // Phase 59 Stage A — narrow `dotnet test` to projects
            // impacted by the engineer's diff vs. baseBranch. The
            // legacy whole-suite behaviour (which timed out at 8 min
            // on stage-1.1-base-classes-and-port-allocation 2026-05-19)
            // returns when the operator sets
            // repo.TestImpactedOnly=false OR when the global
            // Forge:Test:ImpactedOnly config key is set false.
            testBaseRef: baseBranch,
            testImpactedOnly: ResolveImpactedTestOnly(repo, this.config),
            // 2026-05-21 — honour the repo's Edit-Repo gate timeouts
            // (BuildTimeoutSec / TestTimeoutSec) instead of the hardcoded
            // 8-min default. A long integration suite was being killed at
            // 8 min (exit -1) even after the operator raised TestTimeoutSec.
            buildTimeout: TimeoutFromSeconds(repo?.BuildTimeoutSec),
            testTimeout: TimeoutFromSeconds(repo?.TestTimeoutSec),
            // 2026-05-22 — run init.cmd + restore in the same shell session
            // as build/test so the repo's env (PATH/.dev, CDP_* vars) is in
            // scope. Null for repos that configure neither (direct exec).
            initCommand: ResolveInitCommand(repo),
            restoreCommand: ResolveRestoreCommand(repo),
            // 2026-05-26 — RunTestsElevated repos run the test stage via the
            // elevated broker (Forge.Api is non-elevated). Broker unreachable
            // => the gate throws BuildFailedException => workstream stalls.
            runTestsElevated: repo?.RunTestsElevated == true,
            // 2026-06-03 — pass the operator attachment manifest so the loop
            // can scrub engineer-authored files from the operator-only
            // `.forge-attachments/` inbox before each evaluator turn (blocks
            // the self-produced gate-output evidence-gaming loop).
            operatorAttachmentsJson: attachmentsJson,
            // 2026-06-03 — merge the CURRENT origin/{baseBranch} into the
            // workstream branch at pass-time, before the post-pass gate runs.
            // Fixes the stale-base blind spot: a sibling workstream that
            // squash-merges mid-drive (e.g. #401 adding feedback_pr_record.go
            // with `const RoutePRRecords` while THIS drive's #408 adds the
            // same const in read_pr_records.go) produces NO textual conflict
            // but a compile break the per-iter gate — claimed against the old
            // base snapshot — never sees. Merging here makes the post-pass
            // build gate compile the union and demote pass→iterate so the
            // agent fixes the collision before the PR merges. force:false =>
            // clean merge only; a real textual conflict aborts (NO -X ours)
            // and is reported via task.gate.base-refresh{merged:false}, then
            // the gate proceeds on the pre-merge tree (unchanged behavior —
            // genuine conflicts are still resolved at provider-merge time).
            // Safe re: WIP because the engineer's edits are committed+pushed
            // per-iter (CommitAllAndPushAsync at the iter loop), so the
            // merge's internal reset --hard origin/{branch} loses nothing.
            refreshBaseBeforePostPassGate: async (validIter, refreshCt) =>
            {
                if (repo is null
                    || string.IsNullOrWhiteSpace(workstream.Branch)
                    || string.IsNullOrWhiteSpace(baseBranch))
                {
                    return;
                }

                var merged = await this.git.MergeUpstreamIntoBranchAsync(
                    repo, workstream.Branch, baseBranch, force: false, refreshCt);

                await this.taskChannel.EmitAsync(workstream.Id, "task.gate.base-refresh", new
                {
                    iter = validIter,
                    branch = workstream.Branch,
                    baseBranch,
                    merged,
                }, ct: refreshCt);
            },
            declaredTargets: targets,
            ct: ct);

        // Phase 74 follow-up — DeliverableLoop fast-failed because another
        // impl run already owns this work item. Do not read stale workspace
        // files, build a PR body from prior deliverables, or trip Phase 75's
        // failure-marker gate; the winning in-flight run owns the real verdict.
        if (skippedConcurrent)
        {
            throw new WorkstreamInFlightException(workstream.Id);
        }

        // Phase 45 follow-up -- collect only files the agent actually changed
        // in the worktree (via `git status --porcelain`), not every file in
        // the repo. Read each one back from disk; return the dict keyed by
        // its REAL repo-relative path. The orchestrator's `CommitFilesAsync`
        // re-materialises the same content (idempotent since files already
        // live in the worktree) and produces a single commit.
        // Phase 49 -- diff against the merge target (spawn.BaseBranch)
        // instead of origin/{ws-branch}. The workstream's contribution
        // to the PR is the cumulative delta from the branch it merges
        // into, not from its own remote tracking ref (which may be
        // pinned to an unrelated upstream like the plan PR base, as
        // observed on failover-cluster:XRAFT 2026-05-10 where the
        // ws-branch's origin shadowed the agent's commit from the
        // diff scope).
        var changedPaths = await this.git.GetChangedFilesAsync(
            repo, workspace, baseBranch: baseBranch, ct: ct);
        if (changedPaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no file changes for workstream {workstream.Id}. Worktree: {workspace}, base: {baseBranch}. " +
                $"The agent finished without editing any files in place -- check the transcript for tool-call activity.");
        }
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in changedPaths)
        {
            var diskPath = Path.Combine(workspace, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath)) continue; // staged-but-deleted edge case
            dict[rel] = await File.ReadAllTextAsync(diskPath, ct);
        }
        if (dict.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer changed {changedPaths.Count} path(s) but none were readable for workstream {workstream.Id}.");
        }

        // Phase 56 -- post-pass gate suite REMOVED from this method. The
        // gen/eval loop's pass-validator hook
        // (DeliverableLoopRequest.OnPassValidate, wired in
        // RunGenEvalLoopAsync) now owns the only invocation. If any
        // post-pass gate stage (build / quality / test) fails, the
        // validator either demotes pass→iterate and loops back (up to
        // MaxRecoveryAttempts times) or throws BuildFailedException to
        // route the workstream to stalled. By the time control reaches
        // here the gen/eval loop has already proven that the diff
        // passes ALL gates and is safe to commit; no double-check is
        // needed (and the duplicate check used to throw
        // BuildFailedException with no recovery loop, stalling
        // workstreams that the agent could have fixed in another iter).
        return dict;
    }

    public async Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
    {
        // INVARIANT (forge_context.md "Agents NEVER write outside their
        // worktree" / forge_implementation.md): a doc/code agent NEVER works
        // in a scratch dir under the Forge state folder. The reviewer runs IN
        // the workstream's git worktree on the PR branch -- the SAME worktree
        // the engineer wrote into (reuse its slug, idempotent) -- so it
        // reviews the real, checked-out file changes instead of spelunking
        // for the repo and reconstructing a diff from the shared clone root.
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        if (repo is null)
        {
            throw new InvalidOperationException(
                $"ProduceReviewCommentsAsync: no repo for spawn {spawn.Id} (RepoId '{spawn.RepoId}'); "
                + "cannot materialise the PR worktree to review against.");
        }
        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreeSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        var workspace = await this.git.EnsureWorktreeAsync(
            repo, workstream.Branch, baseBranch, ct, worktreeSlug: worktreeSlug);

        // Reviewer scratch/output stays inside the worktree under .forge/
        // (git-excluded via .git/info/exclude + scrubbed from the index,
        // exactly like .forge/iter-notes.md) so comments.json can never leak
        // into the PR.
        await TryAddGitInfoExcludeAsync(workspace, ".forge/", ct);
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));

        // Phase 43 (extended) -- surface operator attachments to the reviewer
        // so divergence between the implementation and the operator's
        // reference docs becomes a review comment. GitInfoExclude keeps the
        // copied .forge-attachments/ out of the worktree's index.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.GitInfoExclude, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var prompt = $$"""
            You are the Reviewer agent reviewing PR `{{prId}}` for workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Your working directory IS the PR's git worktree
            You are inside a git worktree of the actual repo, checked out on the
            PR branch `{{workstream.Branch}}` (base `{{baseBranch}}`). Every changed
            file is already on disk right here -- do NOT go hunting for the repo
            elsewhere and do NOT clone or cd into another directory. To see
            exactly what this PR changed:

            - `git --no-pager diff {{baseBranch}}...HEAD --stat`        (changed-file list)
            - `git --no-pager diff {{baseBranch}}...HEAD -- <path>`     (one file's diff)

            Then open the changed files in place (read-only) to review them.

            ## Story
            {{spawn.StoryTitle}}

            ## Workstream
            **{{workstream.Name}}** -- {{workstream.Summary}}
            {{attachmentPromptSection}}
            ## Round
            {{round}} (0 = first pass; if there are no real issues, return an
            empty array `[]` to approve.)

            ## Your task
            Produce review comments. Each comment must point at a real
            file/line and describe a concrete issue worth fixing -- not nits.

            ## How to deliver
            Write a single file at `.forge/comments.json` (the `.forge/` directory
            inside this worktree is git-excluded, so it will NOT be committed to
            the PR) containing valid JSON of this shape:

            [
              {
                "filePath": "src/.../somefile",
                "line": 1,
                "body": "issue + suggested fix",
                "severity": "major | minor | nit"
              }
            ]

            Empty array `[]` means approve. Reply `DONE` when finished.
            """;

        // Phase 49 follow-up -- emit transcript events under workstream.Id
        // (not spawn.Id) so the workstream's Conversation tab can render
        // them in the new Review subtab. Was emitting to spawn.Id and the
        // review activity was invisible to the operator unless they
        // queried the spawn-level transcript directly.
        await this.InvokeAsync("Review", workstream.Id, $"review:{workstream.Id}:r{round}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review", workstream.Category), ct,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        var diskPath = Path.Combine(workspace, ".forge", "comments.json");
        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Reviewer did not write .forge/comments.json. Workspace: {workspace}");
        }
        var json = await File.ReadAllTextAsync(diskPath, ct);
        try
        {
            var rows = JsonSerializer.Deserialize<List<ReviewCommentJsonRow>>(json, JsonOpts) ?? new();
            return rows.Select(r => new ReviewComment(
                FilePath: r.FilePath ?? string.Empty,
                Line: r.Line ?? 1,
                Body: r.Body ?? string.Empty,
                Severity: r.Severity ?? "minor")).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Reviewer comments.json failed to parse: {ex.Message}. Workspace: {workspace}", ex);
        }
    }

    public async Task<MergedPrScore> ScoreMergedPrAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string mergedPrId, CancellationToken ct = default)
    {
        // 2026-05-27 recovery path. The workstream's PR already merged but its
        // latest deliverable is a non-pass (typically the generator hit its
        // 24-min wall-clock budget and the loop force-scored 0). Run the
        // evaluator ONCE against the LANDED code so the work item can close on
        // a real score instead of re-driving merged work into the same
        // timeout. INVARIANT (forge_context.md): the evaluator runs IN the
        // workstream's git worktree — never a scratch dir.
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        if (repo is null)
        {
            throw new InvalidOperationException(
                $"ScoreMergedPrAsync: no repo for spawn {spawn.Id} (RepoId '{spawn.RepoId}'); "
                + "cannot materialise the worktree to score the merged PR against.");
        }

        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreeSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        // EnsureWorktreeAsync recreates the branch off base when the merged
        // workstream branch was already pruned (PrSyncJob removes it on PR
        // terminal state) — base carries the landed code either way, so the
        // evaluator always sees the merged implementation on disk.
        var workspace = await this.git.EnsureWorktreeAsync(
            repo, workstream.Branch, baseBranch, ct, worktreeSlug: worktreeSlug);

        await TryAddGitInfoExcludeAsync(workspace, ".forge/", ct);
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));

        // Best-effort changed-file hint (handles the already-merged empty-diff
        // case via the merge range / merge-commit first-parent fallback). When
        // empty, the prompt tells the evaluator to discover the diff itself.
        IReadOnlyList<string> changedFiles = Array.Empty<string>();
        try
        {
            (changedFiles, _) = await this.GetMergedPrChangedFilesFallbackAsync(workstream.Id, workspace, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "ScoreMergedPrAsync: changed-file hint failed for ws {WsId}; evaluator will self-discover.",
                workstream.Id);
        }

        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.GitInfoExclude, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var changedFilesSection = changedFiles.Count > 0
            ? "## Files this PR changed\n" + string.Join("\n", changedFiles.Take(200).Select(f => $"- `{f}`"))
            : "## Files this PR changed\n(Could not auto-resolve the changed-file list. Discover it yourself with "
              + "`git --no-pager log --oneline " + baseBranch + "..HEAD` and `git --no-pager diff` against the "
              + "merge-base, or inspect the workstream's stated deliverables in the tree.)";

        var prompt = $$"""
            You are the Evaluator scoring an ALREADY-MERGED pull request.

            PR `{{mergedPrId}}` for workstream `{{workstream.Id}}` (story
            `{{spawn.StoryId}}`) has been **merged** into `{{baseBranch}}`. Your
            job is NOT to change anything — it is to read the LANDED code and
            score how well it satisfies the workstream's goal, exactly as the
            gen↔eval loop's evaluator would have if the generator hadn't been
            cut off by its wall-clock budget.

            ## Your working directory IS a git worktree of the real repo
            You are inside a worktree checked out on `{{workstream.Branch}}`
            (base `{{baseBranch}}`), which contains the merged code. Do NOT
            clone or cd elsewhere. Inspect the actual files in place.

            ## Story
            {{spawn.StoryTitle}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}
            {{attachmentPromptSection}}
            {{changedFilesSection}}

            ## How to deliver
            Write a single file at `.forge/merged-pr-score.json` (the `.forge/`
            directory is git-excluded, so it will NOT be committed) containing a
            fenced JSON block of EXACTLY this shape:

            ```json
            {
              "score": 0,
              "dims": { "correctness": 0, "quality": 0, "tests": 0, "scope": 0, "risk": 0 },
              "verdict": "pass | iterate",
              "notes": "concrete justification: what the merged code does well, and any real gaps"
            }
            ```

            Scoring rubric (max per dim): correctness 40, quality 25, tests 15,
            scope 10, risk 10 — `score` is their sum (0–100). Score the work as
            MERGED; do not penalise it for the generator timeout. Reply `DONE`
            when the file is written.
            """;

        await this.InvokeAsync("Review", workstream.Id, $"merged-pr-rescore:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review", workstream.Category), ct,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        var diskPath = Path.Combine(workspace, ".forge", "merged-pr-score.json");
        if (!File.Exists(diskPath))
        {
            this.logger?.LogWarning(
                "ScoreMergedPrAsync: evaluator did not write .forge/merged-pr-score.json for ws {WsId} (workspace {Ws}); "
                + "returning ParseFailed so the reconciler retries next tick.",
                workstream.Id, workspace);
            return new MergedPrScore(0, new Dictionary<string, int>(), "iterate",
                "Evaluator produced no score file.", 0m, await this.ResolveReviewEvaluatorAgentIdAsync(ct), ParseFailed: true);
        }

        var raw = await File.ReadAllTextAsync(diskPath, ct);
        var parsed = EvaluatorScoreParser.Parse(raw, spawn.StoryPoints > 0 ? spawn.StoryPoints : 1);
        var evaluatorAgentId = await this.ResolveReviewEvaluatorAgentIdAsync(ct);
        if (parsed.ParseFailed)
        {
            this.logger?.LogWarning(
                "ScoreMergedPrAsync: evaluator score for ws {WsId} failed to parse ({Err}); reconciler retries next tick.",
                workstream.Id, parsed.ParseError);
            return new MergedPrScore(0, new Dictionary<string, int>(), "iterate",
                $"Evaluator score failed to parse: {parsed.ParseError}", 0m, evaluatorAgentId, ParseFailed: true);
        }

        return new MergedPrScore(
            Score: parsed.Score,
            Dims: parsed.Dims,
            Verdict: parsed.Verdict,
            Notes: string.IsNullOrWhiteSpace(parsed.Notes) ? "(no notes)" : parsed.Notes,
            EvalCostUsd: 0m,
            EvaluatorAgentId: evaluatorAgentId,
            ParseFailed: false);
    }

    /// <summary>Resolve the Review crew's evaluator/lead agent id for the
    /// merged-PR re-score deliverable's <c>EvaluatorAgentId</c> stamp.
    /// Best-effort — returns null when no review crew is configured.</summary>
    private async Task<string?> ResolveReviewEvaluatorAgentIdAsync(CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var prefix = CrewIdPrefix("Review");
            var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
                ?? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct);
            return crew?.DefaultEvaluatorAgentId ?? crew?.DefaultGeneratorAgentId ?? crew?.Lead;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
    {
        // Phase 59 — deepest defense. If the workstream's PR is already
        // merged/closed, the engineer's code change would never land
        // anywhere useful (the upstream is sealed). Short-circuit before
        // we spin up a workspace, copy attachments, and burn ~$0.10 on
        // an LLM call whose output is discarded by the orchestrator's
        // CommitFilesAsync throw. Any direct caller (admin endpoint,
        // unit tests, future code) gets the same protection.
        try
        {
            using var stalePrScope = this.scopeFactory.CreateScope();
            var stalePrDb = stalePrScope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var prIdList = ParseAgentsList(workstream.PrsJson);
            if (prIdList.Count > 0)
            {
                var firstPrId = prIdList[0];
                var prRow = await stalePrDb.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == firstPrId, ct);
                if (prRow is not null
                    && (string.Equals(prRow.Col, "merged", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(prRow.Col, "closed", StringComparison.OrdinalIgnoreCase)))
                {
                    var col = prRow.Col.ToLowerInvariant();
                    this.logger?.LogInformation(
                        "ProduceReviewFixAsync: PR {PrId} for ws {WsId} is {Col}; "
                        + "returning reply-only without invoking engineer.",
                        firstPrId, workstream.Id, col);
                    return new ReviewFix(
                        FilePath: null,
                        NewContent: null,
                        ReplyBody: $"System: PR already {col} — no fix to produce.");
                }
            }
        }
        catch (Exception probeEx) when (probeEx is not OperationCanceledException)
        {
            // Fail-open: if the probe itself errored we let the original
            // flow proceed. The orchestrator's CommitFilesAsync /
            // RunReviewFixLoopAsync gates will catch the genuinely-stale
            // case; we only want this layer to AVOID extra work in the
            // happy detection path, not BLOCK legitimate fixes.
            this.logger?.LogDebug(probeEx,
                "ProduceReviewFixAsync: stale-PR probe failed for ws {WsId}; proceeding with normal flow.",
                workstream.Id);
        }

        var workspace = this.PrepareWorkspace(spawn.Id, $"fix-{Slug(workstream.Id)}-{Guid.NewGuid():N}");
        var safePath = comment.FilePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        // Phase 43 (extended) -- refresh + copy operator attachments into the
        // scratch dir so the engineer can consult reference docs when crafting
        // the fix. Scratch dir (not a git worktree) → AttachmentExcludeMode.None.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.None, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        // 2026-05-26 — seed the file under review into the scratch workspace
        // so the engineer has immediate context. Without this, agents waste
        // 5+ min of their 8m budget hunting the filesystem for the file
        // (observed on ws stage-1.2-clusterdbstore: 4× hard timeouts at 8m
        // each, JI spent 5.5min on `Get-ChildItem -Recurse` scans of E:\
        // before locating ClusterDbStore.cs). Best-effort: any failure here
        // falls back to the legacy "engineer hunts" behaviour with a warning
        // log so the regression is observable.
        string? worktreeHintPath = null;
        string? seededFileSlice = null;
        var seededFileAbsent = false;
        try
        {
            using var wtScope = this.scopeFactory.CreateScope();
            var wtControl = wtScope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var wtDb = wtScope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var resolved = await WorktreePathResolver.ResolveForWorkstreamAsync(wtControl, wtDb, workstream.Id, ct);
            (worktreeHintPath, seededFileAbsent, seededFileSlice) = await TrySeedReviewFileAsync(
                workspace, resolved.WorktreePath, safePath, comment.Line, ct);
            if (worktreeHintPath is null)
            {
                this.logger?.LogWarning(
                    "ProduceReviewFixAsync: no worktree on disk for ws {WsId} "
                    + "(branch '{Branch}'); engineer will search blind.",
                    workstream.Id, resolved.Branch);
            }
            else if (seededFileAbsent)
            {
                this.logger?.LogWarning(
                    "ProduceReviewFixAsync: worktree {Wt} exists but file '{File}' not found; "
                    + "engineer will need to locate the file via search tools.",
                    worktreeHintPath, safePath);
            }
        }
        catch (Exception seedEx) when (seedEx is not OperationCanceledException)
        {
            this.logger?.LogWarning(seedEx,
                "ProduceReviewFixAsync: file-seed failed for ws {WsId} comment '{File}'; "
                + "engineer will search blind.",
                workstream.Id, safePath);
        }

        var fileContextSection = worktreeHintPath is not null && !seededFileAbsent
            ? $$"""

                ## File context (already seeded)
                The file under review has been COPIED into your working directory at
                the SAME relative path (`{{comment.FilePath}}`). View it directly with
                your `view` tool — there is no need to search the filesystem.

                When taking **Path A**, overwrite that seeded file in place with the
                fixed full content. When taking **Path B**, leave the seeded file
                unchanged (the orchestrator detects Path B by the absence of changes).

                Read-only reference (do NOT modify): the workstream's worktree on disk
                is at `{{worktreeHintPath}}`. Use it only if the fix needs context from
                sibling files; your edits MUST be written to your working directory,
                not to the worktree itself.
                """
            : worktreeHintPath is not null
                ? $$"""

                    ## File context
                    The file under review was NOT found at
                    `{{worktreeHintPath}}\{{comment.FilePath}}` — it may have been
                    moved or renamed. Locate it via search tools before producing a
                    fix; consider Path B (reply-only) if it no longer exists.
                    """
                : string.Empty;

        var sliceSection = !string.IsNullOrEmpty(seededFileSlice)
            ? $$"""

                ## Code around line {{comment.Line}}
                ```
                {{seededFileSlice}}
                ```
                """
            : string.Empty;

        var prompt = $$"""
            You are the Engineer agent responding to review feedback on workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Reviewer comment
            File: `{{comment.FilePath}}` line {{comment.Line}}
            Body: {{comment.Body}}
            {{attachmentPromptSection}}{{fileContextSection}}{{sliceSection}}
            ## Decide first
            Read the comment carefully. EVERY comment needs an explicit response --
            either a code change OR a written reply explaining why no change is
            needed. Silently resolving without responding is not acceptable.

            Choose ONE of two paths:

            ### Path A -- Code change
            Use this when the reviewer's feedback is correct and the file needs
            an update.

              1. Write the FIXED file at the SAME relative path as the comment's
                 file. Inside your working directory, create whatever
                 subdirectories are needed and write the FULL updated content to
                 `{{comment.FilePath}}`.
              2. Write a SECOND file at the working-directory root named
                 `reply.md` containing a 1-3 sentence reply for the reviewer
                 summarising what you changed and why.

            ### Path B -- Reply only (no code change)
            Use this when the comment doesn't require a code change -- e.g. the
            reviewer misread the diff, the behaviour is intentional, the concern
            is addressed elsewhere, or it's out of scope for this PR.

              1. **Do NOT write** the fixed file at `{{comment.FilePath}}`.
              2. Write `reply.md` at the working-directory root containing a 1-3
                 sentence reply explaining WHY no change is needed. Be specific
                 -- point at the existing code / test / doc that already addresses
                 the concern, or explain the design choice.

            `reply.md` is **mandatory** in BOTH paths.

            Reply `DONE` when finished.
            """;

        // Phase 49 follow-up -- emit fix events under workstream.Id so the
        // engineer's iteration on review feedback appears in the same
        // Conversation tab as the original impl work.
        //
        // Phase 65 — dispatch to the workstream's assigned generator (the
        // engineer who originally wrote the code) instead of the crew-wide
        // DefaultGeneratorAgentId. The authoring agent id flows from the
        // orchestrator's AgentsJson[0] → ReviewCommentContext.AuthoringAgentId.
        // Fall back to AgentsJson[0] directly when AuthoringAgentId is empty
        // (e.g. synthetic callers), then to crew default as last resort.
        var assignedGen = ParseAgentsList(workstream.AgentsJson).FirstOrDefault();
        var fixAgentId = !string.IsNullOrEmpty(comment.AuthoringAgentId)
            ? comment.AuthoringAgentId
            : assignedGen;
        await this.InvokeAsync("Engineer", workstream.Id, $"review-fix:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review-fix", workstream.Category), ct, agentIdOverride: fixAgentId,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        // reply.md is mandatory -- every comment needs a written response.
        var replyFile = Path.Combine(workspace, "reply.md");
        if (!File.Exists(replyFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write reply.md. Workspace: {workspace}. " +
                $"reply.md is mandatory for both code-change and reply-only paths.");
        }
        var reply = (await File.ReadAllTextAsync(replyFile, ct)).Trim();

        // Path A (code change) vs Path B (reply only) -- determined by
        // whether the engineer wrote the fixed file. Empty / missing →
        // reply-only path; present → commit the file content.
        var fixedFile = Path.Combine(workspace, safePath);
        if (File.Exists(fixedFile))
        {
            var content = await File.ReadAllTextAsync(fixedFile, ct);
            return new ReviewFix(comment.FilePath, content, reply);
        }
        return new ReviewFix(FilePath: null, NewContent: null, ReplyBody: reply);
    }

    /// <summary>
    /// Phase 79 — agent-driven merge-conflict resolver. The Engineer agent
    /// reads each conflicted file's on-disk content (markers preserved),
    /// the workstream's task description + recent deliverable narrative
    /// (so it knows its own intent), and a tail of the upstream change
    /// summary, then writes a resolved version of each file into the
    /// workspace. Returns path → resolved content for
    /// <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/> to apply.
    ///
    /// <para>The agent runs OUTSIDE the worktree (in a scratch workspace)
    /// so accidental git ops can't corrupt the in-progress merge.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
        StorySpawnRow spawn,
        WorkstreamRow workstream,
        IReadOnlyList<ConflictedFile> conflictedFiles,
        string baseBranch,
        CancellationToken ct = default)
    {
        if (conflictedFiles.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var workspace = this.PrepareWorkspace(spawn.Id, $"conflict-{StoryPaths.Safe(workstream.Id)}");

        // Seed the workspace with each conflicted file's current (markers-
        // included) content. The agent edits these in place and writes a
        // resolved.{path} alongside, OR overwrites the original. We
        // accept both shapes and read whichever exists.
        var fileIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cf in conflictedFiles)
        {
            var safePath = cf.Path.Replace('/', Path.DirectorySeparatorChar);
            var diskPath = Path.Combine(workspace, safePath);
            var dir = Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(diskPath, cf.Content, ct);
            fileIndex[cf.Path] = diskPath;
        }

        // Build a per-file listing for the prompt. Each entry includes
        // the path + first ~120 lines of conflict markers so the agent
        // can plan from the prompt without re-reading every file.
        var listing = new StringBuilder();
        foreach (var cf in conflictedFiles)
        {
            listing.AppendLine($"### `{cf.Path}`");
            var lines = cf.Content.Split('\n');
            var preview = string.Join("\n", lines.Take(120));
            listing.AppendLine("```");
            listing.AppendLine(preview);
            listing.AppendLine("```");
            if (lines.Length > 120)
            {
                listing.AppendLine($"(... {lines.Length - 120} more lines truncated in this prompt preview — read the file from disk for the full content.)");
            }
            listing.AppendLine();
        }

        // Workstream intent — try the last 2 deliverable Notes so the
        // agent knows what its workstream was trying to do. Out-of-scope
        // failures here (DB error, no rows) fall back to summary-only.
        var intentSection = await this.BuildWorkstreamIntentSectionAsync(workstream, ct);

        var prompt = $$"""
            You are the Engineer agent resolving merge conflicts for
            workstream `{{workstream.Id}}` ({{workstream.Name}}).

            Your task: merging `{{baseBranch}}` into this workstream's
            branch produced conflict markers in the files listed below.
            Read each conflicted file, understand both sides
            ("<<<<<<< ours" = this workstream's intent;
            ">>>>>>> theirs" = the upstream change on `{{baseBranch}}`
            since this workstream diverged), then WRITE THE RESOLVED
            CONTENT BACK to the same file path in this workspace.
            Remove ALL conflict markers. Preserve the semantic intent
            of BOTH sides whenever possible:

            - If both sides made independent additions (different
              functions, different doc paragraphs), keep both.
            - If both sides modified the same logic, prefer the
              upstream version UNLESS your workstream's change is a
              strict superset — in which case keep yours, but rebase
              it onto the upstream behaviour.
            - If a conflict appears in a lockfile (`*.lock`,
              `package-lock.json`, `Cargo.lock`, `yarn.lock`,
              `Gemfile.lock`, `poetry.lock`, `pnpm-lock.yaml`), DO
              NOT hand-edit; just write a single line "REGENERATE"
              and the orchestrator will regenerate it via the repo's
              restore command.
            - If the conflict is in a generated file or a binary, DO
              NOT attempt resolution; write "REGENERATE" and let the
              orchestrator handle it.

            Do not commit. Do not run git. Just write resolved file
            contents in place.

            ## Workstream intent
            {{intentSection}}

            ## Conflicted files
            {{listing.ToString().TrimEnd()}}

            When you're done, exit with a single message confirming
            which files you resolved.
            """;

        // Phase tag mirrors `review-fix:{wsId}` for symmetry with the
        // dispatcher's per-workItemId discriminator.
        var engineerId = ParseAgentsList(workstream.AgentsJson).FirstOrDefault();
        await this.InvokeAsync(
            "Engineer", workstream.Id, $"conflict-resolve:{workstream.Id}",
            prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review-fix", workstream.Category),
            ct,
            agentIdOverride: engineerId,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        // Read back each conflicted file. Per-file strategy table:
        // "REGENERATE" sentinel → caller's responsibility (orchestrator
        // runs the repo's restore command). Everything else is the
        // agent's resolution.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cf in conflictedFiles)
        {
            var diskPath = fileIndex[cf.Path];
            string content;
            try { content = await File.ReadAllTextAsync(diskPath, ct); }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex,
                    "Phase 79: could not read resolved file '{Path}' from workspace '{Wt}'; falling back to ours-side strip.",
                    cf.Path, workspace);
                content = FallbackStripMarkersOurs(cf.Content);
            }
            // Defensive: if the agent left markers in place, fall
            // back to ours-side strip so the rebase isn't permanently
            // stuck on the same conflict.
            if (content.Contains("<<<<<<<", StringComparison.Ordinal)
                || content.Contains(">>>>>>>", StringComparison.Ordinal))
            {
                this.logger?.LogWarning(
                    "Phase 79: agent left conflict markers in '{Path}'; falling back to ours-side strip.",
                    cf.Path);
                content = FallbackStripMarkersOurs(cf.Content);
            }
            result[cf.Path] = content;
        }
        return result;
    }

    private async Task<string> BuildWorkstreamIntentSectionAsync(WorkstreamRow workstream, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var recentNotes = await db.Deliverables
                .AsNoTracking()
                .Where(d => d.WorkItemId == workstream.Id)
                .OrderByDescending(d => d.Iter)
                .Take(2)
                .Select(d => new { d.Iter, d.Score, d.Verdict, d.Notes, d.GeneratorSummary })
                .ToListAsync(ct);
            if (recentNotes.Count == 0)
            {
                return $"- Name: {workstream.Name}\n- Summary: {workstream.Summary}";
            }
            var sb = new StringBuilder();
            sb.AppendLine($"- Name: {workstream.Name}");
            sb.AppendLine($"- Summary: {workstream.Summary}");
            foreach (var n in recentNotes)
            {
                sb.AppendLine();
                sb.AppendLine($"### iter {n.Iter} ({n.Verdict}, score {n.Score})");
                var trimmed = (n.GeneratorSummary ?? n.Notes ?? string.Empty).Trim();
                if (trimmed.Length > 1500) trimmed = trimmed[..1500] + "…";
                sb.AppendLine(trimmed);
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return $"- Name: {workstream.Name}\n- Summary: {workstream.Summary}";
        }
    }

    private static string FallbackStripMarkersOurs(string content)
    {
        var sb = new StringBuilder();
        var inConflict = false;
        var keepOurs = true;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                inConflict = true; keepOurs = true;
                continue;
            }
            if (line.StartsWith("=======", StringComparison.Ordinal))
            {
                keepOurs = false;
                continue;
            }
            if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                inConflict = false; keepOurs = true;
                continue;
            }
            if (inConflict && !keepOurs) continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — LLM-driven per-stage qa-e2e producer.
    /// Resolves the prompt inputs (impl-workstream diff via git, parent
    /// stage's <c>### Test Scenarios</c> via the qa-e2e WorkItem's
    /// description, setup block via <see cref="E2eScenariosSetupReader"/>),
    /// composes the QA-crew prompt via <see cref="BuildQaE2ePrompt"/>,
    /// and dispatches the standard gen↔eval loop with a
    /// <c>dotnet test --filter Category=e2e</c> post-pass gate so a
    /// qa-e2e PR cannot open on broken / failing-test code.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        if (workstream.Category != WorkstreamCategories.QaE2e)
        {
            throw new InvalidOperationException(
                $"ProduceStageE2eAsync invoked on workstream {workstream.Id} with Category="
                + $"'{workstream.Category}' (expected '{WorkstreamCategories.QaE2e}').");
        }

        var inputs = await this.ResolveQaE2ePromptInputsAsync(spawn, workstream, ct);

        // docs/forge/e2e.md Stage 4.3 — defense-in-depth: Stage 3.1's
        // BuildDecompositionFromPlan never emits a qa-e2e row for a stage
        // with zero TestScenarios, so this path shouldn't be reachable
        // in production. The guard catches a hand-crafted bad spawn
        // (e.g. the operator restarting a qa-e2e workstream after editing
        // the plan to remove its test scenarios) — throwing here marks
        // the row stalled with a clear LastError, beats silently emitting
        // empty `.feature` files.
        if (inputs.TestScenarios.Count == 0)
        {
            throw new InvalidOperationException(
                $"qa-e2e workstream {workstream.Id} has zero acceptance test scenarios; "
                + "Stage 3.1's decomposer should have skipped emission. Re-decompose the plan "
                + "or remove this stalled qa-e2e row.");
        }

        var generatorPrompt = BuildQaE2ePrompt(inputs, evaluator: false);
        var evaluatorPrompt = BuildQaE2ePrompt(inputs, evaluator: true);

        // docs/forge/e2e.md Stage 4.3 — the post-pass test gate runs the
        // ecosystem's native e2e command (godog `go test -tags e2e` for Go,
        // `cargo test --features e2e` for Rust, `pytest -m e2e` for Python,
        // `npm run test:e2e` for Node, `mvn test -Dgroups=e2e` for Maven,
        // Reqnroll `dotnet test` for .NET). The matching generator prompt
        // (BuildQaE2eGeneratorTask) instructs the agent to make the suite
        // runnable under exactly this command, so gate + prompt stay in sync.
        var e2eTestCommand = E2eTestCommandForEcosystem(inputs.Ecosystem);

        // INVARIANT (forge_context.md "Agents NEVER write outside their worktree"):
        // the qa-e2e producer runs IN the workstream's real git worktree — NOT a
        // `storyworkspaces` scratch — so the e2e test compiles and runs against the
        // REAL, dependency-complete repo (its impl sibling + every upstream stage
        // already merged into base). Mirrors ProduceWorkstreamChangesAsync /
        // ProduceReviewCommentsAsync.
        //
        // Pre-2026-05-30 this used PrepareWorkspace — an EMPTY scratch with no repo
        // packages — which forced the agent to either reimplement its dependencies
        // locally (hollow self-contained test, capped ~78 by the evaluator) or
        // `git clone` the repo into the scratch (1100-file pollution → stall). Both
        // failure modes are removed by checking out the real worktree here.
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreeSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        var workspace = await this.git.EnsureWorktreeAsync(
            repo, workstream.Branch, baseBranch, ct, worktreeSlug: worktreeSlug);

        // Agent scratch (.forge/) stays git-excluded so it never lands in the PR.
        await TryAddGitInfoExcludeAsync(workspace, ".forge/", ct);

        // Bring the branch up to the current base BEFORE the agent runs so the e2e
        // sees every dependency a sibling/upstream workstream merged after this
        // branch was cut (idempotent; the same fix the impl path applies before its
        // agent runs — see ProduceWorkstreamChangesAsync §"Phase 49 follow-up").
        // This is what makes `internal/cli/walk`-style impl packages present at
        // e2e-authoring time instead of "absent in my checkout".
        await this.git.MergeUpstreamIntoBranchAsync(repo, workstream.Branch, baseBranch, ct: ct);

        // Phase 43 (extended) — operator-uploaded attachments may name
        // the specific contracts/types the e2e test must exercise.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.None, stateDir: this.dbPaths.Folder);

        var wsPoints = await this.LookupWorkItemPointsAsync(
            workstream.Id, fallback: Math.Max(1, spawn.StoryPoints), ct);

        // Honour the same agent assignment plumbing the impl path uses so
        // the workstream-bound (generator, evaluator) pair from
        // ResolveImplPairAsync actually drives the loop.
        var assignedAgents = ParseAgentsList(workstream.AgentsJson);
        var assignedGen = assignedAgents.Count > 0 ? assignedAgents[0] : null;
        var assignedEval = assignedAgents.Count > 1 ? assignedAgents[1] : null;

        var skippedConcurrent = await this.RunGenEvalLoopAsync(
            team: "QA",
            workItemId: workstream.Id,
            phaseTag: $"qa-e2e:{workstream.Id}",
            generatorPrompt: generatorPrompt,
            evaluatorPrompt: evaluatorPrompt,
            workspace: workspace,
            budget: BudgetForPhase(wsPoints, "qa-e2e"),
            storyPoints: wsPoints,
            generatorAgentOverride: assignedGen,
            evaluatorAgentOverride: assignedEval,
            // qa-e2e produces the feature + step-def + (optional) pipeline
            // YAML stubs as side-effects in the workspace. The strategy
            // then reads the workspace via ReadAllFiles. The post-pass gate
            // runs the ecosystem's e2e test command (see e2eTestCommand)
            // against the repo so a broken test re-enters the loop with
            // the failure as evaluator feedback. Skipping the per-iter
            // build/quality gates — the e2e's contract is the e2e TEST itself
            // (which now compiles against the real, dependency-complete worktree),
            // not a full rebuild of the whole repo (that's the impl sibling's
            // gate) — leaves the test gate as the sole post-pass check.
            buildCommand: null,
            qualityCommands: null,
            testCommand: e2eTestCommand,
            onIterationCommit: null,
            // 2026-05-21 — honour the repo's Edit-Repo gate timeouts here
            // too. The qa-e2e post-pass test gate is the sole gate on this
            // path; without forwarding the operator's TestTimeoutSec a long
            // e2e suite is killed at the hardcoded 8-min default (exit -1),
            // the same bug the impl path hit on
            // stage-4.2-migrate-cachecontrollerintegrationtests (2026-05-21).
            buildTimeout: TimeoutFromSeconds(repo?.BuildTimeoutSec),
            testTimeout: TimeoutFromSeconds(repo?.TestTimeoutSec),
            // 2026-05-22 — run init.cmd + restore in the same shell session
            // as build/test so the repo's env (PATH/.dev, CDP_* vars) is in
            // scope. Null for repos that configure neither (direct exec).
            initCommand: ResolveInitCommand(repo),
            restoreCommand: ResolveRestoreCommand(repo),
            ct: ct);

        // Phase 74 follow-up — DeliverableLoop fast-failed because another qa-e2e
        // run already owns this work item. Do NOT collect the (now-empty) diff or
        // trip the empty-diff guard below, which would mark this workstream stalled
        // and clobber the winning in-flight run's committed e2e. Observed 2026-05-31
        // on code-intelligence:REPO-SCANNER diagram-projector stage-buildmodulediagram-e2e
        // + stage-buildcallchain-bfs-e2e: both had VALID committed e2e tests
        // ([e2e] commits ahead of base, builds clean) but the losing concurrent run
        // reached GetChangedFilesAsync against a tree the winner hadn't committed yet,
        // saw 0 files, and threw "qa-e2e agent produced no file changes" → stalled.
        // The auto-heal only recovers pass-stalled rows (priorVerdict=pass); a loser
        // that throws BEFORE a pass deliverable is recorded escapes it → permanent stall.
        // Mirror the impl path (ProduceWorkstreamChangesAsync, line ~4062): yield to the
        // in-flight owner via WorkstreamInFlightException, which the orchestrator treats
        // as a transient no-op (NOT a failed attempt, NOT a stall).
        if (skippedConcurrent)
        {
            throw new WorkstreamInFlightException(workstream.Id);
        }

        // Collect the agent's contribution as the git diff vs the merge target —
        // NOT a full directory walk. ReadAllFiles was correct only for the old
        // EMPTY-scratch model; in a real worktree it would return the entire repo.
        // Mirrors ProduceWorkstreamChangesAsync: diff vs baseBranch so the captured
        // set is exactly what this workstream adds on top of every merged
        // dependency. This also structurally prevents the old "silent drop" of
        // impl code — whatever the agent changed in the worktree (test artifacts
        // AND any in-place code edits) is precisely what gets committed.
        var changedPaths = await this.git.GetChangedFilesAsync(
            repo, workspace, baseBranch: baseBranch, ct: ct);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in changedPaths)
        {
            // Agent scratch / input context / build output — never deliverables.
            if (rel.StartsWith(".forge/", StringComparison.Ordinal)) continue;
            if (rel.StartsWith(".forge-attachments/", StringComparison.Ordinal)) continue;
            if (BuildArtifactPaths.IsBuildArtifact(rel)) continue;
            var diskPath = Path.Combine(workspace, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath)) continue; // staged-but-deleted edge case
            files[rel] = await File.ReadAllTextAsync(diskPath, ct);
        }
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"qa-e2e agent produced no file changes for workstream {workstream.Id}. "
                + $"Worktree: {workspace}, base: {baseBranch}. The agent finished without writing "
                + "any test artifacts in place — check the transcript for tool-call activity.");
        }

        // Sanity cap — a real qa-e2e stage touches a handful of files (one
        // `.feature`, a few step-def files, optional setup/base). A diff far
        // beyond that is scope creep or an errant bulk edit; fail fast with an
        // actionable reason rather than committing a sprawling PR. (The old
        // "scratch polluted with a cloned repo copy" failure mode is gone now
        // that we capture the git diff in a real worktree instead of walking a
        // scratch dir — but the cap remains a cheap guard against a runaway diff.)
        if (files.Count > QaE2eProducerFileCountCap)
        {
            throw new InvalidOperationException(
                $"qa-e2e agent changed {files.Count} files (cap={QaE2eProducerFileCountCap}) for workstream {workstream.Id} — "
                + "far beyond the handful a single test stage should touch (likely scope creep or a bulk edit). "
                + $"Worktree: {workspace}, base: {baseBranch}.");
        }

        // docs/forge/e2e.md Stage 7.3 — fold the deterministic
        // appsettings template (per-phase) and the per-spawn E2eBase.cs
        // into the LLM output before returning. These two files are
        // pure templates that don't benefit from agent generation; the
        // shared renderer guarantees byte-identical output to the
        // deterministic test strategy. TryAdd's atomic CAS picks the
        // first qa-e2e workstream per spawn to carry the base class.
        // Any path the LLM happened to emit at the same key gets
        // overwritten by the deterministic template (last-write-wins
        // on the merged dict) so the operator's appsettings/baseclass
        // contract stays canonical.
        var mergedFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);

        // .NET/Reqnroll harness only — fold in the deterministic Reqnroll base
        // class + per-phase appsettings template + a directory-scoped
        // `.gitignore` (bin/obj) so a developer's local build never
        // re-introduces the build-artifact leak (fix #4; additive, never
        // touches the repo-root `.gitignore`).
        //
        // Every NON-.NET harness (go/rust/python/node/maven) has NO
        // deterministic templates to merge: the agent emits its artifacts
        // (`.feature` + step defs / specs) INSIDE the owning module/crate/
        // package, whose path is repo-relative and can't be hardcoded to a
        // repo-root location (monorepos put `go.mod` / `Cargo.toml` /
        // `package.json` under e.g. `services/X/`). Folding the C# base class
        // into those outputs is exactly the cross-language scaffolding bug this
        // dispatch fixes. The ReadAllFiles filter + commit-time scrub remain the
        // build-artifact guard for those ecosystems.
        var ecoLower = (inputs.Ecosystem ?? string.Empty).ToLowerInvariant();
        var usesDotnetHarness = ecoLower is not ("go" or "rust" or "python" or "node" or "maven");
        if (usesDotnetHarness)
        {
            var includeBase = this.e2eBaseEmittedForSpawn.TryAdd(spawn.Id, 0);
            foreach (var (path, body) in E2eBaseRenderer.RenderAll(
                spawn.StoryId, inputs.PhaseSlug, inputs.SetupType, includeBaseClass: includeBase))
            {
                mergedFiles[path] = body;
            }
            mergedFiles[E2eBaseRenderer.GitignorePathFor()] = E2eBaseRenderer.RenderGitignore();
        }

        return mergedFiles;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — pure input record for the QA-crew
    /// prompt. Built by <see cref="ResolveQaE2ePromptInputsAsync"/>;
    /// consumed by <see cref="BuildQaE2ePrompt"/>. Pulled out into a
    /// record so unit tests can construct it directly without standing
    /// up git / DB.
    /// </summary>
    internal sealed record QaE2ePromptInputs(
        string StoryId,
        string StoryTitle,
        string StoryDescription,
        string WorkstreamId,
        string WorkstreamName,
        string PhaseSlug,
        string StageSlug,
        IReadOnlyList<string> TestScenarios,
        string ImplDiff,
        E2eSetupType SetupType,
        string SetupBlockMarkdown,
        string AttachmentPromptSection,
        // docs/forge/e2e.md Stage 4.3 — the target repo's detected ecosystem
        // (`go` | `dotnet` | …, from BuildSystemDetector). Drives which test
        // harness the generator emits: `go` → godog (Gherkin `.feature` +
        // `*_test.go` step defs + `go test`); everything else → Reqnroll /
        // .NET (the historical default). Defaults to `dotnet` so existing
        // callers / unit fixtures keep the prior behaviour.
        string Ecosystem = "dotnet",
        // Phase 94 — whether to emit BDD/Gherkin acceptance tests (a `.feature`
        // file + step bindings) vs plain idiomatic test methods (one per
        // scenario, no `.feature`). Resolved from the repo's
        // `E2eUseBddPattern` override falling back to the global
        // `Forge:Stories:E2eUseBddPattern` (default true). Defaults to `true`
        // so existing callers / unit fixtures keep the BDD behaviour.
        bool UseBddPattern = true,
        // Phase 94 — the .NET unit-test framework the dotnet harness binds to,
        // detected from the target repo so the generated suite matches the
        // repo's existing convention instead of defaulting to xUnit. One of
        // `mstest` | `xunit` | `nunit`. Only consulted for the dotnet
        // ecosystem; defaults to `mstest` (the repo-wide convention here).
        string DotnetTestFramework = "mstest");

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — gather everything the QA-crew
    /// prompt needs from git / DB / the in-memory <see cref="WorkstreamRow"/>
    /// shape. Surfaces as a method (instead of a closure) so the
    /// integration test can drive it directly.
    /// </summary>
    internal async Task<QaE2ePromptInputs> ResolveQaE2ePromptInputsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct)
    {
        var phaseSlug = ExtractPhaseSlugFromQaE2eAnchor(workstream.PlanAnchor);
        var stageSlug = ExtractStageSlugFromQaE2eAnchor(workstream.PlanAnchor);

        // Test scenarios + setup come from the parent qa-e2e WorkItem's
        // DescriptionMd which the reconciler populates from the parsed
        // plan stage. Fallback to the workstream's own Summary when no
        // matching WorkItem exists.
        var scenarios = await this.ReadQaE2eTestScenariosAsync(workstream.Id, workstream.Summary, ct);

        // Read e2e-scenarios.md from main (or the spawn's base branch)
        // for the parent phase's setup block.
        var (setupType, setupBlock) = await this.ReadE2eScenariosSetupAsync(spawn, phaseSlug, ct);

        // Pull the impl workstream's diff via git so the QA agent knows
        // what the test must exercise.
        var implDiff = await this.ReadImplWorkstreamDiffAsync(spawn, workstream, ct);

        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        // docs/forge/e2e.md Stage 4.3 — detect the target repo's ecosystem so
        // the prompt emits the matching e2e harness (godog for Go, Reqnroll
        // for .NET, …) instead of always scaffolding a C# project.
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var ecosystem = ResolveRepoEcosystem(repo);

        // Phase 94 — repo override wins over the global default; the global
        // default is itself true (BDD/Gherkin) unless the operator turns it
        // off in appsettings. Mirrors the GateImplOnPriorStageE2e config shape.
        var useBdd = ResolveE2eUseBddPattern(repo, this.config);

        // Phase 94 — detect the repo's existing .NET test framework so the
        // dotnet harness matches it (MSTest here) instead of free-choosing
        // xUnit. No-op for non-dotnet ecosystems (the token is ignored there).
        var dotnetFramework = ResolveDotnetTestFramework(repo);

        return new QaE2ePromptInputs(
            StoryId: spawn.StoryId,
            StoryTitle: spawn.StoryTitle ?? string.Empty,
            StoryDescription: NonEmpty(spawn.StoryDescription),
            WorkstreamId: workstream.Id,
            WorkstreamName: workstream.Name,
            PhaseSlug: phaseSlug,
            StageSlug: stageSlug,
            TestScenarios: scenarios,
            ImplDiff: implDiff,
            SetupType: setupType,
            SetupBlockMarkdown: setupBlock,
            AttachmentPromptSection: attachmentPromptSection,
            Ecosystem: ecosystem,
            UseBddPattern: useBdd,
            DotnetTestFramework: dotnetFramework);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — resolve the target repo's ecosystem for
    /// qa-e2e harness selection. Precedence: (1) the operator's
    /// <see cref="RepoRow.TestCommand"/> override (its leading tool names the
    /// stack), (2) a <see cref="BuildSystemDetector.Detect"/> scan of the
    /// clone root, (3) <c>dotnet</c> as the back-compat default so an
    /// unrecognised repo keeps the historical Reqnroll behaviour rather than
    /// stalling. Returns a lowercase ecosystem token (<c>go</c>, <c>dotnet</c>,
    /// <c>rust</c>, <c>node</c>, <c>python</c>, <c>maven</c>).
    /// </summary>
    internal static string ResolveRepoEcosystem(RepoRow? repo)
    {
        if (repo is not null && !string.IsNullOrWhiteSpace(repo.TestCommand))
        {
            var tc = repo.TestCommand.ToLowerInvariant();
            if (tc.Contains("go test")) return "go";
            if (tc.Contains("dotnet")) return "dotnet";
            if (tc.Contains("cargo")) return "rust";
            if (tc.Contains("pytest")) return "python";
            if (tc.Contains("npm") || tc.Contains("yarn") || tc.Contains("pnpm")) return "node";
            if (tc.Contains("mvn") || tc.Contains("gradle")) return "maven";
        }

        if (repo is not null && !string.IsNullOrWhiteSpace(repo.CloneRoot))
        {
            // docs/forge/e2e.md Stage 4.3 — delegate to the shared
            // WorkspaceEcosystemDetector (root fast-path + bounded monorepo
            // deep scan). This is the SAME detector the impacted-test LSP
            // chain uses to pick a language server, so the two subsystems
            // can never disagree about a repo's language. The deep scan is
            // why a Go repo whose only `go.mod` is at
            // `services/agent-memory/go.mod` is recognised as `go` instead
            // of defaulting to C#.
            var eco = WorkspaceEcosystemDetector.Detect(repo.CloneRoot);
            if (eco != WorkspaceEcosystemDetector.Unknown) return eco;
        }

        // Back-compat default so an unrecognised repo keeps the historical
        // Reqnroll behaviour rather than stalling.
        return "dotnet";
    }

    /// <summary>
    /// Phase 94 — resolve the target repo's existing .NET unit-test framework so
    /// the dotnet qa-e2e harness binds to the SAME runner the repo already uses
    /// (the root cause of the "generated e2e switched to xUnit" drift: the prompt
    /// never named a Reqnroll unit-test provider, so a fresh scaffold defaulted
    /// to xUnit instead of matching the repo's <c>Reqnroll.MsTest</c> convention).
    /// Scans <c>Directory.Packages.props</c> + a bounded set of <c>*.csproj</c>
    /// under the clone root for the package references that name the framework.
    /// Precedence: an explicit Reqnroll provider package
    /// (<c>Reqnroll.xUnit</c>/<c>Reqnroll.NUnit</c>/<c>Reqnroll.MsTest</c>) wins,
    /// then a bare framework package (<c>xunit</c>/<c>nunit</c>/<c>MSTest.*</c>),
    /// else <c>mstest</c> as the safe default. Returns a lowercase token:
    /// <c>mstest</c> | <c>xunit</c> | <c>nunit</c>. IO failures are non-fatal
    /// (returns the default) per the repo's "permission errors are non-fatal" rule.
    /// </summary>
    internal static string ResolveDotnetTestFramework(RepoRow? repo)
    {
        const string Default = "mstest";
        if (repo is null || string.IsNullOrWhiteSpace(repo.CloneRoot)) return Default;

        try
        {
            if (!Directory.Exists(repo.CloneRoot)) return Default;

            // Cheap, bounded corpus: central package management file (if any) +
            // up to 40 project files. A repo declares its test runner in exactly
            // these files; we never need a full-tree walk.
            var corpus = new List<string>();
            var props = Path.Combine(repo.CloneRoot, "Directory.Packages.props");
            if (File.Exists(props)) corpus.Add(props);
            try
            {
                corpus.AddRange(Directory
                    .EnumerateFiles(repo.CloneRoot, "*.csproj", SearchOption.AllDirectories)
                    .Take(40));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Partial enumeration is fine — score whatever we collected.
            }

            var sawReqnrollXunit = false;
            var sawReqnrollNunit = false;
            var sawReqnrollMstest = false;
            var sawXunit = false;
            var sawNunit = false;
            var sawMstest = false;

            foreach (var file in corpus)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                var t = text.ToLowerInvariant();

                if (t.Contains("reqnroll.xunit")) sawReqnrollXunit = true;
                if (t.Contains("reqnroll.nunit")) sawReqnrollNunit = true;
                if (t.Contains("reqnroll.mstest")) sawReqnrollMstest = true;
                // Bare framework packages — `nunit` is a substring of
                // `reqnroll.nunit`, so only count a standalone reference.
                if (t.Contains("\"xunit\"") || t.Contains("include=\"xunit")) sawXunit = true;
                if (t.Contains("\"nunit\"") || t.Contains("include=\"nunit")) sawNunit = true;
                if (t.Contains("mstest.testframework") || t.Contains("mstest.testadapter")) sawMstest = true;
            }

            // Reqnroll provider package is the strongest signal (it IS the
            // BDD runner the repo already chose). Then a bare framework package.
            if (sawReqnrollXunit) return "xunit";
            if (sawReqnrollNunit) return "nunit";
            if (sawReqnrollMstest) return "mstest";
            if (sawXunit) return "xunit";
            if (sawNunit) return "nunit";
            if (sawMstest) return "mstest";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Non-fatal — fall through to the default.
        }

        return Default;
    }

    /// <summary>Phase 94 — human-facing framework label for the dotnet qa-e2e prompt.</summary>
    private static string DotnetFrameworkLabel(string? framework) =>
        (framework ?? string.Empty).ToLowerInvariant() switch
        {
            "xunit" => "xUnit",
            "nunit" => "NUnit",
            _ => "MSTest",
        };

    /// <summary>Phase 94 — the Reqnroll unit-test-provider package that binds a
    /// `.feature` suite to the repo's framework. Pinning this is what stops the
    /// generator free-choosing xUnit.</summary>
    private static string DotnetReqnrollProvider(string? framework) =>
        (framework ?? string.Empty).ToLowerInvariant() switch
        {
            "xunit" => "Reqnroll.xUnit",
            "nunit" => "Reqnroll.NUnit",
            _ => "Reqnroll.MsTest",
        };

    /// <summary>Phase 94 — for the plain (non-BDD) dotnet harness, the test
    /// class/method attributes + e2e-category tag the agent must emit so the
    /// gate's <c>--filter Category=e2e</c> selects the suite.</summary>
    private static string DotnetPlainTestAttributes(string? framework) =>
        (framework ?? string.Empty).ToLowerInvariant() switch
        {
            "xunit" => "an xUnit test class (one `[Fact]` per scenario) tagged "
                + "`[Trait(\"Category\", \"e2e\")]` on each fact (xUnit has no `[TestClass]`)",
            "nunit" => "an NUnit `[TestFixture]` class with one `[Test]` per scenario, "
                + "each also tagged `[Category(\"e2e\")]`",
            _ => "an MSTest `[TestClass]` with one `[TestMethod]` per scenario, "
                + "each also tagged `[TestCategory(\"e2e\")]`",
        };


    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — pure prompt assembler. Takes the
    /// resolved <see cref="QaE2ePromptInputs"/> and returns the QA
    /// generator (or evaluator) prompt string. Both prompts share the
    /// same context sections; the evaluator's "Your task" + verdict
    /// schema differ.
    /// </summary>
    internal static string BuildQaE2ePrompt(QaE2ePromptInputs inputs, bool evaluator)
    {
        var setupTypeWire = E2eScenariosSetupReader.ToWire(inputs.SetupType);
        var scenariosBlock = string.Join("\n", inputs.TestScenarios.Select(s => $"- {s}"));
        var setupSection = string.IsNullOrWhiteSpace(inputs.SetupBlockMarkdown)
            ? $"_Type:_ {setupTypeWire} _(no `### Setup` block in e2e-scenarios.md; defaulted)_"
            : inputs.SetupBlockMarkdown.TrimEnd();
        var diffSection = string.IsNullOrWhiteSpace(inputs.ImplDiff)
            ? "_(no diff captured — impl workstream's branch hadn't landed any changes at prompt time)_"
            : Trim(inputs.ImplDiff, 16_000);

        var role = evaluator ? "Evaluator" : "Generator";
        // Phase 94 — the "what counts as covered" clause differs between the
        // BDD/Gherkin harness (a `.feature` scenario + its bindings) and the
        // plain harness (one idiomatic test method per scenario, no `.feature`).
        var coverageClause = inputs.UseBddPattern
            ? "covered by at least one Gherkin scenario in the `.feature` "
                + "file. Step-def file must implement every binding referenced."
            : "covered by at least one idiomatic test method (one method per "
                + "scenario, asserting its Given/When/Then). There is NO Gherkin "
                + "feature file in this mode — a missing test method for a "
                + "required scenario is an unmet scenario.";
        var task = evaluator
            ? $$"""
                ## Your task

                Score the Generator's qa-e2e output against the acceptance
                scenarios above. Verdict shape (STRICT JSON):

                ```
                { "verdict": "pass" | "fail", "score": 0-100, "notes": "…" }
                ```

                Pass requires score >= 90 AND every acceptance scenario
                {{coverageClause}}
                A required scenario that SKIPS or no-ops because a backing
                service was unavailable is NOT covered — for `inline` /
                `compose` setups the suite must self-provision its dependency
                (embedded / ephemeral) so the scenario actually runs in the
                gate; treat a dependency-driven skip as an unmet scenario and
                do not pass. (A `Skip` is acceptable only for `lab-*` setups.)
                For `compose` / `lab-*` setup types the pipeline YAML stubs
                must also be present and reference the declared secret /
                runner-pool names.

                LAB-ONLY SCENARIOS — a scenario whose proof tag is `lab` (or
                whose setup is `lab-*` and genuinely needs lab hardware, e.g.
                real Windows service registration or a real Linux VM) is
                covered STRUCTURALLY in this gate, never by local execution:
                (a) the test exists and drives the real code path when the
                lab env-vars / acceptance opt-in are present, (b) it skips
                cleanly with a clear reason when they are absent, and (c) the
                lab pipeline YAML wires it to the declared runner pool and
                secret names. When (a)-(c) hold, score the scenario as
                COVERED and do NOT withhold pass for the missing lab run —
                Forge's gate host is lab-less and non-admin, so demanding an
                "authoritative lab execution result" is a point the Generator
                structurally cannot earn, and repeating it iteration over
                iteration only burns budget. The lab pipeline is the
                execution venue for those scenarios; this gate verifies the
                wiring.
                """
            : BuildQaE2eGeneratorTask(inputs, setupTypeWire);

        // The order is deliberate: context-first (story / workstream /
        // scenarios / setup / diff) then task. The test scenarios at the
        // top of the section list keep them in the agent's working
        // memory; the diff at the bottom of context is the largest
        // chunk and benefits from being adjacent to the task block.
        var sb = new StringBuilder()
            .AppendLine($"You are the QA crew {role} for workstream `{inputs.WorkstreamId}` (story `{inputs.StoryId}`).")
            .AppendLine()
            .AppendLine("## Story")
            .AppendLine(inputs.StoryTitle)
            .AppendLine()
            .AppendLine("## Story description")
            // Cap the operator's story doc the way ImplDiff is capped: a per-stage
            // qa-e2e task is driven by its acceptance scenarios + setup + impl diff,
            // not the whole multi-phase plan. The full doc (often 15-20 KB) is
            // re-sent on every agentic turn and dilutes signal; the stage scenarios
            // below carry the precise contract. ~4 KB keeps the goals/intro context.
            .AppendLine(Trim(NonEmpty(inputs.StoryDescription, "_(no story description)_"), 4_000))
            .AppendLine()
            .AppendLine("## Workstream")
            .AppendLine($"**{inputs.WorkstreamName}** — Stage `{inputs.StageSlug}` of Phase `{inputs.PhaseSlug}`.")
            .AppendLine()
            .AppendLine("## Acceptance scenarios (from `implementation-plan.md`)")
            .AppendLine(scenariosBlock)
            .AppendLine()
            .AppendLine("## Setup")
            .AppendLine(setupSection)
            .AppendLine()
            .AppendLine("## Dependency provisioning (required-scenario rule)")
            .AppendLine(
                "A REQUIRED acceptance scenario must actually EXECUTE and ASSERT in Forge's gate — "
                + "never `Skip`/no-op because a backing service is absent. A skipped scenario does not "
                + "prove acceptance and is scored as UNMET. THE SCENARIO'S REAL DATA DEPENDENCY IS "
                + "AUTHORITATIVE, NOT the `### Setup` `Type` label: if a scenario needs a backing service "
                + "(a connection/DSN env-var, a live database, or equality/byte-identity of PERSISTED state), "
                + "you MUST make it run even when the declared Setup says `inline` / 'no external services'. "
                + "Forge's gate host has NO docker and NO pre-provisioned services, so honor the proof "
                + "strategy the plan declares for the scenario (its `[proof: …]` tag / architecture's "
                + "`Test & Environment Contract`) and pick the lightest option that genuinely proves it:")
            .AppendLine(
                "(1) GOLDEN SNAPSHOT — for preservation/equality assertions (\"byte-identical\", "
                + "\"unchanged after the refactor\", \"same rows\"): commit the expected output as a fixture "
                + "and assert the run reproduces it. No live service, and the only honest option when the "
                + "pre-change 'before' state no longer exists in the tree. "
                + "(2) PROVIDED INSTANCE — when the connection env-var is SET, use it (CI / compose stack). "
                + "(3) EMBEDDED / EPHEMERAL — when a real service is genuinely required and unset, start one "
                + "in-process (no docker), apply migrations, export the connection string, tear it down on "
                + "exit — but ONLY when the schema needs no non-bundled extension an embedded build lacks; "
                + "otherwise fall back to (1) or (2). `Skip` is acceptable ONLY for `lab-*` setups genuinely "
                + "unreproducible off the lab.")
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(inputs.AttachmentPromptSection))
        {
            sb.AppendLine(inputs.AttachmentPromptSection.TrimEnd()).AppendLine();
        }

        sb.AppendLine("## Implementation diff")
            .AppendLine("```diff")
            .AppendLine(diffSection)
            .AppendLine("```")
            .AppendLine()
            .AppendLine(task);

        return sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — the generator "## Your task" block,
    /// dispatched on the target repo's ecosystem (resolved by the shared
    /// <see cref="WorkspaceEcosystemDetector"/>). Each ecosystem gets its
    /// own idiomatic acceptance harness instead of the old hardwired
    /// "Go ⇒ godog, EVERYTHING ELSE ⇒ C#/Reqnroll" fork — that fork still
    /// scaffolded a full .NET project (and committed its <c>bin/</c>/<c>obj/</c>)
    /// for Rust / Python / Node / Java repos (the generalisation of
    /// smartpcr/code-intelligence#51, which fixed only the Go case). The
    /// post-pass gate command (<see cref="E2eTestCommandForEcosystem"/>) and
    /// the prompt are derived from the same ecosystem token so the suite the
    /// agent writes is always runnable under exactly the command Forge runs.
    /// </summary>
    internal static string BuildQaE2eGeneratorTask(QaE2ePromptInputs inputs, string setupTypeWire)
    {
        var gateCommand = E2eTestCommandForEcosystem(inputs.Ecosystem);
        return (inputs.Ecosystem ?? string.Empty).ToLowerInvariant() switch
        {
            "go" => BuildGoQaE2eTask(inputs, setupTypeWire, gateCommand),
            "rust" => BuildRustQaE2eTask(inputs, setupTypeWire, gateCommand),
            "python" => BuildPythonQaE2eTask(inputs, setupTypeWire, gateCommand),
            "node" => BuildNodeQaE2eTask(inputs, setupTypeWire, gateCommand),
            "maven" => BuildMavenQaE2eTask(inputs, setupTypeWire, gateCommand),
            _ => BuildDotnetQaE2eTask(inputs, setupTypeWire, gateCommand),
        };
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — the post-pass e2e gate command per
    /// ecosystem. The matching <see cref="BuildQaE2eGeneratorTask"/> block
    /// instructs the agent to make its suite runnable under exactly this
    /// command (build tag / feature flag / marker / npm script), so the gate
    /// and the generated tests stay in lockstep. Unknown / .NET falls back to
    /// the historical Reqnroll command.
    /// </summary>
    internal static string E2eTestCommandForEcosystem(string? ecosystem) =>
        (ecosystem ?? string.Empty).ToLowerInvariant() switch
        {
            "go" => "go test -tags e2e ./...",
            "rust" => "cargo test --features e2e",
            "python" => "pytest -m e2e",
            "node" => "npm run test:e2e",
            "maven" => "mvn test -Dgroups=e2e",
            _ => "dotnet test --filter Category=e2e",
        };

    private static string BuildGoQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        var storySafe = StoryPaths.Safe(inputs.StoryId);
        var goPhase = inputs.PhaseSlug.Replace('-', '_');
        var goStage = inputs.StageSlug.Replace('-', '_');

        // Phase 94 — plain (non-BDD) variant: idiomatic `go test` table-driven
        // subtests, NO godog / `.feature`.
        if (!inputs.UseBddPattern)
        {
            return $$"""
                ## Your task

                Produce the qa-e2e acceptance suite for the impl workstream above.
                This is a **Go** repository — do NOT scaffold a C#/.NET project
                (no `.csproj`, no Reqnroll, no `dotnet`) and do NOT use godog or a
                Gherkin feature file (plain-test mode).

                FIRST locate the Go module that owns the code under test: find the
                nearest `go.mod` above the files in the impl diff. Call its
                directory `<MOD>`. ALL files below go inside that module so
                `go test` compiles.

                  1. ONE Go test file at
                     `<MOD>/test/e2e/{{storySafe}}/{{goPhase}}_{{goStage}}_test.go`,
                     `package e2e`. Its FIRST line MUST be the build tag
                     `//go:build e2e` so the suite only runs under
                     `go test -tags e2e`. Add a test entrypoint
                     `func TestE2E_{{goPhase}}_{{goStage}}(t *testing.T)` with one
                     `t.Run("<scenario>", func(t *testing.T){ … })` subtest per
                     acceptance scenario, asserting its Given/When/Then. Use these
                     UNIQUE per-stage names so sibling stages sharing the `e2e`
                     package don't collide.
                     BACKING SERVICES — honor the proof strategy the plan declares
                     (`golden` -> committed fixture; `service:<name>` -> provided
                     DSN when set, else ephemeral in-process instance + migrations).
                     Never `t.Skip` a required scenario for a missing service
                     (only `lab-*` setups may skip).
                  2. When setup type is `compose` or `lab-*`, ALSO write
                     `<MOD>/test/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                     `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` per docs/forge/e2e.md §8.

                Do NOT commit build output. Reply `DONE` when complete. Forge will
                run `{{gateCommand}}` against your output; a non-zero exit re-enters
                this loop with the failure summary, so make sure the `e2e` package
                compiles AND the subtests exercise the impl diff above.
                """;
        }

        return $$"""
            ## Your task

            Produce the **godog** (Cucumber-for-Go) qa-e2e artifacts for the
            impl workstream above. This is a **Go** repository — do NOT
            scaffold a C#/.NET project (no `.csproj`, no Reqnroll, no
            `dotnet`, no `appsettings.json`).

            FIRST locate the Go module that owns the code under test: find
            the nearest `go.mod` above the files in the impl diff. Call its
            directory `<MOD>` (it is the repo root for a single-module repo,
            or a sub-path like `services/agent-memory` for a monorepo). ALL
            files below go **inside that module** so `go test` compiles —
            never at the repo root when the repo root has no `go.mod`.

              1. ONE Gherkin `.feature` file at
                 `<MOD>/test/e2e/{{storySafe}}/{{goPhase}}_{{goStage}}.feature`
                 covering every acceptance scenario. Tag with
                 `@story-{{inputs.StoryId}} @phase-{{inputs.PhaseSlug}} @stage-{{inputs.StageSlug}} @setup-{{setupTypeWire}}`.
              2. ONE godog step-definition file at
                 `<MOD>/test/e2e/{{storySafe}}/{{goPhase}}_{{goStage}}_test.go`,
                 `package e2e`. Its FIRST line MUST be the build tag
                 `//go:build e2e` so the suite only runs under
                 `go test -tags e2e`. Implement every Given/When/Then step,
                 register them in
                 `func InitializeScenario_{{goPhase}}_{{goStage}}(ctx *godog.ScenarioContext)`,
                 and add a test entrypoint
                 `func TestE2E_{{goPhase}}_{{goStage}}(t *testing.T)` that runs a
                 `godog.TestSuite` with `ScenarioInitializer:` set to that
                 initializer and `Options: &godog.Options{Format: "pretty", Paths: []string{"{{goPhase}}_{{goStage}}.feature"}, TestingT: t}`.
                 Use these UNIQUE per-stage function names so sibling stages
                 sharing the `e2e` package don't collide. Add
                 `github.com/cucumber/godog` to the module
                 (`go get github.com/cucumber/godog` then `go mod tidy` IN
                 `<MOD>`) and commit the updated `go.mod` / `go.sum`.

                 BACKING SERVICES — if a scenario needs one, honor the proof
                 strategy the plan declares for it (its `[proof: …]` tag):
                 `golden` -> assert against a committed fixture; `service:<name>`
                 -> use the provided DSN when set, else stand up an ephemeral
                 in-process instance (no docker) and apply migrations. Never
                 `t.Skip` a required scenario for a missing service (only
                 `lab-*` setups may skip). See the dependency-provisioning rule
                 above.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `<MOD>/test/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire
                 the right runner pool / secrets per docs/forge/e2e.md §8.

            Do NOT commit build output (no compiled test binaries, no
            vendored dependencies). Reply `DONE` when complete. Forge will
            run `{{gateCommand}}` against your output; a non-zero
            exit re-enters this loop with the failure summary as evaluator
            feedback, so make sure the `e2e` package compiles AND the
            scenarios actually exercise the impl diff above.
            """;
    }

    private static string BuildRustQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        var storySafe = StoryPaths.Safe(inputs.StoryId);
        var rustPhase = inputs.PhaseSlug.Replace('-', '_');
        var rustStage = inputs.StageSlug.Replace('-', '_');

        // Phase 94 — plain (non-BDD) variant: idiomatic `#[test]` functions, NO
        // cucumber-rs / `.feature`.
        if (!inputs.UseBddPattern)
        {
            return $$"""
                ## Your task

                Produce the qa-e2e acceptance suite for the impl workstream above.
                This is a **Rust** repository — do NOT scaffold a C#/.NET project
                (no `.csproj`, no Reqnroll, no `dotnet`) and do NOT use cucumber-rs
                or a Gherkin feature file (plain-test mode).

                FIRST locate the crate that owns the code under test: find the
                nearest `Cargo.toml` above the files in the impl diff. Call its
                directory `<CRATE>`. ALL files below go inside that crate so
                `cargo test` compiles.

                  1. ONE integration test at
                     `<CRATE>/tests/e2e_{{rustPhase}}_{{rustStage}}.rs` mapping each
                     acceptance scenario to a `#[test]` / `#[tokio::test]` fn that
                     asserts its Given/When/Then. Gate the WHOLE file behind
                     `#![cfg(feature = "e2e")]` so it only compiles under the `e2e`
                     feature, and add `[features]`/`e2e = []` (plus any `tokio`
                     dev-dependencies) to `<CRATE>/Cargo.toml`. Commit the updated
                     `Cargo.toml` / `Cargo.lock`.
                  2. When setup type is `compose` or `lab-*`, ALSO write
                     `<CRATE>/tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                     `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` per docs/forge/e2e.md §8.

                Do NOT commit build output (`target/`). Reply `DONE` when complete.
                Forge will run `{{gateCommand}}` against your output; a non-zero
                exit re-enters this loop with the failure summary, so make sure the
                suite compiles under the `e2e` feature AND the tests exercise the
                impl diff above.
                """;
        }

        return $$"""
            ## Your task

            Produce the qa-e2e acceptance suite for the impl workstream above.
            This is a **Rust** repository — do NOT scaffold a C#/.NET project
            (no `.csproj`, no Reqnroll, no `dotnet`).

            FIRST locate the crate that owns the code under test: find the
            nearest `Cargo.toml` above the files in the impl diff. Call its
            directory `<CRATE>` (repo root for a single-crate repo, or a
            sub-path like `crates/agent-memory` for a workspace). ALL files
            below go inside that crate so `cargo test` compiles.

              1. ONE cucumber `.feature` file at
                 `<CRATE>/tests/e2e/{{storySafe}}/{{rustPhase}}_{{rustStage}}.feature`
                 covering every acceptance scenario. Tag with
                 `@story-{{inputs.StoryId}} @phase-{{inputs.PhaseSlug}} @stage-{{inputs.StageSlug}} @setup-{{setupTypeWire}}`.
              2. ONE integration test at
                 `<CRATE>/tests/e2e_{{rustPhase}}_{{rustStage}}.rs`. Prefer the
                 `cucumber` crate (cucumber-rs) to run the `.feature` file when
                 a Gherkin runner fits; otherwise map each scenario to a
                 `#[test]` / `#[tokio::test]` fn mirroring its Given/When/Then.
                 Gate the WHOLE file behind `#![cfg(feature = "e2e")]` so it
                 only compiles under the `e2e` feature, and add
                 `[features]`/`e2e = []` (plus any `cucumber` / `tokio`
                 dev-dependencies) to `<CRATE>/Cargo.toml`. Commit the updated
                 `Cargo.toml` / `Cargo.lock`.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `<CRATE>/tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire the
                 right runner pool / secrets per docs/forge/e2e.md §8.

            Do NOT commit build output (`target/`). Reply `DONE` when complete.
            Forge will run `{{gateCommand}}` against your output; a non-zero
            exit re-enters this loop with the failure summary as evaluator
            feedback, so make sure the suite compiles under the `e2e` feature
            AND the scenarios actually exercise the impl diff above.
            """;
    }

    private static string BuildPythonQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        var pyStage = inputs.StageSlug.Replace('-', '_');

        // Phase 94 — plain (non-BDD) variant: idiomatic pytest functions, NO
        // pytest-bdd / `.feature`.
        if (!inputs.UseBddPattern)
        {
            return $$"""
                ## Your task

                Produce the qa-e2e acceptance suite for the impl workstream above.
                This is a **Python** repository — do NOT scaffold a C#/.NET project
                (no `.csproj`, no Reqnroll, no `dotnet`) and do NOT use pytest-bdd
                or a Gherkin feature file (plain-test mode).

                FIRST locate the package/project that owns the code under test: find
                the nearest `pyproject.toml` / `setup.cfg` / `requirements.txt`
                above the files in the impl diff. Call its directory `<PKG>`.

                  1. ONE pytest module at
                     `<PKG>/tests/e2e/{{inputs.PhaseSlug}}/test_{{pyStage}}.py` with
                     one `def test_<scenario>()` per acceptance scenario, asserting
                     its Given/When/Then. Mark the module
                     `pytestmark = pytest.mark.e2e` and REGISTER the `e2e` marker
                     (in `pyproject.toml`
                     `[tool.pytest.ini_options] markers = ["e2e: end-to-end"]` or
                     `pytest.ini`) so `-m e2e` selects it.
                  2. When setup type is `compose` or `lab-*`, ALSO write
                     `<PKG>/tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                     `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` per docs/forge/e2e.md §8.

                Reply `DONE` when complete. Forge will run `{{gateCommand}}` against
                your output; a non-zero exit re-enters this loop with the failure
                summary, so make sure the module imports cleanly AND the tests
                exercise the impl diff above.
                """;
        }

        return $$"""
            ## Your task

            Produce the qa-e2e acceptance suite for the impl workstream above.
            This is a **Python** repository — do NOT scaffold a C#/.NET project
            (no `.csproj`, no Reqnroll, no `dotnet`).

            FIRST locate the package/project that owns the code under test: find
            the nearest `pyproject.toml` / `setup.cfg` / `requirements.txt`
            above the files in the impl diff. Call its directory `<PKG>`.

              1. ONE Gherkin `.feature` file at
                 `<PKG>/tests/e2e/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}.feature`
                 covering every acceptance scenario. Tag with
                 `@story-{{inputs.StoryId}} @phase-{{inputs.PhaseSlug}} @stage-{{inputs.StageSlug}} @setup-{{setupTypeWire}}`.
              2. ONE pytest-bdd module at
                 `<PKG>/tests/e2e/{{inputs.PhaseSlug}}/test_{{pyStage}}.py` that
                 binds the feature via `scenarios("{{inputs.StageSlug}}.feature")`
                 and implements every `@given` / `@when` / `@then` step. Mark
                 the module `pytestmark = pytest.mark.e2e` and REGISTER the
                 `e2e` marker (in `pyproject.toml`
                 `[tool.pytest.ini_options] markers = ["e2e: end-to-end"]` or
                 `pytest.ini`) so `-m e2e` selects it. Add `pytest-bdd` to the
                 project's test dependencies.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `<PKG>/tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire the
                 right runner pool / secrets per docs/forge/e2e.md §8.

            Reply `DONE` when complete. Forge will run `{{gateCommand}}` against
            your output; a non-zero exit re-enters this loop with the failure
            summary as evaluator feedback, so make sure the suite imports
            cleanly AND the scenarios actually exercise the impl diff above.
            """;
    }

    private static string BuildNodeQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        return $$"""
            ## Your task

            Produce the qa-e2e acceptance suite for the impl workstream above.
            This is a **Node / TypeScript** repository — do NOT scaffold a
            C#/.NET project (no `.csproj`, no Reqnroll, no `dotnet`).

            FIRST locate the package that owns the code under test: find the
            nearest `package.json` above the files in the impl diff. Call its
            directory `<PKG>`.

              1. ONE e2e spec at
                 `<PKG>/test/e2e/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}.e2e.test.ts`
                 covering every acceptance scenario — one `describe(...)` for
                 the stage and one `it(...)` per scenario, mirroring each
                 Given/When/Then in the test body. Use the runner the repo
                 ALREADY uses — **vitest** or **jest** (check `<PKG>/package.json`
                 devDependencies; do NOT introduce a second runner).
              2. Add an npm script `"test:e2e"` to `<PKG>/package.json` that runs
                 ONLY the specs under `test/e2e/` (e.g. `vitest run test/e2e` or
                 `jest test/e2e`). Add any missing dev-dependency for the chosen
                 runner and commit the updated `package.json`.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `<PKG>/test/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire the
                 right runner pool / secrets per docs/forge/e2e.md §8.

            Do NOT commit build output (`node_modules/`, `dist/`). Reply `DONE`
            when complete. Forge will run `{{gateCommand}}` against your output;
            a non-zero exit re-enters this loop with the failure summary as
            evaluator feedback, so make sure the spec type-checks / runs AND the
            scenarios actually exercise the impl diff above.
            """;
    }

    private static string BuildMavenQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        var javaPhase = inputs.PhaseSlug.Replace('-', '_');
        var pascalStage = ToPascalCase(inputs.StageSlug);

        // Phase 94 — plain (non-BDD) variant: idiomatic JUnit 5 test methods, NO
        // Cucumber-JVM / `.feature`.
        if (!inputs.UseBddPattern)
        {
            return $$"""
                ## Your task

                Produce the qa-e2e acceptance suite for the impl workstream above.
                This is a **Java / Maven** repository — do NOT scaffold a C#/.NET
                project (no `.csproj`, no Reqnroll, no `dotnet`) and do NOT use
                Cucumber-JVM or a Gherkin feature file (plain-test mode).

                  1. ONE JUnit 5 test class at
                     `src/test/java/e2e/{{javaPhase}}/{{pascalStage}}Test.java`
                     annotated `@Tag("e2e")`, with one `@Test` method per
                     acceptance scenario asserting its Given/When/Then, so
                     `-Dgroups=e2e` selects it.
                  2. When setup type is `compose` or `lab-*`, ALSO write
                     `src/test/resources/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                     `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` per docs/forge/e2e.md §8.

                Do NOT commit build output (`target/`). Reply `DONE` when complete.
                Forge will run `{{gateCommand}}` against your output; a non-zero
                exit re-enters this loop with the failure summary, so make sure the
                suite compiles AND the tests exercise the impl diff above.
                """;
        }

        return $$"""
            ## Your task

            Produce the qa-e2e acceptance suite for the impl workstream above.
            This is a **Java / Maven** repository — do NOT scaffold a C#/.NET
            project (no `.csproj`, no Reqnroll, no `dotnet`).

              1. ONE Gherkin `.feature` file at
                 `src/test/resources/e2e/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}.feature`
                 covering every acceptance scenario. Tag with
                 `@story-{{inputs.StoryId}} @phase-{{inputs.PhaseSlug}} @stage-{{inputs.StageSlug}} @setup-{{setupTypeWire}}`.
              2. ONE Cucumber-JVM glue class at
                 `src/test/java/e2e/{{javaPhase}}/{{pascalStage}}Steps.java`
                 implementing every step, plus a JUnit 5 runner annotated
                 `@Tag("e2e")` (e.g. a `@Suite` + `@IncludeEngines("cucumber")`
                 class) so `-Dgroups=e2e` selects it. Add
                 `io.cucumber:cucumber-java` +
                 `io.cucumber:cucumber-junit-platform-engine` (test scope) to
                 `pom.xml` and commit it.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `src/test/resources/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire the
                 right runner pool / secrets per docs/forge/e2e.md §8.

            Do NOT commit build output (`target/`). Reply `DONE` when complete.
            Forge will run `{{gateCommand}}` against your output; a non-zero
            exit re-enters this loop with the failure summary as evaluator
            feedback, so make sure the suite compiles AND the scenarios actually
            exercise the impl diff above.
            """;
    }

    private static string BuildDotnetQaE2eTask(QaE2ePromptInputs inputs, string setupTypeWire, string gateCommand)
    {
        var fwLabel = DotnetFrameworkLabel(inputs.DotnetTestFramework);

        // Phase 94 — plain (non-BDD) variant: idiomatic test methods in the
        // repo's existing framework, NO `.feature` file. The framework is
        // detected from the repo (ResolveDotnetTestFramework) so the suite
        // matches the repo's convention instead of defaulting to xUnit.
        if (!inputs.UseBddPattern)
        {
            var plainAttrs = DotnetPlainTestAttributes(inputs.DotnetTestFramework);
            var nsStory = inputs.StoryId.Replace('-', '_');
            var nsPhase = inputs.PhaseSlug.Replace('-', '_');
            return $$"""
                ## Your task

                Produce the qa-e2e artifacts for the impl workstream above.
                This repo uses **{{fwLabel}}** for tests — do NOT introduce a
                Gherkin feature file, a BDD/Gherkin test runner, or a second test
                framework. Write plain {{fwLabel}} tests:
                  1. ONE test file at
                     `tests/E2e/{{inputs.StoryId}}/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}Tests.cs`
                     containing {{plainAttrs}}. One test method per acceptance
                     scenario, named after the scenario, with the Given/When/Then
                     steps as Arrange/Act/Assert in the body. Namespace
                     `Forge.Tests.Stories.{{nsStory}}.{{nsPhase}}`. The `[…Category…]`
                     `e2e` tag is REQUIRED so the gate's
                     `--filter Category=e2e` selects the suite.
                  2. When setup type is `compose` or `lab-*`, ALSO write
                     `tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                     `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire
                     the right runner pool / secrets per docs/forge/e2e.md §8.

                Reply `DONE` when complete. Forge will run
                `{{gateCommand}}` against your output; a non-zero exit re-enters
                this loop with the failure summary as evaluator feedback, so make
                sure every test compiles AND actually exercises the impl diff above.
                """;
        }

        // BDD/Gherkin (default) — Gherkin `.feature` + Reqnroll step defs. The
        // Reqnroll provider package is PINNED to the repo's detected framework
        // ({{provider}}) so a fresh test-project scaffold matches the repo's
        // existing runner instead of free-choosing xUnit (the reported drift).
        var provider = DotnetReqnrollProvider(inputs.DotnetTestFramework);
        return $$"""
            ## Your task

            Produce the qa-e2e artifacts for the impl workstream above:
              1. ONE Gherkin `.feature` file at `tests/Features/{{inputs.StoryId}}/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}.feature`
                 covering every acceptance scenario. Tag with
                 `@story-{{inputs.StoryId}} @phase-{{inputs.PhaseSlug}} @stage-{{inputs.StageSlug}} @setup-{{setupTypeWire}}`.
              2. ONE Reqnroll step-definition file at
                 `tests/Steps/{{inputs.StoryId}}/{{inputs.PhaseSlug}}/{{inputs.StageSlug}}Steps.cs`
                 that implements every binding. `[Binding]` class,
                 namespace `Forge.Tests.Stories.{{inputs.StoryId.Replace('-', '_')}}.{{inputs.PhaseSlug.Replace('-', '_')}}`.
                 This repo's tests use **{{fwLabel}}** — bind Reqnroll to the SAME
                 runner: reference the **{{provider}}** unit-test-provider package
                 (NOT Reqnroll.xUnit unless the repo already uses xUnit) and the
                 matching test SDK, so the scaffolded test project compiles under
                 the repo's existing convention. Match the version Central Package
                 Management (`Directory.Packages.props`) already pins when present.
              3. When setup type is `compose` or `lab-*`, ALSO write
                 `tests/e2e/{{inputs.PhaseSlug}}/azure-pipeline.yml` +
                 `.github/workflows/e2e-{{inputs.PhaseSlug}}.yml` that wire
                 the right runner pool / secrets per docs/forge/e2e.md §8.

            Reply `DONE` when complete. Forge will run
            `{{gateCommand}}` against your output;
            a non-zero exit re-enters this loop with the failure
            summary as evaluator feedback, so make sure every Given /
            When / Then binding compiles AND the scenarios actually
            exercise the impl diff above.
            """;
    }

    /// <summary>
    /// Convert a hyphen/underscore slug to PascalCase for Java class names
    /// (`agent-memory` ⇒ `AgentMemory`). Empty / null ⇒ `Stage`.
    /// </summary>
    private static string ToPascalCase(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "Stage";
        var parts = slug.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p[1..]);
        }
        return sb.Length == 0 ? "Stage" : sb.ToString();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — extract `{phaseSlug}` from the
    /// qa-e2e workstream's anchor (`phase-{phaseSlug}/stage-{stageSlug}/e2e`).
    /// Returns <c>"unknown"</c> when the anchor doesn't follow the shape.
    /// </summary>
    private static string ExtractPhaseSlugFromQaE2eAnchor(string? anchor)
    {
        if (string.IsNullOrEmpty(anchor)) return "unknown";
        var trimmed = anchor.EndsWith(QaE2eAnchorSuffix, StringComparison.Ordinal)
            ? anchor[..^QaE2eAnchorSuffix.Length]
            : anchor;
        var slash = trimmed.IndexOf('/');
        var phasePart = slash > 0 ? trimmed[..slash] : trimmed;
        return phasePart.StartsWith("phase-", StringComparison.Ordinal)
            ? phasePart["phase-".Length..]
            : phasePart;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — extract `{stageSlug}` from the
    /// qa-e2e workstream's anchor. Strips the `stage-` prefix AND the
    /// reconciler's `@{hash}` scope-edit suffix so the slug stays
    /// stable across plan revs.
    /// </summary>
    private static string ExtractStageSlugFromQaE2eAnchor(string? anchor)
    {
        if (string.IsNullOrEmpty(anchor)) return "unknown";
        var trimmed = anchor.EndsWith(QaE2eAnchorSuffix, StringComparison.Ordinal)
            ? anchor[..^QaE2eAnchorSuffix.Length]
            : anchor;
        var slash = trimmed.IndexOf('/');
        var stagePart = slash > 0 ? trimmed[(slash + 1)..] : "stage-unknown";
        var slug = stagePart.StartsWith("stage-", StringComparison.Ordinal)
            ? stagePart["stage-".Length..]
            : stagePart;
        var at = slug.IndexOf('@');
        if (at > 0) slug = slug[..at];
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — read the qa-e2e WorkItem's
    /// DescriptionMd (populated by <c>PlanReconciler.QaE2eStageDescription</c>)
    /// and extract the bullets under its <c>## Test Scenarios</c>
    /// section. Falls back to the workstream's own Summary when the
    /// WorkItem isn't present (e.g. legacy spawns without the
    /// reconciler-managed pair).
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadQaE2eTestScenariosAsync(
        string workItemId, string? summaryFallback, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        var desc = wi?.DescriptionMd ?? summaryFallback ?? string.Empty;

        var scenarios = new List<string>();
        var inSection = false;
        foreach (var raw in desc.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## Test Scenarios", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (inSection)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal)) break;
                var t = line.TrimStart();
                if (t.StartsWith("- ", StringComparison.Ordinal))
                {
                    scenarios.Add(t["- ".Length..].Trim());
                }
            }
        }
        return scenarios;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — read the merged e2e-scenarios.md
    /// from the spawn's base branch and extract (setup-type, raw-block)
    /// for the phase identified by <paramref name="phaseSlug"/>.
    /// Returns <c>(Inline, "")</c> when the file or phase block is
    /// missing.
    /// </summary>
    private async Task<(E2eSetupType Type, string BlockMd)> ReadE2eScenariosSetupAsync(
        StorySpawnRow spawn, string phaseSlug, CancellationToken ct)
    {
        // docs/forge/e2e.md Stage 6.3 — per-spawn cache. The first
        // ProduceStageE2eAsync call for a given spawn parses the
        // e2e-scenarios.md once; every subsequent stage of the same
        // spawn reads from the cached phase map. The cache key is the
        // spawn id; the file's path is derived from the spawn id +
        // base branch so two stages of one spawn always resolve to the
        // same on-disk file.
        if (this.phaseSetupCache.TryGetValue(spawn.Id, out var cached))
        {
            return ProjectPhaseSetup(cached, phaseSlug);
        }

        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        if (repo is null) return (E2eSetupType.Inline, string.Empty);

        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var path = $"{StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId)}/e2e-scenarios.md";

        string? md;
        try { md = await this.git.ReadFileAsync(repo, baseBranch, path, ct); }
        catch { md = null; }

        // Parse + populate the cache. Even when the file is missing or
        // empty we populate (with an empty map) so a second call doesn't
        // re-hit git. The cache miss counter increments here so the
        // integration test can prove cache-hit behaviour.
        this.phaseSetupCacheMisses.AddOrUpdate(spawn.Id, 1, (_, n) => n + 1);
        var phaseMap = E2eScenariosSetupReader.ReadAllPhases(md);
        this.phaseSetupCache[spawn.Id] = phaseMap;

        return ProjectPhaseSetup(phaseMap, phaseSlug);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 6.3 — project a parsed phase map down
    /// to the legacy <c>(Type, BlockMd)</c> tuple. Returns the inline
    /// fallback when the phase isn't in the map (mirrors
    /// <see cref="E2eScenariosSetupReader.ReadSetupTypeForPhase"/>'s
    /// pre-Stage-6.3 defensive default).
    /// </summary>
    private static (E2eSetupType Type, string BlockMd) ProjectPhaseSetup(
        IReadOnlyDictionary<string, E2eScenariosSetupReader.PhaseSetup> phaseMap,
        string phaseSlug)
    {
        return phaseMap.TryGetValue(phaseSlug, out var setup)
            ? (setup.Type, setup.BlockMarkdown)
            : (E2eSetupType.Inline, string.Empty);
    }

    /// <summary>
    /// Slice the raw <c>### Setup</c> bullet body for the phase whose
    /// name slug matches <paramref name="phaseSlug"/> out of
    /// <paramref name="markdown"/>. Used in the prompt so the QA agent
    /// sees the same setup declaration the operator authored, not a
    /// reconstructed approximation.
    /// </summary>
    internal static string ExtractSetupBlockForPhase(string? markdown, string phaseSlug)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(phaseSlug)) return string.Empty;
        var normalised = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalised.Split('\n');

        string? currentPhaseSlug = null;
        var inSetup = false;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^#\s+Phase\s+\d+\s*[:\-—]\s*(.+?)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var name = System.Text.RegularExpressions.Regex.Match(line, @"^#\s+Phase\s+\d+\s*[:\-—]\s*(.+?)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                currentPhaseSlug = PlanParser.Slug(name);
                inSetup = false;
                continue;
            }

            if (currentPhaseSlug is null || !string.Equals(currentPhaseSlug, phaseSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^###\s+Setup\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                inSetup = true;
                continue;
            }

            if (inSetup)
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) break;
                sb.AppendLine(line);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — read the impl workstream's diff
    /// against the spawn's base branch so the QA prompt knows what the
    /// test must exercise. Returns the empty string when:
    /// <list type="bullet">
    /// <item>The spawn has no repo bound,</item>
    /// <item>The impl sibling workstream is not present in the DB,</item>
    /// <item>The impl workstream's branch has not yet diverged from base,</item>
    /// <item>Reading the diff throws (best-effort — the loop still runs
    /// against the test scenarios + setup alone).</item>
    /// </list>
    /// </summary>
    private async Task<string> ReadImplWorkstreamDiffAsync(
        StorySpawnRow spawn, WorkstreamRow qaWorkstream, CancellationToken ct)
    {
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        if (repo is null) return string.Empty;

        var implAnchor = qaWorkstream.PlanAnchor;
        if (string.IsNullOrEmpty(implAnchor)
            || !implAnchor.EndsWith(QaE2eAnchorSuffix, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        implAnchor = implAnchor[..^QaE2eAnchorSuffix.Length];

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var impl = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.StorySpawnId == qaWorkstream.StorySpawnId
                                    && w.PlanAnchor == implAnchor, ct);
        if (impl is null) return string.Empty;

        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        try
        {
            // Ensure the impl workstream's worktree exists, then read
            // its diff vs base. GetChangedFilesAsync requires a worktree
            // path (filesystem checkout), not a branch name.
            var worktreePath = await this.git.EnsureWorktreeAsync(
                repo, impl.Branch, baseBranch, ct);
            var changed = await this.git.GetChangedFilesAsync(
                repo, worktreePath, baseBranch: baseBranch, ct: ct);
            if (changed.Count == 0) return string.Empty;
            // Stage 4.3 ships the path list; full unified-diff capture
            // is a Stage R polish so the prompt size stays bounded for
            // realistic stages.
            var sb = new StringBuilder();
            sb.AppendLine($"# Impl workstream `{impl.Id}` branch `{impl.Branch}` vs `{baseBranch}` — {changed.Count} files changed:");
            foreach (var p in changed.Take(200)) sb.AppendLine($"  {p}");
            if (changed.Count > 200) sb.AppendLine($"  … and {changed.Count - 200} more");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    public async Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, "qa");
        var wsList = string.Join("\n", workstreams.Select(w => $"- `{w.Id}`: {w.Name} -- {w.Summary}"));
        var repoPrefix = $"tests/Features/{StoryPaths.Safe(spawn.StoryId)}";

        // Phase 43 (extended) -- operator-uploaded attachments often contain
        // acceptance criteria / UX flows the feature files must cover.
        // Refresh + copy into the scratch dir; ReadAllFiles below filters
        // .forge-attachments/* from the deliverable set so QA outputs only
        // contain authored .feature files.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.None, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var prompt = $$"""
            You are the QA agent for story `{{spawn.StoryId}}`.

            ## Story
            {{story.Title}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}
            {{attachmentPromptSection}}
            ## Workstreams
            {{wsList}}

            ## Your task
            Produce one Gherkin `.feature` file per workstream that describes
            the user-visible behavior the workstream must deliver. Real
            scenarios -- Given/When/Then -- that step definitions can be written
            against.

            ## How to deliver
            Write one `.feature` file per workstream into your working
            directory. Name each file using the workstream's last segment
            (after the final '-') with `.feature` extension; e.g. workstream
            `ws-myStory-auth` becomes `auth.feature`. Reply `DONE` when done.
            """;

        await this.InvokeAsync("QA", spawn.Id, "qa-features", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "qa-features"), ct,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"QA produced no feature files. Workspace: {workspace}");
        }
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
    }

    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    public async Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"qa-steps-{Slug(workstream.Id)}");
        var repoPrefix = $"tests/Steps/{StoryPaths.Safe(spawn.StoryId)}";

        // Phase 43 (extended) -- step-defs implement the feature scenarios; if
        // the operator's attachments named specific contracts/types, surface
        // them so the steps don't drift from the operator-authored spec.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.None, stateDir: this.dbPaths.Folder);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var prompt = $$"""
            You are the Engineer agent writing step definitions for the QA
            feature file of workstream `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Workstream
            **{{workstream.Name}}** -- {{workstream.Summary}}
            {{attachmentPromptSection}}
            ## How to deliver
            Use your filesystem tools to write step-definition source files
            into your working directory. Name the primary file
            `{{workstream.Id}}Steps.cs`. You may add helper files. Reply
            `DONE` when finished.
            """;

        await this.InvokeAsync("Engineer", spawn.Id, $"qa-steps:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "qa-steps"), ct,
            maxTurns: spawn.StoryPoints > 0 ? spawn.StoryPoints * 30 : null);

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no step definitions for workstream {workstream.Id}. Workspace: {workspace}");
        }
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
    }

    // ── Workspace + agent invocation ─────────────────────────────────────────

    /// <summary>
    /// Resolve a per-spawn-per-phase workspace under the Forge state directory
    /// (sibling of the SQLite DB). Idempotent -- does NOT wipe an existing
    /// workspace so the calling strategy can resume from cached agent output
    /// when a downstream step (commit/push/PR-create) failed and the
    /// orchestrator re-ticks. Each individual producer method is responsible
    /// for deciding whether existing files in the workspace are reusable.
    /// </summary>
    private string PrepareWorkspace(string spawnId, string phaseTag)
    {
        var stateDir = this.dbPaths.Folder;

        // Check both slug conventions -- older workspaces may use dashes
        // (Slug) while newer code uses underscores (SlugForFs). Prefer the
        // existing directory to avoid duplicating workspaces.
        var slugUnderscore = SlugForFs(spawnId);
        var slugDash = Slug(spawnId);
        var pathUnderscore = Path.Combine(stateDir, "storyworkspaces", slugUnderscore, phaseTag);
        var pathDash = Path.Combine(stateDir, "storyworkspaces", slugDash, phaseTag);

        if (Directory.Exists(pathDash)) return pathDash;
        if (Directory.Exists(pathUnderscore)) return pathUnderscore;

        // Neither exists -- create with the dash convention (matches existing data)
        Directory.CreateDirectory(pathDash);
        return pathDash;
    }

    internal static string QaE2eWorkspacePhaseTag(
        WorkstreamRow workstream,
        IReadOnlyList<WorkstreamRow> sameSpawnWorkstreams)
    {
        var worktreeSlug = WorkstreamWorktreeSlug.Compute(workstream, sameSpawnWorkstreams);
        return $"qa-e2e-{worktreeSlug}";
    }

    private async Task<string> ComputeQaE2eWorkspacePhaseTagAsync(
        WorkstreamRow workstream,
        CancellationToken ct)
    {
        List<WorkstreamRow> sameSpawnWorkstreams;
        if (string.IsNullOrWhiteSpace(workstream.StorySpawnId))
        {
            sameSpawnWorkstreams = new List<WorkstreamRow> { workstream };
        }
        else
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            sameSpawnWorkstreams = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == workstream.StorySpawnId)
                .ToListAsync(ct);
            if (sameSpawnWorkstreams.All(w => w.Id != workstream.Id))
            {
                sameSpawnWorkstreams.Add(workstream);
            }
        }

        return QaE2eWorkspacePhaseTag(workstream, sameSpawnWorkstreams);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Phase 45 follow-up -- same fix as ProduceWorkstreamChangesAsync.
        // The "cached workspace" is now the workstream's git worktree on
        // the workstream branch; cached files are uncommitted changes
        // sitting in that worktree from a prior gen-eval pass that already
        // hit verdict=pass but the orchestrator didn't get to commit before
        // a restart. We read those uncommitted changes via git, not via
        // a raw recursive enumeration of the worktree (which would return
        // the entire repo).
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        if (repo is null)
        {
            this.logger?.LogWarning(
                "ReadCachedWorkstreamFilesAsync: no repo for spawn {SpawnId}; returning empty.", spawn.Id);
            return dict;
        }
        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;

        // EnsureWorktreeAsync is idempotent -- if the worktree already
        // exists from the earlier producer pass, returns its path
        // unchanged. If it doesn't (e.g. operator wiped .worktree/ between
        // ticks), this re-creates it from origin/{branch} which means
        // changes the agent left uncommitted are gone -- handled below.
        // Phase 49 -- pass the same short slug as the producer path so we
        // open the same directory the engineer agent wrote into.
        var cachedSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        string workspace;
        try
        {
            workspace = await this.git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch, ct, worktreeSlug: cachedSlug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "ReadCachedWorkstreamFilesAsync: could not ensure worktree for {Branch}; returning empty.",
                workstream.Branch);
            return dict;
        }

        // Phase 49 -- diff against spawn.BaseBranch so cached files
        // include the agent's local commits even when origin/{branch}
        // is at an unrelated upstream tip (matches the post-pass
        // commit-scope frame in ProduceWorkstreamChangesAsync).
        var changedPaths = await this.git.GetChangedFilesAsync(
            repo, workspace, baseBranch: baseBranch, ct: ct);
        foreach (var rel in changedPaths)
        {
            var diskPath = Path.Combine(workspace, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath)) continue;
            dict[rel] = await File.ReadAllTextAsync(diskPath, ct);
        }

        this.logger?.LogInformation(
            "Read {Count} uncommitted change(s) from worktree for workstream {WsId}.",
            dict.Count, workstream.Id);

        return dict;
    }

    private static string CrewIdPrefix(string team) => team.ToLowerInvariant() switch
    {
        "architect" => "crew-arch",
        "engineer" => "crew-eng",
        "review" => "crew-rev",
        "qa" => "crew-qa",
        _ => throw new InvalidOperationException($"Unknown team '{team}' for story strategy."),
    };

    /// <summary>
    /// Per-phase wall-clock budget, computed from the originating story's
    /// points so a 1-pt change isn't given the same window as a 13-pt epic.
    /// Formula: <c>base = max(180s, points * 60s)</c>; per-phase
    /// multiplier on top.
    ///
    /// <para><paramref name="workstreamCategory"/> (optional) lets the
    /// <c>review</c> / <c>review-fix</c> phases inherit the qa-e2e 3.0× tier
    /// when they operate on a <see cref="WorkstreamCategories.QaE2e"/> PR.
    /// Rationale: the qa-e2e <em>generation</em> phase was bumped to 3.0×
    /// (24min @ 8pts) because it builds/tests against the real worktree, but
    /// the REVIEW of those e2e PRs was left at the 1.0× default (8min @ 8pts)
    /// — only a third of the time that produced the PR. A reviewer can't read
    /// a build-compiling structural PR (new nested go.mod / go.work / suite
    /// reorg) in 8min, so it hits the Copilot SDK hard timeout, posts zero
    /// comments, and the workstream stalls (observed code-intelligence
    /// ENABLE-MCP transport-wrapper-e2e PR #434, 2026-06-23:
    /// "Copilot SDK hard timeout: exceeded 8m budget"). Matching the review
    /// budget to the generation budget closes that gap.</para>
    /// </summary>
    internal static TimeSpan BudgetForPhase(int points, string phaseTag, string? workstreamCategory = null)
    {
        // Base budget scales with story points: 60s per point, floor 180s.
        var baseSeconds = Math.Max(180.0, points * 60.0);
        var prefix = phaseTag.Split(':', 2)[0];
        var multiplier = prefix switch
        {
            "plan" => 1.0,
            "decompose" => 0.7,
            "impl" => 3.0,
            "review" => 1.0,
            "review-fix" => 1.0,
            "qa-features" => 1.0,
            "qa-steps" => 0.8,
            // docs/forge/e2e.md Stage 4.3 — per-stage qa-e2e bundles
            // feature + step-defs + (optional) pipeline YAML into one
            // workspace. 2026-05-31: bumped 1.2 → 3.0 (= impl) because
            // qa-e2e now runs in the REAL git worktree and compiles/tests
            // against the actual repo (the worktree fix), so it pays the
            // same build cost as impl — a cgo-heavy repo (go-tree-sitter)
            // exhausted the old 1.2× budget (≈25min @ 21pts) on the build
            // alone → repeated `generator-timeout` with score 0 (observed
            // repo-scanner worker-adopts-ancestrywriter-e2e 2026-05-31).
            // The 1.2× was calibrated for the pre-fix EMPTY-scratch model
            // where the agent built nothing; that no longer holds.
            "qa-e2e" => 3.0,
            _ => 1.0,
        };

        // Reviewing / fixing a PR pays roughly the same cost as GENERATING it
        // — the reviewer reads the full diff and reasons about build/test
        // impact. Both impl (3.0×) and qa-e2e (3.0×) generation run at the
        // 3.0× tier, so EVERY review / review-fix inherits 3.0× instead of the
        // 1.0× default. Without this the reviewer gets only a THIRD of the
        // budget that produced the PR and hard-times-out on large PRs:
        //   - qa-e2e transport-wrapper-e2e #434 ("exceeded 8m budget");
        //   - impl dynamic-dispatch-synthesizer #453 + secondary-tool-set #454
        //     ("exceeded 8m budget" twice on 8-pt PRs, 2026-06-24) — the
        //     reviewer can't read an 8-pt impl diff in the 8m a 1.0× tier
        //     allots when the generator had 24m at 3.0×.
        // (workstreamCategory is retained for call-site compatibility and
        // future per-category tuning; today both generation tiers are 3.0×.)
        if (prefix == "review" || prefix == "review-fix")
        {
            multiplier = 3.0;
        }

        var seconds = baseSeconds * multiplier;
        // Plan docs need a higher floor -- the architect must read repo
        // context + siblings + write a large markdown file. 3 minutes
        // (the 0-point default) is too tight and causes timeouts.
        //
        // 2026-05-13 (Phase 60) -- scale wall-clock with the turn budget
        // so a 3-point plan-doc (90 turns @ ~10s/turn ≈ 900s) doesn't
        // hit `generator-timeout` before exhausting its turn cap.
        // Formula: `max(600s, min(1800s, points * 300s))`.
        //   - 1-2 pt: 600s (the historical floor; turn cap is 60 anyway)
        //   - 3 pt:   900s (15 min; aligns with 90-turn cap)
        //   - 4 pt:  1200s (20 min)
        //   - 6+ pt: 1800s (30 min cap to avoid runaway costs)
        // Without this, raising plan-doc Points 2→3 (Phase 60 floor)
        // would just shift failures from `generator-max-turns` to
        // `generator-timeout` -- same outcome, different label.
        if (prefix == "plan")
        {
            var planFloor = Math.Max(600.0, Math.Min(1800.0, points * 300.0));
            if (seconds < planFloor) seconds = planFloor;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Runs a generator↔evaluator iter-and-score loop for one story phase.
    /// Each iteration the generator writes/updates files in
    /// <paramref name="workspace"/>; the evaluator inspects them with
    /// read-only tools and emits a JSON score. Loop continues until the
    /// evaluator passes or hits <c>ScoringConstants.MaxIterations</c>. All
    /// transcript events are emitted on <paramref name="workItemId"/>'s
    /// channel so the workstream UI card lights up "live" -- single-shot
    /// <see cref="InvokeAsync"/> emits on the spawn id, which doesn't
    /// surface on per-workstream cards.
    /// </summary>
    /// <summary>
    /// Runs the gen↔eval loop. Returns <c>true</c> when the loop was a
    /// no-op because another <c>DeliverableLoop.RunAsync</c> already owns
    /// this workItemId (Phase 74 skipped-concurrent — e.g. an operator
    /// Restart racing the scheduler tick). Returns <c>false</c> on a
    /// terminal <c>pass</c> or a parked <c>stalled-questions</c>. Throws
    /// on any other terminal verdict. Callers that materialise an artifact
    /// (the plan-doc producer) use the bool to avoid mistaking "another run
    /// owns it" for "the agent produced nothing".
    /// </summary>
    private async Task<bool> RunGenEvalLoopAsync(
        string team,
        string workItemId,
        string phaseTag,
        string generatorPrompt,
        string evaluatorPrompt,
        string workspace,
        TimeSpan? budget,
        int storyPoints,
        string? generatorAgentOverride = null,
        // Phase 47 -- evaluator agent override. When set, the crew's
        // DefaultEvaluatorAgentId is temporarily replaced so
        // ResolveAgentRunConfigAsync (useEvaluatorRole=true) picks up
        // the override. Allows the parallel plan-doc fan-out to assign
        // a different evaluator per doc instead of all 4 docs sharing
        // the crew's default evaluator.
        string? evaluatorAgentOverride = null,
        // Phase 41 -- Claude-Code tool names to revoke for this invocation
        // (forwarded to the CLI as `--disallowedTools`). Architect plan-doc
        // generators pass `["Bash"]` so an agent can't `Remove-Item` its
        // own artifact mid-turn.
        IReadOnlyList<string>? disallowedTools = null,
        // Phase 41 -- single artifact to snapshot before each generator turn
        // and restore from snapshot if the agent loses it. Catches the
        // "delete-then-forget-to-rewrite" failure mode regardless of which
        // tool was used to delete (defense in depth alongside disallowedTools).
        string? protectedFile = null,
        // Phase 42 -- invoked after each iteration's deliverable row is
        // persisted. Used by the architect plan path to commit the doc per
        // iteration so the Worktree tab shows real progress. Args are
        // (iter, verdict). Best-effort -- exceptions log but don't break
        // the loop. Null = no per-iter side effects (engineer crews don't
        // need this; their CommitFilesAsync runs once at the end).
        Func<int, string, Task>? onIterationCompleted = null,
        // Phase 49 -- resolved build command for the per-iter build gate
        // (impl phase only). When null, the gate is skipped: either the
        // repo's ecosystem isn't recognised by BuildSystemDetector or
        // the caller is a non-impl phase that doesn't run a build at all
        // (architect plan-doc, decompose, qa, review-fix). Passed through
        // to StoryBuildRunner.RunBuildAsync; null/empty there falls back
        // to `dotnet build`, which is the wrong default for non-.NET
        // ecosystems -- we explicitly skip instead.
        string? buildCommand = null,
        // Phase 56 -- quality-gate commands (cargo fmt --check / cargo
        // clippy / etc.) chained after the build gate every iter. Empty
        // = no quality gates run (today's behavior for ecosystems
        // without universal quality opinions). Resolved by the caller
        // via ResolveQualityCommands.
        IReadOnlyList<string>? qualityCommands = null,
        // Phase 56 -- test command chained after quality gates every
        // iter. Null = no test gate (today's behavior when neither the
        // operator nor BuildSystemDetector can resolve one). Resolved
        // by the caller via ResolveTestCommand.
        string? testCommand = null,
        // Phase 65 -- per-iter commit snapshot. Forwarded to
        // DeliverableLoopRequest.OnIterationCommit so the just-persisted
        // DeliverableRow gets its CommitSha + FilesChanged/Added/
        // Modified/Deleted columns populated. Null = no snapshot, row
        // columns stay null, iter-KPI endpoint falls back to event
        // aggregation. See CrewStoryArtifactStrategy producer call
        // sites (plan-doc + engineer) for typical wiring.
        Func<int, long, CancellationToken, Task<IterCommitInfo?>>? onIterationCommit = null,
        // Phase 59 Stage A — base ref for the impacted-test resolver.
        // When set together with testImpactedOnly=true, the per-iter
        // AND post-pass test gates narrow `dotnet test` to projects
        // touched by changes since this ref. Engineer impl crews pass
        // the workstream's base branch (e.g. main); plan-doc / qa
        // crews leave it null since their test commands either skip or
        // already carry an explicit filter.
        string? testBaseRef = null,
        bool testImpactedOnly = false,
        // 2026-05-21 — per-repo gate wall-clock budgets, forwarded to
        // both the per-iter and post-pass gate chains. Null falls back
        // to StoryBuildRunner.DefaultTimeout (8 min). Resolved by the
        // caller from repo.BuildTimeoutSec / repo.TestTimeoutSec.
        TimeSpan? buildTimeout = null,
        TimeSpan? testTimeout = null,
        // 2026-05-22 — repo init/restore commands threaded into both the
        // per-iter and post-pass gate chains so they run in the same shell
        // session as build/test. Resolved by the caller from
        // repo.InitCommand / repo.RestoreCommand.
        string? initCommand = null,
        string? restoreCommand = null,
        // 2026-05-26 — repo.RunTestsElevated: route the TEST stage of both
        // gate chains to the elevated broker (Forge.Api runs non-elevated).
        bool runTestsElevated = false,
        // 2026-06-03 — operator attachment manifest (StorySpawnRow.AttachmentsJson)
        // for this drive. When set, the loop scrubs any engineer-authored
        // file from the worktree's operator-only `.forge-attachments/` inbox
        // BEFORE each evaluator turn, so a self-produced "gate-output" log
        // cannot be read by the evaluator as a genuine operator artifact and
        // bait the score. Null (plan-doc / qa / review-fix callers) = no scrub.
        string? operatorAttachmentsJson = null,
        // 2026-06-03 — pass-time base refresh. Invoked at the START of the
        // OnPassValidate hook (before the post-pass gate chain runs) so the
        // worktree is merged up to the CURRENT origin/{baseBranch} before the
        // build/quality/test gates score it. Without this, a long-running
        // impl drive validates against the base snapshot it claimed against:
        // sibling workstreams that squash-merge mid-drive never re-enter the
        // gate, so a CROSS-FILE collision (two siblings each declaring the
        // same symbol in different files — no textual merge conflict, but the
        // compiler rejects the union) slips past every per-iter gate and the
        // post-pass gate, then breaks the base branch build after merge.
        // Observed 2026-06-03 on code-intelligence:PR-INGESTER stage 5.3:
        // #408 (read_pr_records.go) and #401 (feedback_pr_record.go) both
        // declared `const RoutePRRecords`; #408's worktree branched before
        // #401 landed, so its go-build gate never saw both. The callback
        // (engineer impl path only) merges origin/{baseBranch} into the
        // worktree branch with force=false (clean merge only; textual
        // conflicts abort with NO -X ours and are surfaced via event, then
        // the gate proceeds on the pre-merge tree — no regression, real
        // conflicts are still caught at provider-merge time). Safe because
        // the engineer's WIP is already committed+pushed per-iter
        // (CommitAllAndPushAsync), so the merge's internal reset --hard
        // origin/{branch} is a no-op data-wise. Null (plan-doc / qa /
        // review-fix callers) = no base refresh.
        Func<int, CancellationToken, Task>? refreshBaseBeforePostPassGate = null,
        // #2 — the workstream's DECLARED target files (the engineer impl path's
        // `targets` list from ExtractTargetPaths + architect fallback). Drives the
        // off-task wandering guard: when the agent's per-iter diff is overwhelmingly
        // outside the area implied by these for OffTaskThreshold consecutive iters,
        // the loop aborts to `stalled` so it can't keep burning the wall-clock
        // ceiling chasing an out-of-scope gate. Null (plan-doc / qa / review-fix
        // callers, or no resolvable targets) = guard disabled (fail-open).
        IReadOnlyList<string>? declaredTargets = null,
        CancellationToken ct = default)
    {
        var prefix = CrewIdPrefix(team);
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();

        var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
            ?? throw new InvalidOperationException(
                $"No crew found for team '{team}' (looked for id '{prefix}' or prefix). Configure crews in DB.");

        // When a generator override is specified, temporarily set it on
        // the crew so ResolveAgentRunConfigAsync picks it up.
        if (!string.IsNullOrEmpty(generatorAgentOverride))
        {
            crew.DefaultGeneratorAgentId = generatorAgentOverride;
        }

        // NOTE: the evaluator override write moved BELOW the evalCrew
        // fetch. See the Phase-50-followup comment there for the bug
        // history (operator-reported 2026-05-12: all four plan docs
        // pinned to the night-crew Lead). Writing the override on
        // `crew` here was a no-op for any crew with PairedWithCrewId.

        var (genAgentId, genContext, genBackendStr, genInvokeStr) = await ResolveAgentRunConfigAsync(
            control, db, crew, useEvaluatorRole: false, workspace, budget, ct,
            // 2026-05-13 root-cause fix -- Phase 21.5 (30 turns / point).
            // Without this, the dispatcher's global MaxTurns=60 default
            // capped a 13-pt workstream's generator at turn 60 of an
            // expected 390 (operator-reported on
            // qq:TELEGRAM-MESSENGER-S Outbound Sender stage; agent_usage
            // showed gen=$0/0tok next to eval=$7.31/464K because the
            // cap-aborted Failed event also dropped accumulated usage --
            // see AgentRunnerDispatcher.BuildFailedWithUsage for the
            // companion fix). Mirrors CrewQueueWorker.cs:1250-1252.
            maxTurns: storyPoints > 0 ? storyPoints * 30 : null);

        // Phase 41 -- apply per-loop tool restrictions (Bash revoked for
        // architect plan-doc loops). Records are immutable; layer the field
        // on with `with`.
        if (disallowedTools is { Count: > 0 })
        {
            genContext = genContext with { DisallowedTools = disallowedTools };
        }

        // Evaluator is best-effort -- when crew has no evaluator wiring, the
        // loop's null delegate path returns an auto-pass score so we still
        // exit after one generator iteration.
        string? evalAgentId = null;
        AgentContext? evalContext = null;
        string? evalCrewId = null;
        var evalPrefix = crew.PairedWithCrewId;
        CrewRow? evalCrew = !string.IsNullOrEmpty(evalPrefix)
            ? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evalPrefix, ct)
            : crew;

        // Phase 50 follow-up bug fix (operator-reported 2026-05-12) --
        // the evaluator override MUST land on `evalCrew`, which is the
        // row ResolveAgentRunConfigAsync(useEvaluatorRole: true)
        // actually consults via `evalCrew.DefaultEvaluatorAgentId ??
        // evalCrew.Lead`. When `crew.PairedWithCrewId` is set, `evalCrew`
        // above is a FRESH AsNoTracking row from the DB, so any earlier
        // mutation on `crew` doesn't propagate; the override was being
        // silently dropped and the resolver fell through to
        // `evalCrew.Lead`, pinning every plan-doc evaluator to the
        // night-crew Lead (all four MICROSOFT-TEAMS-MESSENGER docs ran
        // against the same agent "AB2" instead of the 4 distinct agents
        // the algorithm picked). Applying the override here covers both
        // the paired case (target = night crew row) and the unpaired
        // case (evalCrew == crew, same reference, equivalent to the
        // old write).
        if (!string.IsNullOrEmpty(evaluatorAgentOverride) && evalCrew is not null)
        {
            evalCrew.DefaultEvaluatorAgentId = evaluatorAgentOverride;
        }

        if (evalCrew is not null)
        {
            try
            {
                var (eId, eCtx, _, _) = await ResolveAgentRunConfigAsync(
                    control, db, evalCrew, useEvaluatorRole: true, workspace, budget, ct,
                    maxTurns: storyPoints > 0 ? storyPoints * 30 : null);
                evalAgentId = eId;
                evalContext = eCtx with { IsEvaluator = true };
                evalCrewId = evalCrew.Id;
                if (disallowedTools is { Count: > 0 })
                {
                    evalContext = evalContext with { DisallowedTools = disallowedTools };
                }
            }
            catch
            {
                // Crew has no evaluator agent / model. Loop will auto-pass.
            }
        }

        // Pair-diversity gate (forge_implementation.md Phase 13e).
        // The generator and evaluator MUST run on different models so the
        // evaluator catches blind spots the generator's training distribution
        // can't see. If both resolve to the same model, refuse to dispatch
        // and surface the misconfiguration on the spawn -- silent same-model
        // pairs were producing pass-stamped subpar work that no second eye
        // ever caught.
        if (evalContext is not null
            && !string.IsNullOrEmpty(genContext.Model)
            && !string.IsNullOrEmpty(evalContext.Model)
            && string.Equals(genContext.Model, evalContext.Model, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Pair-diversity rule violated: generator and evaluator both resolved to model '{genContext.Model}'. " +
                $"Generator: agent={genAgentId} crew={crew.Id}. Evaluator: agent={evalAgentId} crew={evalCrewId}. " +
                $"Set crew.{nameof(CrewRow.EvaluatorModel)} on the evaluator crew (or agent.{nameof(AgentRow.EvaluatorModel)} override) to a different model -- typically gpt-5.5/sonnet against an opus generator.");
        }

        // Phase 55 / Phase C+D+E (docs/forge/sloppy_guard.md) --
        // decide per role whether to wire in agent memory. Phase C
        // covered Claude/CLI (--resume); Phase D added Claude/SDK
        // (conversation history replay against /v1/messages); Phase E
        // adds Copilot/SDK (pooled live session reused across iters).
        // Copilot/CLI now uses COPILOT_HOME + --session-id against the
        // worktree-local .forge/memory store. Each side is
        // gated on its OWN crew row (the paired evaluator crew may
        // opt in independently). When false, the delegate skips the
        // load/upsert pair and the runner sees a null ctx.Memory --
        // exactly the pre-Phase-55 shape.
        // Phase 55 / Phase G -- merge crew default with per-agent
        // override. Agent override takes precedence when non-null so
        // an operator can flip a single agent on/off without touching
        // the rest of the crew (e.g. A/B comparing memory-enabled
        // generator against a non-memory sibling).
        var genAgentRow = await control.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == genAgentId, ct);
        AgentRow? evalAgentRow = null;
        if (!string.IsNullOrEmpty(evalAgentId))
        {
            evalAgentRow = await control.Agents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == evalAgentId, ct);
        }
        var genCrewWantsMemory = genAgentRow?.AgentMemoryOverride ?? crew.EnableAgentMemory;
        var evalCrewWantsMemory = evalAgentRow?.AgentMemoryOverride
            ?? (evalCrew?.EnableAgentMemory ?? false);

        var genMemoryEnabled = genCrewWantsMemory
            && (genContext.Backend == AgentBackend.Claude
                || genContext.Backend == AgentBackend.Copilot);
        var evalMemoryEnabled = evalCrewWantsMemory
            && evalContext is not null
            && (evalContext.Backend == AgentBackend.Claude
                || evalContext.Backend == AgentBackend.Copilot);

        await this.taskChannel.EmitAsync(workItemId, "story.agent-prompt", new
        {
            phase = phaseTag,
            team,
            generatorAgentId = genAgentId,
            evaluatorAgentId = evalAgentId,
            crewId = crew.Id,
            evaluatorCrewId = evalCrewId,
            workspace,
            generatorPromptPreview = Trim(generatorPrompt, 600),
            evaluatorPromptPreview = Trim(evaluatorPrompt, 600),
        }, agentId: genAgentId, crewId: crew.Id, ct: ct);

        // Phase 47 -- per-iter no-progress detection. Track consecutive
        // iterations whose post-generator worktree has zero uncommitted
        // changes (impl phase only -- plan/decompose write to scratch dirs
        // that aren't git worktrees, so `git status` would always show 0
        // there and false-trigger the guard). After NoProgressThreshold
        // such iterations in a row, throw NoProgressException so the
        // orchestrator can mark the workstream `blocked` instead of
        // burning the full maxIter budget. Set up here, mutated inside
        // GeneratorDelegate's closure.
        var consecutiveNoChange = 0;
        // #2 — off-task wandering guard. Derive the declared scope from the
        // workstream's target files (already computed above into `targets`) so we
        // can detect a generator that abandoned its area to churn unrelated files
        // chasing an out-of-scope repo-wide gate. Empty scope ⇒ guard disabled
        // (fail-open: never trips). Mutated inside GeneratorDelegate's closure.
        // #(b) — drop declared targets whose directory doesn't exist in the
        // worktree (e.g. a hallucinated `src/CodeIntelligence` path in a Go repo).
        // If none survive, scope is unknown → guard disabled (fail-open), so a
        // wrong/stale target list can't define a phantom scope that stalls on-task
        // work. The score-suppression above is the second line of defence.
        var offTaskScopeKeys = WorkstreamScopeGuard.ScopeKeysFromTargets(
            FilterTargetsToExisting(workspace, declaredTargets));
        var consecutiveOffTask = 0;
        // #(a) — files the agent MUTATES this iter (captured from the tool-call
        // stream), NOT just the committed diff. Reset at the top of each
        // generator turn. Lets the off-task guard catch backend thrash the agent
        // reverts before commit, so a committed diff that looks on-task (mostly
        // web/) no longer hides 40+ edits to go.mod / schema.sql / internal/**.
        var editedPathsThisIter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Phase 56 (was Phase 47e) -- separate counter for "agent edited
        // files but the unified gate chain (build → quality → test)
        // tripped". Renamed from consecutiveBrokenBuild because the
        // per-iter gate now spans more than build alone. Tripped after
        // three consecutive iters where ANY gate stage failed (build,
        // fmt/clippy, or test); routes to BuildFailedException →
        // stalled. Distinct from no-progress so the operator can tell
        // which failure mode hit. Any iter where all gate stages pass
        // resets the counter.
        var consecutiveGateFailure = 0;
        // Phase 56 (was Phase 49 follow-up) -- when ANY per-iter gate
        // stage (build/quality/test) fails we capture the failure so
        // the NEXT iteration's generator prompt can quote the actual
        // command output. Without this the generator only sees the
        // evaluator's text-only feedback and has no way to know its
        // code failed a gate (the gates run AFTER the generator returns
        // its diff, so the agent itself never observes the failure
        // mid-turn unless it volunteered the command inside the tool
        // stream). Cleared back to null on the first successful chain
        // so a stale failure doesn't haunt the prompt forever once the
        // agent fixes it.
        GateFailure? lastGateFailure = null;
        const int NoProgressThreshold = 3;
        const int BuildFailThreshold = 3;
        // Phase 56 -- post-pass recovery cap. Each time the evaluator
        // scores ≥ 90 and OnPassValidate's gate suite rejects the
        // pass, this counter increments. When it reaches the cap, the
        // validator throws BuildFailedException instead of demoting,
        // which propagates out of DeliverableLoop.RunAsync exactly like
        // today's stall path. A successful pass-validator (or a non-
        // pass iter) does NOT decrement; this is a HARD cap across the
        // workstream's lifetime within one DeliverableLoop call.
        var postPassRecoveryAttempts = 0;
        const int MaxRecoveryAttempts = 3;
        var isImplPhase = phaseTag.StartsWith("impl:", StringComparison.Ordinal);

        async Task<GeneratorRunOutput> GeneratorDelegate(int iter, string evalNotes, CancellationToken iterCt)
        {
            // 2026-06-23 — prove iter-to-iter progress to any contending drive so
            // a long-but-healthy multi-iter plan-doc run isn't mistaken for a
            // hang and force-recovered. No-op for non-plan-doc work items.
            RefreshPlanDocLockHeartbeat(workItemId);

            // An answer that parks the work ("pause until the repair merges") stops
            // the loop before any agent runs; the orchestrator stalls the workstream
            // with the answer as its reason, and Retry resumes it.
            if (isImplPhase && await ApplyParkAnswerAsync(this.scopeFactory, workItemId, iterCt) is { } park)
            {
                throw new ParkedByAnswerException(park.ExternalId, park.Answer!.Trim());
            }

            // Phase 49 follow-up -- assemble the iter prompt from up to
            // THREE layered sections so the generator sees the most
            // actionable feedback first:
            //   1. Gate output from the prior iter (if any) --
            //      compiler / linter / test runner truth, highest
            //      signal. Forge already decided this iter's diff
            //      didn't pass the gate; the agent MUST fix that
            //      before doing anything else.
            //   2. Evaluator feedback (if any) -- text critique on
            //      what's wrong with the design / approach. Useful
            //      once the code at least compiles + lints + tests.
            //   3. The original brief -- for reference. Same exact
            //      content as iter 1; never gets "lost" but takes
            //      bottom priority once feedback exists.
            // iter 1 has neither section (1) nor (2), so it sees only
            // the brief -- the unchanged historic shape.
            // Phase 56 -- "build feedback" generalised to "gate
            // feedback": build OR quality (fmt/clippy) OR test
            // failures all flow through this same prepend.
            var hasGateFeedback = lastGateFailure is not null;
            var hasEvalFeedback = !string.IsNullOrWhiteSpace(evalNotes);

            string iterPrompt;
            // Phase 50 -- load operator-pinned decisions (binding directives
            // the operator added via /spawn/pins). Refreshed each iter so a
            // freshly-added pin takes effect immediately on the next
            // generator turn. Cheap query -- one row read + JSON parse.
            // Filtered to pins scoped to this work item (workItemId match)
            // OR globally-scoped pins (workItemId == null).
            var decisionPinsBlock = await BuildDecisionPinsBlockAsync(this.scopeFactory, workItemId, iterCt, this.logger);

            // Phase 55 (docs/forge/sloppy_guard.md Phase A) -- read the
            // agent's own working notes from the prior iteration. Each
            // iter runs in a fresh agent session (no provider memory
            // across runner.StartAsync calls), so the only continuity is
            // what the prior iter wrote to `.forge/iter-notes.md` in the
            // workspace.
            var priorNotesBlock = await ReadPriorIterNotesAsync(workspace, iterCt);
            var hasPriorNotes = !string.IsNullOrEmpty(priorNotesBlock);

            if (string.IsNullOrEmpty(decisionPinsBlock) && !hasPriorNotes && !hasGateFeedback && !hasEvalFeedback)
            {
                iterPrompt = generatorPrompt;
            }
            else
            {
                var sb = new StringBuilder();
                // Decision pins ALWAYS go first when present -- they
                // override every other piece of guidance (build feedback,
                // evaluator feedback, brief) because they're the
                // operator's explicit arbitration on cross-doc loops.
                if (!string.IsNullOrEmpty(decisionPinsBlock))
                {
                    sb.AppendLine(decisionPinsBlock);
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                if (hasPriorNotes)
                {
                    sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
                    sb.AppendLine("║  NOTES FROM PRIOR ITERATIONS (your own working state)    ║");
                    sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
                    sb.AppendLine();
                    sb.AppendLine("You wrote these notes across the prior iterations of this");
                    sb.AppendLine("workstream, newest first. The agent sessions that produced");
                    sb.AppendLine("them are gone -- this archive is your only memory of what");
                    sb.AppendLine("you already learned, decided, or tried. Forge appends a new");
                    sb.AppendLine("entry every iter; you do NOT need to copy prior entries");
                    sb.AppendLine("forward yourself.");
                    sb.AppendLine();
                    sb.AppendLine(priorNotesBlock);
                    sb.AppendLine();
                    sb.AppendLine("Overwrite `.forge/iter-notes.md` with THIS iter's reflection");
                    sb.AppendLine("only -- Forge will archive it and prepend the cumulative");
                    sb.AppendLine("history to your next prompt.");
                    sb.AppendLine();
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                if (hasGateFeedback)
                {
                    // Phase 56 -- generalised from the Phase 47e/49 BUILD
                    // FAILURE block. The same prepend shape now serves
                    // build / quality / test gate failures; the stage
                    // name + the "how to fix" footer vary per stage.
                    var gf = lastGateFailure!;
                    var stage = gf.Stage;
                    var (banner, gateNoun, fixHints) = stage switch
                    {
                        "quality" => (
                            "║  QUALITY GATE FAILURE FROM PRIOR ITERATION -- FIX THIS FIRST  ║",
                            "FAILED A QUALITY GATE (fmt / clippy / lint)",
                            new[]
                            {
                                "  1. Read the error output above. The linter / formatter names the exact file and line.",
                                "  2. For `fmt --check` failures, run the formatter's WRITE mode (e.g. `cargo fmt`) yourself; do NOT hand-tweak whitespace.",
                                "  3. For `clippy -D warnings` failures, treat every warning as an error and address them all -- silencing with `#[allow(...)]` is acceptable ONLY with a one-line justification comment.",
                                $"  4. Run `{gf.Command}` yourself before responding to confirm exit 0.",
                            }),
                        "test" => (
                            "║  TEST FAILURE FROM PRIOR ITERATION -- FIX THIS FIRST          ║",
                            "FAILED THE TEST SUITE",
                            new[]
                            {
                                "  1. Read the test output above. The runner names the failing test and the assertion that fired.",
                                "  2. Decide: is the test wrong, or is the production code wrong? If the test encodes a contract you intentionally changed, update the test AND add a rationale in your Iteration Summary. If the production code regressed an existing contract, fix the production code.",
                                "  3. Do NOT delete or `#[ignore]` a failing test as a shortcut -- if a test is genuinely obsolete, remove it AND state why in your Iteration Summary.",
                                $"  4. Run `{gf.Command}` yourself before responding to confirm exit 0.",
                            }),
                        _ => (
                            "║  BUILD FAILURE FROM PRIOR ITERATION -- FIX THIS FIRST         ║",
                            "DID NOT COMPILE",
                            new[]
                            {
                                "  1. Read the error output above. The compiler tells you the exact file, line, and reason.",
                                "  2. Open the file at the line indicated and make the targeted fix.",
                                "  3. If the error is in a file OUTSIDE your workstream's target list, it is STILL your responsibility to make the workspace build. NEVER raise an Open Question for a compile/build break -- fix it yourself.",
                                "  3a. If it is your own code (or a shared file you legitimately own), edit it (don't delete it) and fix it properly. If it is a SIBLING test/file owned by ANOTHER workstream that references a symbol a later impl removed or renamed (a stale contract), do NOT fabricate a stub of the missing symbol -- quarantine that file with a top-of-file `//go:build ignore` plus a comment naming the owning workstream that must re-author it, so the shared package compiles and sibling stages can run.",
                                "  3b. Open Questions are ONLY for genuine design/scope DECISIONS an operator must make (e.g. 'absorb a future stage's feature here vs defer it'), never for unblocking a build.",
                                $"  4. Run `{gf.Command}` yourself before responding to confirm exit 0.",
                            }),
                    };
                    var bf = gf.Result;
                    sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
                    sb.AppendLine(banner);
                    sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
                    sb.AppendLine();
                    sb.AppendLine($"Your iteration {iter - 1} diff {gateNoun}.");
                    sb.AppendLine($"  stage    : {stage}");
                    sb.AppendLine($"  command  : {gf.Command}");
                    sb.AppendLine($"  exit code: {bf.ExitCode}");
                    sb.AppendLine($"  errors   : {bf.ErrorCount}");
                    sb.AppendLine($"  warnings : {bf.WarningCount}");
                    sb.AppendLine($"  duration : {bf.Duration.TotalSeconds:F2}s{(bf.TimedOut ? " (TIMED OUT)" : "")}");
                    sb.AppendLine();
                    sb.AppendLine("GATE ERROR OUTPUT (verbatim from the command -- fix EVERY error):");
                    sb.AppendLine("```");
                    // Cap at ~6000 chars so the prompt stays under most
                    // model context windows even after the rest of the
                    // layered sections + the original brief. The
                    // scanner's ErrorSummary is already a truncated
                    // best-effort excerpt, so this is a second guard.
                    var summary = bf.ErrorSummary ?? string.Empty;
                    if (summary.Length > 6000)
                    {
                        summary = summary[..6000] + "\n…(truncated; run the command again locally to see the full output)";
                    }
                    sb.AppendLine(summary);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine($"Three consecutive failed-gate iterations (across build/quality/test) will trip the gate threshold and stall the workstream. You are currently {consecutiveGateFailure} of {BuildFailThreshold}.");
                    sb.AppendLine();
                    sb.AppendLine("How to fix:");
                    foreach (var line in fixHints)
                    {
                        sb.AppendLine(line);
                    }
                    sb.AppendLine();
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                if (hasEvalFeedback)
                {
                    sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
                    sb.AppendLine("║  EVALUATOR FEEDBACK -- STRICT PER-ITEM ATTENTION REQUIRED ║");
                    sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
                    sb.AppendLine();
                    sb.AppendLine($"This is iteration {iter}. The same critique appearing across multiple");
                    sb.AppendLine("iters has been observed on architecture.md spawns (DefaultAction in 41/51");
                    sb.AppendLine("iters, P95 latency in 46/51, /handoff in 32/51) -- the agent reads the");
                    sb.AppendLine("feedback but its edits don't actually resolve the issue. The system now");
                    sb.AppendLine("treats this failure mode as a HARD STOP: if the same numbered item from");
                    sb.AppendLine("the prior \"What still needs work\" list re-appears verbatim in the");
                    sb.AppendLine("evaluator's next review, this loop will exit with");
                    sb.AppendLine("`stalled-no-convergence` and the operator will be asked to pin a");
                    sb.AppendLine("decision or restart.");
                    sb.AppendLine();
                    sb.AppendLine("═══ MANDATORY PROCESS -- DO THIS BEFORE TOUCHING THE FILE ═══");
                    sb.AppendLine();
                    sb.AppendLine("1. Read the numbered \"What still needs work\" list below.");
                    sb.AppendLine("2. For EACH numbered item, pick ONE of these actions:");
                    sb.AppendLine("   (a) FIX -- open the cited section/line, make the targeted edit,");
                    sb.AppendLine("       then run `grep -F <key-term>` on the file to confirm there");
                    sb.AppendLine("       are no remaining dangling references that contradict the fix.");
                    sb.AppendLine("   (b) DEFER -- only when the fix is genuinely out of scope for");
                    sb.AppendLine("       this iter (e.g. requires changing a sibling doc, requires");
                    sb.AppendLine("       an operator decision). Provide a one-line rationale.");
                    sb.AppendLine("3. After your edits, write a `### Prior feedback resolution` block");
                    sb.AppendLine("   in your Iteration Summary listing EVERY numbered item:");
                    sb.AppendLine("     `1. FIXED -- added §X.Y field table; grep-checked no orphan refs`");
                    sb.AppendLine("     `2. DEFERRED -- requires tech-spec edit; raised as Open Question`");
                    sb.AppendLine("4. If a previously-FIXED item re-appears in the next iter's feedback,");
                    sb.AppendLine("   your fix was insufficient -- try a STRUCTURAL change (re-split the");
                    sb.AppendLine("   table, move the section, escalate as a pin) instead of another");
                    sb.AppendLine("   word-tweak. Repeating the same edit shape three times is a strong");
                    sb.AppendLine("   signal you should defer with an Open Question.");
                    sb.AppendLine();
                    sb.AppendLine("Iterations that lack a `### Prior feedback resolution` block, or that");
                    sb.AppendLine("silently skip numbered items, are wasting tokens. The evaluator can see");
                    sb.AppendLine("when you've ignored its feedback and will hold you below pass.");
                    sb.AppendLine();
                    sb.AppendLine("═══ THE EVALUATOR'S FEEDBACK (read carefully) ═══");
                    sb.AppendLine();
                    sb.AppendLine(evalNotes);
                    sb.AppendLine();
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                // When the generator has had 3+ iterations and still isn't
                // passing, prepend the ralph-loop persistence discipline (read at
                // runtime from .claude/skills/ralph-loop/SKILL.md) so it locks onto
                // the single highest-priority blocker, verifies its own work, and
                // names an out-of-scope blocker explicitly instead of thrashing on
                // unrelated files for another full-budget turn. Best-effort:
                // skipped silently when the skill file can't be located.
                if (iter >= 3 && RalphLoopPrompt.TryGetGuidanceBlock() is { Length: > 0 } ralphBlock)
                {
                    sb.AppendLine(ralphBlock);
                    await this.taskChannel.EmitAsync(workItemId, "task.ralph-loop.injected", new
                    {
                        iter,
                    }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                }
                if (hasGateFeedback || hasEvalFeedback)
                {
                    sb.AppendLine("Now here is the original task brief (for reference only -- the");
                    sb.AppendLine("feedback above takes priority):");
                }
                else
                {
                    sb.AppendLine("Now here is the original task brief:");
                }
                sb.AppendLine("────────────────────────────────────────────────────────────");
                sb.AppendLine();
                sb.Append(generatorPrompt);
                iterPrompt = sb.ToString();
            }

            // Phase 92 -- prepend the human operator's steering directives so
            // BOTH the generator (here) and the evaluator (below) honour them.
            // Refreshed each iter: a freshly-added prompt takes effect on the
            // next turn; a dismissed one drops out. Goes ABOVE everything else
            // (decision pins / gate feedback / brief) because it is the live
            // human supervisor's binding instruction.
            var operatorSteeringBlock = await BuildOperatorSteeringBlockAsync(this.scopeFactory, workItemId, iterCt);
            if (!string.IsNullOrEmpty(operatorSteeringBlock))
            {
                iterPrompt = operatorSteeringBlock
                    + "────────────────────────────────────────────────────────────\n\n"
                    + iterPrompt;
            }

            // Phase 41 -- snapshot the protected file before invoking the
            // generator so an agent that deletes it mid-turn (observed:
            // architect ran `Remove-Item tech-spec.md` then forgot to write
            // it back) gets restored from the prior iteration's content.
            // Also snapshot on iter 1 so we have a baseline if the generator
            // nukes the seed file.
            //
            // Phase 49 follow-up -- the snapshot file MUST be deleted at the
            // end of every iter, even when the body throws (NoProgressException,
            // BuildFailedException) or the agent's event stream is cancelled
            // mid-await. Previously the cleanup was a trailing best-effort
            // delete inside the restore branch; if any throw fired between
            // snapshot-create and that line, the `.iter-snapshot.bak` leaked
            // into the worktree, got committed by the next engineer turn, and
            // poisoned the base branch (observed 2026-05-10 on
            // feature/xraft after the architect's plan-doc loop). The
            // try/finally below guarantees cleanup; *.iter-snapshot.bak is
            // also in .gitignore as a second line of defence.
            string? snapshotPath = null;
            long snapshotSize = 0;
            if (!string.IsNullOrEmpty(protectedFile) && File.Exists(protectedFile))
            {
                snapshotPath = protectedFile + ".iter-snapshot.bak";
                try
                {
                    File.Copy(protectedFile, snapshotPath, overwrite: true);
                    snapshotSize = new FileInfo(snapshotPath).Length;
                }
                catch { snapshotPath = null; }
            }

            try
            {
            // task.prompt -- same shape CrewQueueWorker emits, so the live-exec /
            // doc-conv view's latestPrompt extraction works against plan-doc
            // workItemIds the same way it does against work-item runs.
            await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
            {
                agentId = genAgentId,
                crewId = crew.Id,
                systemPrompt = string.IsNullOrEmpty(genContext.SoulMd) ? (string?)null : genContext.SoulMd,
                userPrompt = iterPrompt,
                model = genContext.Model,
                backend = genBackendStr,
                invokeMode = genInvokeStr,
                role = "generator",
                iter,
            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

            // Phase 82 -- durable per-iter prompt audit trail. Mirrors the
            // task.prompt emit above so the audit survives the transcript
            // retention janitor (last-10-iters open / last-1 closed) and
            // TaskChannel's 10 000-row cap. Per-iter scope (mirrors the
            // AgentMemoryStore pattern below) -- the strategy is a
            // singleton, IterationPromptStore is scoped on ProductDbContext.
            // Failures must NOT abort the iteration: log + emit telemetry,
            // never throw.
            try
            {
                using var promptScope = this.scopeFactory.CreateScope();
                var promptStore = promptScope.ServiceProvider.GetRequiredService<IterationPromptStore>();
                await promptStore.SaveAsync(
                    workItemId: workItemId,
                    iter: iter,
                    role: "generator",
                    agentId: genAgentId,
                    crewId: crew.Id,
                    systemPrompt: string.IsNullOrEmpty(genContext.SoulMd) ? null : genContext.SoulMd,
                    userPrompt: iterPrompt,
                    model: genContext.Model,
                    backend: genBackendStr,
                    invokeMode: genInvokeStr,
                    ct: iterCt);
            }
            catch (Exception persistEx)
            {
                this.logger?.LogWarning(persistEx,
                    "Phase 82 -- failed to persist generator prompt for {WorkItemId} iter {Iter}",
                    workItemId, iter);
                try
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.prompt.persist-failed", new
                    {
                        role = "generator",
                        iter,
                        error = persistEx.Message,
                    }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                }
                catch
                {
                    // best-effort -- transcript channel itself may be down
                }
            }

            // Phase 55 / Phase C+D -- load the prior iteration's
            // memory handle (session id for CLI, conversation turns
            // for SDK) and attach to the runner context. The runner
            // picks the right strategy based on (Backend, InvokeMode)
            // -- Claude/CLI uses --resume <sid>, Claude/SDK replays
            // PriorTurns in the messages array. Per-iter scope so the
            // AgentMemoryStore writes don't fight the outer scope's
            // tracked entities.
            var genContextForIter = genContext;
            AgentMemoryHandle? loadedGenHandle = null;
            var genUsesProviderSession = UsesProviderSessionMemory(genContext);
            if (genMemoryEnabled)
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    loadedGenHandle = await memStore.GetHandleAsync(
                        workItemId, genAgentId, AgentMemoryRoles.Generator, iterCt);
                    if (genUsesProviderSession)
                    {
                        var ensured = await memStore.EnsureSessionIdAsync(
                            workItemId, genAgentId, AgentMemoryRoles.Generator,
                            backend: genBackendStr, invokeMode: genInvokeStr,
                            memoryStorePath: genContext.Backend == AgentBackend.Copilot
                                ? AgentRunnerDispatcher.ResolveCopilotMemoryStorePath(genContext.WorktreePath)
                                : null,
                            ct: iterCt);
                        loadedGenHandle = new AgentMemoryHandle(
                            SessionId: ensured.SessionId,
                            PriorTurns: loadedGenHandle?.PriorTurns,
                            ResumeSession: ensured.ShouldResume);
                    }
                    if (genUsesProviderSession && !string.IsNullOrEmpty(genContext.WorktreePath))
                    {
                        var contextStore = memScope.ServiceProvider.GetRequiredService<WorkstreamMemoryContextStore>();
                        await contextStore.WriteAsync(workItemId, genContext.WorktreePath, iterCt);
                    }
                    if (loadedGenHandle is not null)
                    {
                        genContextForIter = genContextForIter with { Memory = loadedGenHandle };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.resumed", new
                        {
                            role = "generator",
                            agentId = genAgentId,
                            sessionId = loadedGenHandle.SessionId,
                            resumeSession = loadedGenHandle.ResumeSession,
                            priorTurns = loadedGenHandle.PriorTurns?.Count ?? 0,
                            iter,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    else
                    {
                        // Memory is on but this is the first iter (or a
                        // prior row was reaped). Pass a non-null but
                        // empty handle so the runner picks the
                        // memory-enabled path (stream-json for CLI,
                        // messages-array build for SDK) and we capture
                        // the new continuity payload at iter end.
                        genContextForIter = genContextForIter with { Memory = new AgentMemoryHandle() };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.fresh", new
                        {
                            role = "generator",
                            agentId = genAgentId,
                            iter,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "AgentMemoryStore.GetHandleAsync failed for {WorkItem}/{Agent}/gen; running without memory.",
                        workItemId, genAgentId);
                }
            }

            var invocation = await this.runner.StartAsync(workItemId, genAgentId, iterPrompt, genContextForIter, iterCt);
            var output = new StringBuilder();
            decimal cost = 0m;
            long inTok = 0, outTok = 0;
            string? capturedSessionId = null;
            // 2026-05-16 Phase 72 — same cache / reasoning / conversation
            // accumulation as the CrewQueueWorker generator delegate so
            // architect / story-artifact loops report telemetry to
            // agent_usage too.
            long cacheReadTok = 0, cacheWriteTok = 0, reasoningTok = 0, conversationTokLatest = 0;
            // 2026-05-30 — capture the model's reasoning ("thinking") text so
            // it can be persisted on the deliverable for the Summary tab.
            // The dispatcher stamps the full accumulated text on the Finished
            // event; we also accumulate the live Reasoning-delta stream as a
            // fallback for backends that only stream it.
            string? reasoningTextFinal = null;
            var reasoningSb = new System.Text.StringBuilder();
            // 2026-05-13 root-cause fix -- capture the dispatcher's
            // Failed-event error so DeliverableLoop can short-circuit
            // before the evaluator runs against a cap-aborted partial
            // output. See DeliverableLoop's FailureReason short-circuit
            // and GeneratorFailureKinds.Classify for the receiving end.
            string? failureReason = null;
            // Phase 88 -- count tool-call events so the DeliverableLoop
            // refusal guard can distinguish "agent did nothing" from
            // "agent did legitimate work then summarized briefly". Zero
            // tool calls + a refusal-like output text is the signature
            // of a wrong-crew routing (e.g., code-refactor-defender
            // assigned to a qa-e2e workstream) where the model
            // self-vetoes via its soul prompt.
            long toolCallCount = 0;
            // Set when the runner's wait-only guard ended the turn: the agent
            // was only waiting on background work it started. The iteration is
            // graded normally but does not count toward the no-progress stall.
            var endedWhileWaiting = false;
            // Phase 54 -- capture per-AssistantUsageEvent samples so the
            // per-invocation row on agent_usage retains the sub-invocation
            // curve for richer UI later. Cleared on each invocation; null
            // is preserved if the runner emits no usage event (CLI path).
            var turnSamples = new List<object>();
            int turnIndex = 0;
            editedPathsThisIter.Clear();
            await foreach (var evt in invocation.Events.WithCancellation(iterCt))
            {
                await this.taskChannel.EmitAsync(workItemId, $"task.{evt.Kind.ToString().ToLowerInvariant()}", new
                {
                    invocationId = invocation.InvocationId,
                    role = "generator",
                    agentId = genAgentId,
                    crewId = crew.Id,
                    text = evt.Text,
                    toolName = evt.ToolName,
                    toolPayload = evt.ToolPayload,
                    error = evt.Error,
                    inputTokens = evt.InputTokens,
                    outputTokens = evt.OutputTokens,
                    costUsd = evt.CostUsd,
                    // 2026-05-16 Phase 72 — propagate enriched fields
                    // to the SignalR stream so the UI Tools tab and
                    // iter-kpis can render them in real time.
                    cacheReadTokens = evt.CacheReadTokens,
                    cacheWriteTokens = evt.CacheWriteTokens,
                    reasoningTokens = evt.ReasoningTokens,
                    conversationTokens = evt.ConversationTokens,
                    toolCallId = evt.ToolCallId,
                    parentToolCallId = evt.ParentToolCallId,
                    durationMs = evt.ToolDurationMs,
                    exitCode = evt.ToolExitCode,
                    mcpServerName = evt.McpServerName,
                    sessionId = evt.SessionId,
                }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                // #(a) — record the agent's mutating-tool edit targets this turn
                // so the off-task guard sees backend thrash even when reverted
                // before commit. Reads return null (non-mutating) and are ignored.
                // Worktree-relative so absolute and relative spellings of one file
                // dedupe and root-level files match RepoRootScopeKey.
                var editTarget = WorkstreamScopeGuard.TryExtractEditedPath(evt.ToolName, evt.ToolPayload);
                if (editTarget is not null) editedPathsThisIter.Add(WorkstreamScopeGuard.NormalizeEditedPath(editTarget, workspace));

                if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
                if (evt.CacheReadTokens is > 0) cacheReadTok += evt.CacheReadTokens.Value;
                if (evt.CacheWriteTokens is > 0) cacheWriteTok += evt.CacheWriteTokens.Value;
                if (evt.ReasoningTokens is > 0) reasoningTok += evt.ReasoningTokens.Value;
                if (evt.ConversationTokens is > 0) conversationTokLatest = evt.ConversationTokens.Value;
                if (evt.Kind == AgentEventKind.Reasoning && !string.IsNullOrEmpty(evt.Text) && reasoningSb.Length < 65536) reasoningSb.Append(evt.Text);
                if (!string.IsNullOrEmpty(evt.ReasoningText)) reasoningTextFinal = evt.ReasoningText;
                // Phase 55 / Phase C -- capture session id surfaced by
                // the Claude/CLI stream-json runner so the post-iter
                // upsert can persist it for the next iter's --resume.
                // Both SessionInfo (early) and Finished (final) may
                // carry the id; latest wins.
                if (!string.IsNullOrEmpty(evt.SessionId)) capturedSessionId = evt.SessionId;
                if (evt.Kind == AgentEventKind.Failed && !string.IsNullOrEmpty(evt.Error))
                {
                    failureReason = evt.Error;
                }
                if (evt.Kind == AgentEventKind.Finished && evt.EndedWhileWaiting)
                {
                    endedWhileWaiting = true;
                }
                // Phase 88 — tool-call counter for the DeliverableLoop refusal guard.
                if (evt.Kind == AgentEventKind.ToolCall)
                {
                    toolCallCount++;
                }
                // Phase 54 -- a non-zero token tally on a single event is the
                // SDK's per-turn usage report. Record the snapshot so the
                // post-iter rollup can render a sub-invocation curve.
                if (evt.InputTokens is > 0 || evt.OutputTokens is > 0)
                {
                    turnIndex++;
                    turnSamples.Add(new
                    {
                        turnIndex,
                        inputTokens = evt.InputTokens ?? 0,
                        outputTokens = evt.OutputTokens ?? 0,
                        atUtc = evt.At,
                    });
                }
            }

            if (endedWhileWaiting)
            {
                await this.taskChannel.EmitAsync(workItemId, "task.generator.ended-waiting", new
                {
                    iter,
                    invocationId = invocation.InvocationId,
                    note = "The agent only waited on background work it started, so Forge ended the turn and is grading the iteration instead of letting it time out.",
                }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
            }

            // 2026-05-13 root-cause fix -- when the dispatcher emitted a
            // Failed event (per-task max-turns cap, hard timeout,
            // transport silent, CLI exit), short-circuit the post-loop
            // work (commit, build gate, no-progress check, memory
            // upsert) and surface FailureReason to DeliverableLoop
            // immediately. Running the build gate / no-progress check
            // against a cap-aborted partial worktree would either throw
            // BuildFailedException (drowning the original cap reason)
            // or pass against incomplete edits (deceptively
            // "build-clean"). The DeliverableLoop short-circuit handles
            // the verdict, deliverable row, and pair-assignment stamp;
            // we only need to deliver the Failure context here. The
            // `finally` block below still runs (snapshot cleanup is
            // safe).
            if (!string.IsNullOrEmpty(failureReason))
            {
                return new GeneratorRunOutput(
                    output.ToString(),
                    cost,
                    inTok,
                    outTok,
                    TurnSamplesJson: turnSamples.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(turnSamples),
                    FailureReason: failureReason,
                    FailureKind: GeneratorFailureKinds.Classify(failureReason),
                    CacheReadTokens: cacheReadTok > 0 ? cacheReadTok : null,
                    CacheWriteTokens: cacheWriteTok > 0 ? cacheWriteTok : null,
                    ReasoningTokens: reasoningTok > 0 ? reasoningTok : null,
                    ConversationTokens: conversationTokLatest > 0 ? conversationTokLatest : null,
                    SessionId: capturedSessionId,
                    ToolCallCount: toolCallCount,
                    ReasoningText: reasoningTextFinal ?? (reasoningSb.Length > 0 ? reasoningSb.ToString() : null));
            }

            // Phase 55 / Phase A.2 -- archive this iter's working
            // notes so the next iter's prompt sees the FULL history,
            // not just the last reflection. The agent always writes to
            // the same well-known path (`.forge/iter-notes.md`) and
            // doesn't manage iter numbering; Forge owns versioning
            // here because agents are unreliable at "append, don't
            // overwrite". Runs BEFORE the memory upsert / build gate
            // because those may throw and we still want the
            // reflection preserved even on a failed iter.
            try
            {
                var notesPath = Path.Combine(workspace, ".forge", "iter-notes.md");
                if (File.Exists(notesPath))
                {
                    var archiveDir = Path.Combine(workspace, ".forge", "notes");
                    Directory.CreateDirectory(archiveDir);
                    var archivePath = Path.Combine(archiveDir, $"iter-{iter}.md");
                    File.Copy(notesPath, archivePath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogDebug(ex,
                    "Phase A.2 notes archive failed at iter {Iter} for {WorkItem}; next iter prompt may show fewer iters.",
                    iter, workItemId);
            }

            // Phase 55 / Phase C+D -- persist this iter's continuity
            // payload so the next iter resumes the same conversation.
            // CLI path: just the session id (Claude Code keeps history
            // on disk under ~/.claude/projects). SDK path: the full
            // turn list = priorTurns + new user turn + new assistant
            // turn, serialised into MessagesJson for the next iter's
            // /v1/messages replay. Runs BEFORE the protected-file
            // restore and the no-progress / build-gate guards because
            // those may throw; the conversation is valid regardless
            // of whether the diff compiled. Best-effort: an upsert
            // failure logs but doesn't fail the iter.
            if (genMemoryEnabled && UsesMessageReplayMemory(genContext))
            {
                try
                {
                    var priorTurns = loadedGenHandle?.PriorTurns
                        ?? (IReadOnlyList<ConversationTurn>)Array.Empty<ConversationTurn>();
                    var assistantText = output.ToString();
                    var newTurns = new List<ConversationTurn>(priorTurns.Count + 2);
                    newTurns.AddRange(priorTurns);
                    newTurns.Add(new ConversationTurn(
                        Role: "user",
                        Content: iterPrompt,
                        Iter: iter,
                        Tokens: Math.Max(1, iterPrompt.Length / 4)));
                    newTurns.Add(new ConversationTurn(
                        Role: "assistant",
                        Content: assistantText,
                        Iter: iter,
                        Tokens: outTok > 0 ? outTok : Math.Max(1, assistantText.Length / 4)));

                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    await memStore.UpsertWithTurnsAsync(
                        workItemId, genAgentId, AgentMemoryRoles.Generator,
                        backend: genBackendStr, invokeMode: genInvokeStr,
                        sessionId: null, turns: newTurns,
                        lastIter: iter, ct: iterCt);
                    await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                    {
                        role = "generator",
                        agentId = genAgentId,
                        turnCount = newTurns.Count,
                        iter,
                    }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                    // Phase 55 / Phase F -- compact when the memory
                    // row's input-token meter crosses the effective
                    // threshold. Runs inline so the next iter's prompt
                    // sees the compacted shape; cheap (Haiku) but
                    // not free, so the trigger only fires when actually
                    // needed.
                    var compactor = memScope.ServiceProvider.GetRequiredService<MemoryCompactor>();
                    await compactor.CompactIfNeededAsync(
                        workItemId, genAgentId, AgentMemoryRoles.Generator,
                        generatorModelId: genContext.Model,
                        crewThresholdOverride: crew.CompactionTokenThreshold,
                        ct: iterCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "AgentMemoryStore.UpsertWithTurnsAsync failed for {WorkItem}/{Agent}/gen at iter {Iter}.",
                        workItemId, genAgentId, iter);
                }
            }
            else if (genMemoryEnabled)
            {
                var sessionIdToPersist = !string.IsNullOrEmpty(capturedSessionId)
                    ? capturedSessionId
                    : genContextForIter.Memory?.SessionId;
                if (!string.IsNullOrEmpty(sessionIdToPersist))
                {
                    try
                    {
                        using var memScope = this.scopeFactory.CreateScope();
                        var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                        await memStore.UpsertAsync(
                            workItemId, genAgentId, AgentMemoryRoles.Generator,
                            backend: genBackendStr, invokeMode: genInvokeStr,
                            sessionId: sessionIdToPersist, messagesJson: null,
                            lastIter: iter, approxInputTokens: 0, ct: iterCt);
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                        {
                            role = "generator",
                            agentId = genAgentId,
                            sessionId = sessionIdToPersist,
                            iter,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger?.LogWarning(ex,
                            "AgentMemoryStore.UpsertAsync failed for {WorkItem}/{Agent}/gen at iter {Iter}.",
                            workItemId, genAgentId, iter);
                    }
                }
            }

            // Phase 41 -- restore the protected file from snapshot if the
            // generator destroyed it (deleted OR emptied/truncated). A file
            // that shrinks to <10% of the snapshot size is treated as
            // truncated -- the generator's "rewrite completely" pattern often
            // hits tool-output limits, leaving an empty or stub file.
            if (!string.IsNullOrEmpty(protectedFile) && !string.IsNullOrEmpty(snapshotPath) && File.Exists(snapshotPath))
            {
                var needsRestore = false;
                string reason;
                if (!File.Exists(protectedFile))
                {
                    needsRestore = true;
                    reason = "generator-destroyed-artifact";
                }
                else
                {
                    var currentSize = new FileInfo(protectedFile).Length;
                    // Truncation guard: if the snapshot was meaningful (>100 bytes)
                    // and the current file is <10% of that, restore the snapshot.
                    if (snapshotSize > 100 && currentSize < snapshotSize / 10)
                    {
                        needsRestore = true;
                        reason = "generator-truncated-artifact";
                    }
                    else
                    {
                        reason = "";
                    }
                }

                if (needsRestore)
                {
                    try
                    {
                        File.Copy(snapshotPath, protectedFile, overwrite: true);
                        await this.taskChannel.EmitAsync(workItemId, "task.recovery", new
                        {
                            iter,
                            reason,
                            file = Path.GetFileName(protectedFile),
                            snapshotBytes = snapshotSize,
                            note = reason == "generator-truncated-artifact"
                                ? $"Agent truncated the artifact to near-zero bytes (snapshot was {snapshotSize:N0}B). Restored from previous iteration's snapshot before evaluator scoring."
                                : "Agent deleted the artifact mid-turn and didn't rewrite it. Restored from previous iteration's snapshot before evaluator scoring.",
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    catch
                    {
                        // best-effort; if the restore fails the evaluator
                        // will score 0 on a missing file as it does today.
                    }
                }
            }

            // Phase 47 -- no-progress fail-fast. After the generator finishes
            // editing the worktree this iteration, count uncommitted file
            // changes via `git status --porcelain`. If the count is zero for
            // NoProgressThreshold consecutive impl iterations, abort the
            // gen↔eval loop with a NoProgressException -- the orchestrator
            // catches that and marks the workstream `blocked` instead of
            // letting the loop burn through all 50 iters at $70+/iter on an
            // agent that has stopped producing diffs. Only fires for
            // `impl:` phaseTags; plan/decompose/qa/review-fix workspaces
            // aren't git worktrees.
            if (isImplPhase)
            {
                int changedCount;
                IReadOnlyList<string> changed = Array.Empty<string>();
                try
                {
                    changed = await this.git.GetChangedFilesAsync(
                        repo: null, worktreePath: workspace, ct: iterCt);
                    changedCount = changed.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // git status failed -- treat as inconclusive (don't trip
                    // the guard on a transient failure). Reset the counter
                    // so a real no-progress streak starts fresh on the next
                    // successful read.
                    this.logger?.LogDebug(ex,
                        "No-progress check: GetChangedFilesAsync threw for {Workspace}; skipping this iter.",
                        workspace);
                    changedCount = -1;
                }

                // A turn the wait-only guard ended was waiting on background
                // work, not stuck: it neither extends nor resets the streak.
                // Nor does a turn that declared background work still running
                // in .forge/waiting.json: Forge holds the next iteration until
                // that work ends (see BackgroundWait).
                if (changedCount == 0 && !endedWhileWaiting
                    && !await this.HoldOnDeclaredBackgroundWorkAsync(workItemId, iter, workspace, genAgentId, crew.Id, iterCt))
                {
                    consecutiveNoChange++;
                    await this.taskChannel.EmitAsync(workItemId, "task.no-progress.detected", new
                    {
                        iter,
                        consecutiveNoChange,
                        threshold = NoProgressThreshold,
                        workspace,
                    }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                    if (consecutiveNoChange >= NoProgressThreshold)
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.no-progress.aborted", new
                        {
                            iter,
                            consecutiveNoChange,
                            threshold = NoProgressThreshold,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                        throw new NoProgressException(
                            $"Engineer produced zero file changes in {consecutiveNoChange} consecutive iterations "
                            + $"(through iter {iter}, workspace {workspace}). Aborting gen/eval loop "
                            + "to save budget; operator must Restart to retry.")
                        {
                            ConsecutiveIterations = consecutiveNoChange,
                            LastIteration = iter,
                        };
                    }
                }
                else if (changedCount > 0)
                {
                    consecutiveNoChange = 0;

                    // 2026-05-27 root-cause fix for the story-spawn path: persist
                    // the agent's iter work as a commit on the workstream branch
                    // and push it BEFORE running the per-iter gate chain. Without
                    // this, a Copilot SDK 24-min timeout (or any premature
                    // generator termination) loses everything the agent edited
                    // because EnsureWorktreeAsync's `git reset --hard
                    // origin/{branch}` at the start of iter N+1 wipes the
                    // uncommitted state. Mirrors CrewQueueWorker.cs:1820-1873
                    // (live since 2026-05-13 for the queue-worker path; XRAFT
                    // stage 3.1 root cause). Pre-gate placement is deliberate:
                    // even when the gate chain throws BuildFailedException after
                    // 3 consecutive failures, the work itself is durable on
                    // origin so the operator can inspect / fix it. Best-effort:
                    // failures inside CommitAllAndPushAsync log a warning and
                    // return false; we never throw the iter just because git
                    // hiccuped. Observed pre-fix on local:UPDATE-HISTORY-IN-CL
                    // InMemoryIndex stage: 6 work items / 8 generator-timeout
                    // iters / ALL with empty CommitSha / $481+ cumulative cost
                    // with zero forward progress until operator hand-salvaged.
                    try
                    {
                        var commitMsg = $"forge[{genAgentId}] iter {iter}: {workItemId}";
                        var committed = await this.git.CommitAllAndPushAsync(
                            workspace, commitMsg, iterCt);
                        if (committed)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.iter-commit.pushed", new
                            {
                                iter,
                                workspace,
                                workItemId,
                                commitMessage = commitMsg,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        this.logger?.LogWarning(ex,
                            "Per-iter commit/push threw for wi={Wi} iter={Iter} workspace={Workspace}; continuing.",
                            workItemId, iter, workspace);
                    }

                    // #2 — off-task wandering guard. The agent committed a diff this
                    // iter (durable on origin for inspection). If it's overwhelmingly
                    // OUTSIDE the declared scope for OffTaskThreshold consecutive
                    // iters, the agent has abandoned its task — typically to chase an
                    // out-of-scope / repo-wide gate it does not own (observed on
                    // code-intelligence 3D-RENDER stage 3.1: a `web/src` types edit
                    // burned 30-39 min/iter editing internal/** Go + Makefile +
                    // schema.sql to fix an inherited agent-memory gate). Abort to
                    // `stalled` before it burns the per-iter wall-clock ceiling again.
                    // Fail-open: IsOffTask returns false when scope is unknown.
                    // #(a) extension — ALSO count out-of-scope files the agent
                    // MUTATED this turn (from the tool-call stream), not just the
                    // committed diff. The committed diff hid 3.2's backend thrash
                    // (40+ edits to go.mod / schema.sql / serve_module.go while the
                    // commit looked on-task, mostly web/). Either signal trips it.
                    var offByCommit = WorkstreamScopeGuard.IsOffTask(changed, offTaskScopeKeys, out var offRatio, out var offCount);
                    var outOfScopeEdits = WorkstreamScopeGuard.CountOutOfScope(editedPathsThisIter, offTaskScopeKeys);
                    var offByEdits = outOfScopeEdits >= WorkstreamScopeGuard.OffTaskOutOfScopeEditThreshold;
                    var offTaskTriggered = offByCommit || offByEdits;

                    // #(a) FALSE-POSITIVE GUARD — when off-task fires, first check
                    // whether the deliverable is ALREADY scoring well. The evaluator
                    // endorsing the diff is ground truth that the agent is on the
                    // RIGHT files, so a declared scope it contradicts is a bad/stale
                    // target list (e.g. a hallucinated path), not the agent off-task.
                    // (BEYOND-AST 5.2: scored 79→92→88 on the real behavioral/summary
                    // files while its declared scope was a bogus `src/CodeIntelligence`
                    // path in a Go repo — the guard wrongly stalled near-passing work.)
                    var bestPriorScore = 0;
                    if (offTaskTriggered)
                    {
                        try
                        {
                            using var scoreScope = this.scopeFactory.CreateScope();
                            var scoreDb = scoreScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                            bestPriorScore = await scoreDb.Deliverables.AsNoTracking()
                                .Where(d => d.WorkItemId == workItemId)
                                .Select(d => (int?)d.Score)
                                .MaxAsync(iterCt) ?? 0;
                        }
                        catch (Exception scoreEx) when (scoreEx is not OperationCanceledException)
                        {
                            this.logger?.LogDebug(scoreEx,
                                "Off-task suppression: best-score query failed for {Wi}; not suppressing.", workItemId);
                        }
                    }

                    if (offTaskTriggered && bestPriorScore >= WorkstreamScopeGuard.OffTaskHealthyScoreSuppress)
                    {
                        consecutiveOffTask = 0;
                        await this.taskChannel.EmitAsync(workItemId, "task.off-task.suppressed", new
                        {
                            iter,
                            bestPriorScore,
                            outOfScopeEdits,
                            declaredScope = string.Join(",", offTaskScopeKeys),
                            reason = "deliverable scoring well — declared scope likely wrong, not off-task",
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    else if (offTaskTriggered)
                    {
                        consecutiveOffTask++;
                        await this.taskChannel.EmitAsync(workItemId, "task.off-task.detected", new
                        {
                            iter,
                            consecutiveOffTask,
                            threshold = WorkstreamScopeGuard.OffTaskThreshold,
                            offRatio,
                            offCount,
                            changedCount = changed.Count,
                            outOfScopeEdits,
                            editedCount = editedPathsThisIter.Count,
                            trigger = offByEdits ? (offByCommit ? "commit+edits" : "edits") : "commit",
                            declaredScope = string.Join(",", offTaskScopeKeys),
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                        if (consecutiveOffTask >= WorkstreamScopeGuard.OffTaskThreshold)
                        {
                            var offSample = string.Join(", ", changed.Concat(editedPathsThisIter)
                                .Where(f => !WorkstreamScopeGuard.IsInScope(f, offTaskScopeKeys))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Take(8));
                            await this.taskChannel.EmitAsync(workItemId, "task.off-task.aborted", new
                            {
                                iter,
                                consecutiveOffTask,
                                threshold = WorkstreamScopeGuard.OffTaskThreshold,
                                declaredScope = string.Join(",", offTaskScopeKeys),
                                outOfScopeEdits,
                                offSample,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                            throw new OffTaskException(
                                $"Engineer abandoned its declared scope ({string.Join(", ", offTaskScopeKeys)}) for "
                                + $"{consecutiveOffTask} consecutive iterations (through iter {iter}): "
                                + $"{offCount}/{changed.Count} committed files and {outOfScopeEdits} edited file(s) are out of "
                                + $"scope (e.g. {offSample}). This usually means the agent is thrashing on an out-of-scope / "
                                + "repo-wide gate it does not own. Aborting to save budget; scope the gate or fix the base "
                                + "branch rather than Restart.")
                            {
                                ConsecutiveIterations = consecutiveOffTask,
                                LastIteration = iter,
                                DeclaredScope = string.Join(", ", offTaskScopeKeys),
                                OffTaskFilesSample = offSample,
                            };
                        }
                    }
                    else
                    {
                        consecutiveOffTask = 0;
                    }

                    // Phase 56 (was Phase 47e/49) -- agent edited files
                    // this iter. Run the unified gate chain (build →
                    // quality → test) BEFORE the evaluator scores the
                    // diff. The evaluator (text-only) was observed
                    // scoring 68 on a diff that deleted EvolutionEngine.cs
                    // along with 17 other production files -- the
                    // compiler / linter / test runner is the only honest
                    // check on "does this diff make sense as code".
                    //
                    // The chain short-circuits on the first failing
                    // stage. A null/skipped stage is treated as "passed
                    // by virtue of not running" so the chain continues
                    // to the next stage (matches today's null-build
                    // skip semantics). Each stage emits its own
                    // task.{build,test}-gate.{passed,failed,skipped}
                    // event so the Build tab shows the full timeline.
                    GateFailure? perIterFailure = await this.RunPerIterGateChainAsync(
                        workItemId,
                        workspace,
                        iter,
                        buildCommand,
                        qualityCommands,
                        testCommand,
                        genAgentId,
                        crew.Id,
                        iterCt,
                        testBaseRef: testBaseRef,
                        testImpactedOnly: testImpactedOnly,
                        buildTimeout: buildTimeout,
                        testTimeout: testTimeout,
                        initCommand: initCommand,
                        restoreCommand: restoreCommand,
                        runTestsElevated: runTestsElevated);

                    // Phase 92 — durable mirror of this iter's gate result onto the
                    // workstream row so the phase-board BUILD/TEST badges survive
                    // transcript-event eviction.
                    await this.PersistGateOutcomeAsync(workItemId, buildCommand, testCommand, perIterFailure, iterCt);

                    if (perIterFailure is not null)
                    {
                        consecutiveGateFailure++;
                        // Capture the failure so iter N+1's
                        // GeneratorDelegate can prepend it to the
                        // prompt. Cleared back to null on the next
                        // all-green iter below so the agent only sees
                        // CURRENT gate state, not a stale failure from
                        // a fix it already shipped.
                        lastGateFailure = perIterFailure;

                        await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.detected", new
                        {
                            stage = "per-iter",
                            gateStage = perIterFailure.Stage,
                            iter,
                            consecutiveGateFailure,
                            threshold = BuildFailThreshold,
                            exitCode = perIterFailure.Result.ExitCode,
                            errorCount = perIterFailure.Result.ErrorCount,
                            warningCount = perIterFailure.Result.WarningCount,
                            durationSec = perIterFailure.Result.Duration.TotalSeconds,
                            timedOut = perIterFailure.Result.TimedOut,
                            errorSummary = perIterFailure.Result.ErrorSummary,
                            command = perIterFailure.Command,
                            workspace,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                        // A HOST process loader/init failure (e.g. exit
                        // 0xC0000142 STATUS_DLL_INIT_FAILED) means the build/test
                        // toolchain process never ran — it died during DLL/runtime
                        // init before compiling anything (execution crashes like
                        // an access violation are NOT treated as host failures;
                        // those can be a real code bug — see IsHostLaunchFailure).
                        // That is an environment problem on the Forge.Api host —
                        // commonly desktop-heap / handle exhaustion from leaked
                        // gate child-process trees — NOT a defect in the
                        // engineer's diff. There are no compiler errors to read
                        // (the error summary is empty), so re-running just burns
                        // another full generator iter + budget on a failure the
                        // agent has zero levers over. Stall IMMEDIATELY with a
                        // host-specific reason instead of waiting for
                        // BuildFailThreshold consecutive launch failures.
                        // (Root cause: code-intelligence 3D-RENDER stage 4.1,
                        // 2026-06-25 — `go build ./...` exited -1073741502 every
                        // iter while leaked make/sh/go trees pinned the host.)
                        if (perIterFailure.Result.IsHostLaunchFailure)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.aborted", new
                            {
                                stage = "per-iter",
                                gateStage = perIterFailure.Stage,
                                iter,
                                reason = "host-process-init-failure",
                                exitCode = perIterFailure.Result.ExitCode,
                                durationSec = perIterFailure.Result.Duration.TotalSeconds,
                                command = perIterFailure.Command,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                            throw new BuildFailedException(
                                $"{perIterFailure.Stage} gate `{perIterFailure.Command}` exited "
                                + $"{perIterFailure.Result.ExitCode} "
                                + $"(0x{unchecked((uint)perIterFailure.Result.ExitCode):X8}) — a Windows "
                                + "process-INITIALIZATION failure (iter " + iter + "). The toolchain process never "
                                + "ran; it died during DLL/runtime init before compiling anything, so this is a HOST "
                                + "environment problem (commonly desktop-heap / handle exhaustion from leaked "
                                + "build-process trees on the Forge.Api host), NOT a defect in the engineer's diff. "
                                + "Re-running won't help — stalling for operator triage. Fix: clear leaked build "
                                + "processes / restart the Forge.Api host; a workstream Restart will NOT resolve it."
                                + $"\n\n{perIterFailure.Result.ErrorSummary}")
                            {
                                ExitCode = perIterFailure.Result.ExitCode,
                                ErrorCount = perIterFailure.Result.ErrorCount,
                                WarningCount = perIterFailure.Result.WarningCount,
                                ErrorSummary = perIterFailure.Result.ErrorSummary,
                                TrippedByConsecutiveIters = false,
                                ConsecutiveBrokenIters = consecutiveGateFailure,
                                LastIteration = iter,
                                Stage = perIterFailure.Stage,
                                Command = perIterFailure.Command,
                                HostLaunchFailure = true,
                            };
                        }

                        // 2026-05-25 — a TEST-gate TIMEOUT (distinct from a
                        // normal assertion failure) means the suite is too
                        // large/slow or hung for the gate budget; re-running
                        // the same command next iter just burns another full
                        // timeout + generator iter ($$$). Stall IMMEDIATELY for
                        // operator triage instead of waiting for
                        // BuildFailThreshold consecutive timeouts. Observed on
                        // local:UPDATE-HISTORY-IN-CL stage-1.1: with impacted-
                        // only off, the gate ran the whole build.proj suite
                        // (OnlineDiscoveryTests needs real online-discovery
                        // infra) and timed out at 15 min every iter.
                        if (perIterFailure.Stage == "test" && perIterFailure.Result.TimedOut)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.aborted", new
                            {
                                stage = "per-iter",
                                gateStage = perIterFailure.Stage,
                                iter,
                                reason = "test-gate-timeout",
                                durationSec = perIterFailure.Result.Duration.TotalSeconds,
                                command = perIterFailure.Command,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                            throw new BuildFailedException(
                                $"Test gate `{perIterFailure.Command}` TIMED OUT after "
                                + $"{perIterFailure.Result.Duration.TotalMinutes:F1} min (iter {iter}). The suite is "
                                + "too large/slow or hung for the gate budget — re-running won't help, so stalling "
                                + "for operator triage. Fix: enable impacted-only on the repo, raise TestTimeoutSec, "
                                + $"or exclude the infra-dependent tests.\n\n{perIterFailure.Result.ErrorSummary}")
                            {
                                ExitCode = perIterFailure.Result.ExitCode,
                                ErrorCount = perIterFailure.Result.ErrorCount,
                                WarningCount = perIterFailure.Result.WarningCount,
                                ErrorSummary = perIterFailure.Result.ErrorSummary,
                                TrippedByConsecutiveIters = false,
                                ConsecutiveBrokenIters = consecutiveGateFailure,
                                LastIteration = iter,
                                Stage = perIterFailure.Stage,
                                Command = perIterFailure.Command,
                            };
                        }

                        // TEST gate is BLOCKING but NOT TERMINAL (operator policy
                        // 2026-07-01): a red TEST gate must never dead-end an impl
                        // workstream to "operator must Restart". Build + quality stay
                        // HARD (a diff that doesn't compile or fails lint/format can't
                        // ship). For test we keep iterating — the failure is fed back to
                        // the generator via lastGateFailure and the evaluator still
                        // forces correctness=0, so the diff never reaches a pass/PR while
                        // tests are red — and the normal MaxIter / budget caps are the
                        // backstop. (Host-init + test-TIMEOUT above keep their own
                        // terminal handling: the toolchain produced no real test signal.)
                        if (consecutiveGateFailure >= BuildFailThreshold && perIterFailure.Stage == "test")
                        {
                            // Emit once, on the crossing, so the operator can see the ws
                            // is in blocking-but-non-terminal iterate mode (not looping
                            // silently and not dead).
                            if (consecutiveGateFailure == BuildFailThreshold)
                            {
                                await this.taskChannel.EmitAsync(workItemId, "task.gate.test-non-terminal", new
                                {
                                    stage = "per-iter",
                                    gateStage = perIterFailure.Stage,
                                    iter,
                                    consecutiveGateFailure,
                                    threshold = BuildFailThreshold,
                                    reason = "test-gate-blocking-non-terminal-keep-iterating",
                                    command = perIterFailure.Command,
                                    exitCode = perIterFailure.Result.ExitCode,
                                    errorCount = perIterFailure.Result.ErrorCount,
                                }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                            }
                        }
                        else if (consecutiveGateFailure >= BuildFailThreshold)
                        {
                            // #3 — INHERITED vs OWNED. Extract the failing test
                            // files from the gate output; if NONE of them are in
                            // this workstream's diff, the gate was already red on the
                            // base branch — a Restart will just re-burn budget on a
                            // gate this ws cannot fix. Flag it so the orchestrator
                            // renders a distinct "fix the base, don't Restart" reason.
                            var failingFiles = WorkstreamScopeGuard.ExtractFailingFiles(perIterFailure.Result.ErrorSummary);
                            var inheritedGate = WorkstreamScopeGuard.IsInheritedGateFailure(failingFiles, changed, out var inheritedFailingList);

                            await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.aborted", new
                            {
                                stage = "per-iter",
                                gateStage = perIterFailure.Stage,
                                iter,
                                consecutiveGateFailure,
                                threshold = BuildFailThreshold,
                                inherited = inheritedGate,
                                failingFiles = inheritedFailingList,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                            throw new BuildFailedException(
                                $"Engineer's diff failed `{perIterFailure.Command}` ({perIterFailure.Stage} gate) in "
                                + $"{consecutiveGateFailure} consecutive iterations (through iter {iter}, exit "
                                + $"{perIterFailure.Result.ExitCode}, {perIterFailure.Result.ErrorCount} error(s)). "
                                + $"Aborting gen/eval loop; operator must Restart to retry.\n\n{perIterFailure.Result.ErrorSummary}")
                            {
                                ExitCode = perIterFailure.Result.ExitCode,
                                ErrorCount = perIterFailure.Result.ErrorCount,
                                WarningCount = perIterFailure.Result.WarningCount,
                                ErrorSummary = perIterFailure.Result.ErrorSummary,
                                TrippedByConsecutiveIters = true,
                                ConsecutiveBrokenIters = consecutiveGateFailure,
                                LastIteration = iter,
                                Stage = perIterFailure.Stage,
                                Command = perIterFailure.Command,
                                InheritedGateFailure = inheritedGate,
                                InheritedFailingTests = inheritedGate ? inheritedFailingList : null,
                            };
                        }
                    }
                    else
                    {
                        // All gate stages either passed or were skipped
                        // (resolver returned null/empty for that stage).
                        // Reset the streak and clear the carry-forward
                        // failure so iter N+1 doesn't see stale output.
                        consecutiveGateFailure = 0;
                        lastGateFailure = null;
                    }
                }
                // changedCount == -1 → inconclusive, leave counter as-is.
            }

            return new GeneratorRunOutput(
                output.ToString(),
                cost,
                inTok,
                outTok,
                TurnSamplesJson: turnSamples.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(turnSamples),
                FailureReason: failureReason,
                FailureKind: failureReason is null ? null : GeneratorFailureKinds.Classify(failureReason),
                CacheReadTokens: cacheReadTok > 0 ? cacheReadTok : null,
                CacheWriteTokens: cacheWriteTok > 0 ? cacheWriteTok : null,
                ReasoningTokens: reasoningTok > 0 ? reasoningTok : null,
                ConversationTokens: conversationTokLatest > 0 ? conversationTokLatest : null,
                SessionId: capturedSessionId,
                ToolCallCount: toolCallCount,
                ReasoningText: reasoningTextFinal ?? (reasoningSb.Length > 0 ? reasoningSb.ToString() : null));
            }
            finally
            {
                // Phase 49 -- ALWAYS delete the snapshot file. Runs after
                // a clean iter, after a thrown guard (NoProgressException /
                // BuildFailedException), and after iterCt cancellation.
                // File.Delete is idempotent on non-existent paths so a
                // double-cleanup (e.g., the restore branch already ran) is
                // safe.
                if (!string.IsNullOrEmpty(snapshotPath))
                {
                    try { File.Delete(snapshotPath); } catch { /* best-effort */ }
                }
            }
        }

        async Task<EvaluatorRunOutput> EvaluatorDelegate(int iter, string genOutput, CancellationToken iterCt)
        {
            if (string.IsNullOrEmpty(evalAgentId) || evalContext is null)
            {
                // No evaluator configured -- auto-pass so the loop exits after
                // one generator iteration.
                return new EvaluatorRunOutput(
                    "{\"score\":95,\"verdict\":\"pass\",\"notes\":\"No evaluator configured; auto-pass.\"}",
                    0m);
            }

            // Phase 56 -- empty-diff guard. Before any LLM cost is incurred,
            // capture the ground-truth diff state from git itself: what
            // files has the engineer actually changed in the worktree?
            // Two failure modes the evaluator was previously blind to:
            //
            //   (a) Zero file changes — the agent's narrative ("All edits
            //       localized to xraft-core/src/node.rs") was pure prose
            //       with nothing to back it up. Observed 2026-05-13 on
            //       failover-cluster:XRAFT stage 3.1 iter 2: 4.3M tokens
            //       / $192 spent / 0 files committed, evaluator scored 89.
            //       Like a PR reviewer asked to review an empty PR — the
            //       right answer is REJECT, not score the cover letter.
            //
            //   (b) Build broken — the per-iter gate caught it but only
            //       fed the failure into the NEXT iter's generator prompt.
            //       The current iter's evaluator was blissfully unaware
            //       and scored the broken diff against the rubric.
            //
            // Both are fixed by injecting structured ground-truth headers
            // into the evaluator prompt for THIS iter — diff summary first,
            // build status second. Empty-diff short-circuits with a
            // synthetic reject output (zero LLM cost). Both non-empty
            // and empty paths emit telemetry so the operator can audit
            // when the guard fired.
            //
            // Impl phase only: plan-doc / decompose / qa / review-fix
            // write to scratch dirs that aren't git worktrees, so the
            // diff call would always return 0 and false-trigger the
            // reject. Mirrors the no-progress guard's same restriction.
            string? diffHeader = null;
            if (isImplPhase)
            {
                IReadOnlyList<string> changedFiles;
                IReadOnlyList<string> mergedPrIds = Array.Empty<string>();
                try
                {
                    // 2026-05-26 — diff against the merge target (testBaseRef ==
                    // the workstream's base branch) rather than the workstream's
                    // OWN remote tracking branch. With baseBranch=null
                    // GetChangedFilesAsync measures `git status` (uncommitted) +
                    // `origin/{ws-branch}..HEAD`. For a workstream already in
                    // review, the engineer's work is committed AND pushed to its
                    // own branch, so BOTH signals are zero (clean tree, HEAD ==
                    // origin/{ws-branch}) and the guard below false-fires the
                    // "ZERO file changes" reject with score 0 / $0 eval — even
                    // though the branch is 1000+ lines ahead of the PR base.
                    // Observed on local:UPDATE-HISTORY-IN-CL stage-1.2 (PR
                    // #15864664): 4 committed files vs base, but 0 vs the
                    // ws-branch → synthetic reject every iter. The delivery /
                    // pass-time GetChangedFilesAsync call already passes
                    // baseBranch; this guard must use the SAME reference so the
                    // evaluator scores the real contribution. testBaseRef is the
                    // base branch on the impl path; null on the (non-impl) plan /
                    // qa-e2e paths, which never reach this guard.
                    changedFiles = await this.git.GetChangedFilesAsync(
                        repo: null, worktreePath: workspace, baseBranch: testBaseRef, ct: iterCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogDebug(ex,
                        "Empty-diff guard: GetChangedFilesAsync threw for {Workspace}; skipping evaluator diff injection.",
                        workspace);
                    changedFiles = Array.Empty<string>();
                }

                if (changedFiles.Count == 0)
                {
                    var mergedPrFallback = await this.GetMergedPrChangedFilesFallbackAsync(
                        workItemId, workspace, iterCt);
                    if (mergedPrFallback.Files.Count > 0)
                    {
                        changedFiles = mergedPrFallback.Files;
                        mergedPrIds = mergedPrFallback.PrIds;
                        await this.taskChannel.EmitAsync(workItemId, "task.eval.merged-pr-diff", new
                        {
                            iter,
                            workspace,
                            prIds = mergedPrIds,
                            fileCount = changedFiles.Count,
                            note = "Live branch-vs-base diff was empty, so evaluator will score the historical merged PR diff.",
                        }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
                    }
                }

                if (changedFiles.Count == 0)
                {
                    // EMPTY DIFF — short-circuit with synthetic reject. The
                    // evaluator never runs (zero LLM cost) and the next-iter
                    // generator sees the explicit feedback in its iter chain.
                    // Verdict = "iterate" so the loop continues (giving the
                    // agent another shot) but score = 0 so it's clearly the
                    // worst possible outcome for this iter. After
                    // NoProgressThreshold (3) consecutive zero-change iters
                    // the Phase 47 NoProgressException trips and the
                    // workstream stalls.
                    await this.taskChannel.EmitAsync(workItemId, "task.eval.empty-diff", new
                    {
                        iter,
                        workspace,
                        note = "Engineer produced zero file changes in the worktree this iter; evaluator short-circuited with reject.",
                    }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

                    var emptyDiffNotes = $"REJECTED: Engineer produced ZERO file changes in iter {iter}. An empty diff cannot be scored — like a PR reviewer asked to review a PR with no commits. The generator's narrative described work that was never written to disk (or was written and then reverted before the evaluator could see it). Concrete file edits are required before any rubric score is meaningful.\n\nNext iter MUST:\n1. Write code to disk via Edit / Write tools.\n2. Run the repo's build command (`cargo build --workspace` / `dotnet build` / etc.) and quote the output.\n3. List the changed files in the Change Summary section (one path per line).";
                    var emptyDiffJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        score = 0,
                        verdict = "iterate",
                        dims = new
                        {
                            correctness = 0,
                            quality = 0,
                            tests = 0,
                            scope = 0,
                            risk = 0,
                        },
                        notes = emptyDiffNotes,
                    });
                    return new EvaluatorRunOutput(emptyDiffJson, 0m);
                }

                // Non-empty diff — build a "## Engineer changes this iter"
                // header to prepend to the evaluator prompt. Cap at 200
                // paths to keep the prompt size sane on monster diffs.
                const int MaxPathsInHeader = 200;
                var truncated = changedFiles.Count > MaxPathsInHeader;
                var displayList = truncated
                    ? changedFiles.Take(MaxPathsInHeader).ToList()
                    : changedFiles;
                var diffHeaderSb = new StringBuilder();
                if (mergedPrIds.Count > 0)
                {
                    diffHeaderSb.AppendLine("## Engineer changes — GROUND TRUTH (historical merged PR diff)");
                    diffHeaderSb.AppendLine();
                    diffHeaderSb.Append("The live branch-vs-base diff is empty because linked PR(s) ");
                    diffHeaderSb.Append(string.Join(", ", mergedPrIds));
                    diffHeaderSb.AppendLine(" are already merged. Score the code changes from that PR diff:");
                }
                else
                {
                    diffHeaderSb.AppendLine($"## Engineer changes — GROUND TRUTH (uncommitted `git status` + commits vs the PR base)");
                    diffHeaderSb.AppendLine();
                    diffHeaderSb.AppendLine($"The engineer's branch changes {changedFiles.Count} file(s) vs the merge target:");
                }
                diffHeaderSb.AppendLine();
                diffHeaderSb.AppendLine($"{changedFiles.Count} file(s) changed:");
                diffHeaderSb.AppendLine();
                foreach (var p in displayList)
                {
                    diffHeaderSb.Append("- `").Append(p).AppendLine("`");
                }
                if (truncated)
                {
                    diffHeaderSb.AppendLine($"- … and {changedFiles.Count - MaxPathsInHeader} more (truncated).");
                }
                diffHeaderSb.AppendLine();
                diffHeaderSb.AppendLine("Score against THIS list. Open each file (read-only) and verify the");
                diffHeaderSb.AppendLine("generator's narrative claims line up with what actually changed.");
                diffHeaderSb.AppendLine("If the generator claims a file is edited but it's not in this list,");
                diffHeaderSb.AppendLine("flag the claim as UNVERIFIED in your 'Still needs improvement' list.");
                diffHeaderSb.AppendLine();

                // Phase 56 — gate-status injection. When the prior iter's
                // per-iter gate chain (build → quality → test) failed,
                // the evaluator now sees the failing stage + command +
                // output as ground truth alongside the diff list.
                // Previously this only fed forward into the next-iter
                // GENERATOR prompt; the current-iter evaluator would
                // score the agent's "build clean" narrative without
                // independent verification.
                if (lastGateFailure is not null)
                {
                    var gf = lastGateFailure;
                    var failureNoun = gf.Stage switch
                    {
                        "quality" => "QUALITY GATE FAILED",
                        "test" => "TEST GATE FAILED",
                        _ => "BUILD FAILED",
                    };
                    diffHeaderSb.AppendLine("## Gate status this iter — GROUND TRUTH (from forge per-iter gate chain)");
                    diffHeaderSb.AppendLine();
                    diffHeaderSb.AppendLine($"**{failureNoun}** running `{gf.Command}` (stage: `{gf.Stage}`)");
                    diffHeaderSb.AppendLine($"Exit code: {gf.Result.ExitCode} · Errors: {gf.Result.ErrorCount} · Warnings: {gf.Result.WarningCount} · Duration: {gf.Result.Duration.TotalSeconds:F1}s");
                    if (!string.IsNullOrWhiteSpace(gf.Result.ErrorSummary))
                    {
                        diffHeaderSb.AppendLine();
                        diffHeaderSb.AppendLine("Error summary:");
                        diffHeaderSb.AppendLine("```");
                        // Cap at 4 KB so the evaluator's prompt doesn't bloat
                        // on a wall-of-errors output.
                        var capped = gf.Result.ErrorSummary.Length > 4096
                            ? gf.Result.ErrorSummary[..4096] + "\n…(truncated)"
                            : gf.Result.ErrorSummary;
                        diffHeaderSb.AppendLine(capped);
                        diffHeaderSb.AppendLine("```");
                    }
                    diffHeaderSb.AppendLine();
                    diffHeaderSb.AppendLine("**Score must reflect this**: any failed gate is correctness=0 minimum.");
                    diffHeaderSb.AppendLine("Do NOT pass a diff that doesn't compile, doesn't pass lint/format, or breaks tests, regardless of the generator's narrative.");
                    diffHeaderSb.AppendLine();
                }

                diffHeader = diffHeaderSb.ToString();

                await this.taskChannel.EmitAsync(workItemId, "task.eval.diff-injected", new
                {
                    iter,
                    workspace,
                    changedFilesCount = changedFiles.Count,
                    truncated,
                    gateFailedAttached = lastGateFailure is not null,
                    gateFailedStage = lastGateFailure?.Stage,
                }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
            }

            // 2026-06-03 — deterministic anti-gaming guard. The generator may
            // have self-produced files in the operator-only
            // `.forge-attachments/` inbox THIS iter (e.g. an
            // engineer-written "gate-output-*.log" to clear a phantom
            // attachment pin). Scrub anything that isn't an operator upload
            // BEFORE the evaluator reads the worktree, so it can never score
            // fabricated "operator artifacts" as genuine evidence.
            ScrubNonManifestAttachments(workspace, operatorAttachmentsJson);

            // Phase 56 — prepend the ground-truth diff header to the
            // evaluator's user prompt for THIS iter. The base evaluatorPrompt
            // (workstream brief + rubric + instructions) stays unchanged so
            // the evaluator's task definition is stable across iters; only
            // the iter-specific ground-truth header varies.
            var iterEvaluatorPrompt = diffHeader is null
                ? evaluatorPrompt
                : diffHeader + "\n" + evaluatorPrompt;

            // Phase 92 -- the evaluator must score against the same human
            // operator steering directives the generator was given. Refreshed
            // each iter (same source as the generator block above).
            var evalOperatorSteeringBlock = await BuildOperatorSteeringBlockAsync(this.scopeFactory, workItemId, iterCt);
            if (!string.IsNullOrEmpty(evalOperatorSteeringBlock))
            {
                iterEvaluatorPrompt = evalOperatorSteeringBlock
                    + "────────────────────────────────────────────────────────────\n\n"
                    + iterEvaluatorPrompt;
            }

            await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
            {
                agentId = evalAgentId,
                crewId = evalCrewId,
                systemPrompt = string.IsNullOrEmpty(evalContext.SoulMd) ? (string?)null : evalContext.SoulMd,
                userPrompt = iterEvaluatorPrompt,
                model = evalContext.Model,
                backend = evalContext.Backend.ToString().ToLowerInvariant(),
                invokeMode = evalContext.InvokeMode.ToString().ToLowerInvariant(),
                role = "evaluator",
                iter,
            }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

            // Phase 82 -- durable per-iter prompt audit trail for the
            // evaluator role. Mirrors the generator-side save above.
            try
            {
                using var promptScope = this.scopeFactory.CreateScope();
                var promptStore = promptScope.ServiceProvider.GetRequiredService<IterationPromptStore>();
                await promptStore.SaveAsync(
                    workItemId: workItemId,
                    iter: iter,
                    role: "evaluator",
                    agentId: evalAgentId,
                    crewId: evalCrewId,
                    systemPrompt: string.IsNullOrEmpty(evalContext.SoulMd) ? null : evalContext.SoulMd,
                    userPrompt: iterEvaluatorPrompt,
                    model: evalContext.Model,
                    backend: evalContext.Backend.ToString().ToLowerInvariant(),
                    invokeMode: evalContext.InvokeMode.ToString().ToLowerInvariant(),
                    ct: iterCt);
            }
            catch (Exception persistEx)
            {
                this.logger?.LogWarning(persistEx,
                    "Phase 82 -- failed to persist evaluator prompt for {WorkItemId} iter {Iter}",
                    workItemId, iter);
                try
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.prompt.persist-failed", new
                    {
                        role = "evaluator",
                        iter,
                        error = persistEx.Message,
                    }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
                }
                catch
                {
                    // best-effort
                }
            }

            // Phase 55 / Phase C+D -- mirror the generator-side
            // memory wiring for the evaluator role. The evaluator's
            // crew opts in independently (paired crews can disagree).
            // Resumed evaluator sessions inherit the prior iter's
            // scoring history (CLI: --resume sid; SDK: PriorTurns
            // replayed) -- the feature the operator explicitly
            // enabled because the evaluator's "Still needs
            // improvement" checkbox list must survive into the next
            // iter to keep convergence detection honest.
            var evalContextForIter = evalContext;
            AgentMemoryHandle? loadedEvalHandle = null;
            var evalUsesProviderSession = UsesProviderSessionMemory(evalContext);
            if (evalMemoryEnabled)
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    loadedEvalHandle = await memStore.GetHandleAsync(
                        workItemId, evalAgentId, AgentMemoryRoles.Evaluator, iterCt);
                    if (evalUsesProviderSession)
                    {
                        var ensured = await memStore.EnsureSessionIdAsync(
                            workItemId, evalAgentId, AgentMemoryRoles.Evaluator,
                            backend: evalContext.Backend.ToString().ToLowerInvariant(),
                            invokeMode: evalContext.InvokeMode.ToString().ToLowerInvariant(),
                            memoryStorePath: evalContext.Backend == AgentBackend.Copilot
                                ? AgentRunnerDispatcher.ResolveCopilotMemoryStorePath(evalContext.WorktreePath)
                                : null,
                            ct: iterCt);
                        loadedEvalHandle = new AgentMemoryHandle(
                            SessionId: ensured.SessionId,
                            PriorTurns: loadedEvalHandle?.PriorTurns,
                            ResumeSession: ensured.ShouldResume);
                    }
                    if (evalUsesProviderSession && !string.IsNullOrEmpty(evalContext.WorktreePath))
                    {
                        var contextStore = memScope.ServiceProvider.GetRequiredService<WorkstreamMemoryContextStore>();
                        await contextStore.WriteAsync(workItemId, evalContext.WorktreePath, iterCt);
                    }
                    if (loadedEvalHandle is not null)
                    {
                        evalContextForIter = evalContextForIter with { Memory = loadedEvalHandle };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.resumed", new
                        {
                            role = "evaluator",
                            agentId = evalAgentId,
                            sessionId = loadedEvalHandle.SessionId,
                            resumeSession = loadedEvalHandle.ResumeSession,
                            priorTurns = loadedEvalHandle.PriorTurns?.Count ?? 0,
                            iter,
                        }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
                    }
                    else
                    {
                        evalContextForIter = evalContextForIter with { Memory = new AgentMemoryHandle() };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.fresh", new
                        {
                            role = "evaluator",
                            agentId = evalAgentId,
                            iter,
                        }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "AgentMemoryStore.GetHandleAsync failed for {WorkItem}/{Agent}/eval; running without memory.",
                        workItemId, evalAgentId);
                }
            }

            var invocation = await this.runner.StartAsync(workItemId, evalAgentId, iterEvaluatorPrompt, evalContextForIter, iterCt);
            var output = new StringBuilder();
            decimal cost = 0m;
            long inTok = 0, outTok = 0;
            string? capturedSessionId = null;
            // 2026-05-16 Phase 72 — cache / reasoning / conversation
            // accumulation mirrored from the generator delegate so the
            // evaluator's agent_usage row carries the same telemetry.
            long evalCacheRead = 0, evalCacheWrite = 0, evalReasoning = 0, evalConversationLatest = 0;
            // Phase 54 -- mirror the generator-side per-AssistantUsageEvent
            // sample capture (see GeneratorDelegate above). Evaluator runs
            // are usually single-turn so the array typically has one entry,
            // but we keep the symmetric shape for the snapshot endpoint.
            var turnSamples = new List<object>();
            int turnIndex = 0;
            editedPathsThisIter.Clear();
            await foreach (var evt in invocation.Events.WithCancellation(iterCt))
            {
                await this.taskChannel.EmitAsync(workItemId, $"task.{evt.Kind.ToString().ToLowerInvariant()}", new
                {
                    invocationId = invocation.InvocationId,
                    role = "evaluator",
                    agentId = evalAgentId,
                    crewId = evalCrewId,
                    text = evt.Text,
                    toolName = evt.ToolName,
                    toolPayload = evt.ToolPayload,
                    error = evt.Error,
                    inputTokens = evt.InputTokens,
                    outputTokens = evt.OutputTokens,
                    costUsd = evt.CostUsd,
                    // 2026-05-16 Phase 72 — enriched event payload.
                    cacheReadTokens = evt.CacheReadTokens,
                    cacheWriteTokens = evt.CacheWriteTokens,
                    reasoningTokens = evt.ReasoningTokens,
                    conversationTokens = evt.ConversationTokens,
                    toolCallId = evt.ToolCallId,
                    parentToolCallId = evt.ParentToolCallId,
                    durationMs = evt.ToolDurationMs,
                    exitCode = evt.ToolExitCode,
                    mcpServerName = evt.McpServerName,
                    sessionId = evt.SessionId,
                }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

                // #(a) — record the agent's mutating-tool edit targets this turn
                // so the off-task guard sees backend thrash even when reverted
                // before commit. Reads return null (non-mutating) and are ignored.
                // Worktree-relative so absolute and relative spellings of one file
                // dedupe and root-level files match RepoRootScopeKey.
                var editTarget = WorkstreamScopeGuard.TryExtractEditedPath(evt.ToolName, evt.ToolPayload);
                if (editTarget is not null) editedPathsThisIter.Add(WorkstreamScopeGuard.NormalizeEditedPath(editTarget, workspace));

                if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
                if (evt.CacheReadTokens is > 0) evalCacheRead += evt.CacheReadTokens.Value;
                if (evt.CacheWriteTokens is > 0) evalCacheWrite += evt.CacheWriteTokens.Value;
                if (evt.ReasoningTokens is > 0) evalReasoning += evt.ReasoningTokens.Value;
                if (evt.ConversationTokens is > 0) evalConversationLatest = evt.ConversationTokens.Value;
                if (!string.IsNullOrEmpty(evt.SessionId)) capturedSessionId = evt.SessionId;
                if (evt.InputTokens is > 0 || evt.OutputTokens is > 0)
                {
                    turnIndex++;
                    turnSamples.Add(new
                    {
                        turnIndex,
                        inputTokens = evt.InputTokens ?? 0,
                        outputTokens = evt.OutputTokens ?? 0,
                        atUtc = evt.At,
                    });
                }
            }

            // Phase 55 / Phase C+D -- persist the evaluator's iter
            // continuity payload so the next iter resumes the same
            // scoring conversation. The evaluator's prior "Still
            // needs improvement" checkbox list sits in its own
            // conversation history; without memory it re-scores from
            // scratch each iter and can't verify whether the
            // generator actually addressed each item. CLI path
            // persists session id; SDK path persists the rebuilt turn
            // list. Best-effort: an upsert failure logs but doesn't
            // fail the iter.
            if (evalMemoryEnabled && UsesMessageReplayMemory(evalContext))
            {
                try
                {
                    var priorTurns = loadedEvalHandle?.PriorTurns
                        ?? (IReadOnlyList<ConversationTurn>)Array.Empty<ConversationTurn>();
                    var assistantText = output.ToString();
                    var newTurns = new List<ConversationTurn>(priorTurns.Count + 2);
                    newTurns.AddRange(priorTurns);
                    newTurns.Add(new ConversationTurn(
                        Role: "user",
                        Content: iterEvaluatorPrompt,
                        Iter: iter,
                        Tokens: Math.Max(1, iterEvaluatorPrompt.Length / 4)));
                    newTurns.Add(new ConversationTurn(
                        Role: "assistant",
                        Content: assistantText,
                        Iter: iter,
                        Tokens: outTok > 0 ? outTok : Math.Max(1, assistantText.Length / 4)));

                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    await memStore.UpsertWithTurnsAsync(
                        workItemId, evalAgentId, AgentMemoryRoles.Evaluator,
                        backend: evalContext.Backend.ToString().ToLowerInvariant(),
                        invokeMode: evalContext.InvokeMode.ToString().ToLowerInvariant(),
                        sessionId: null, turns: newTurns,
                        lastIter: iter, ct: iterCt);
                    await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                    {
                        role = "evaluator",
                        agentId = evalAgentId,
                        turnCount = newTurns.Count,
                        iter,
                    }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

                    // Phase 55 / Phase F -- compact evaluator memory
                    // against ITS OWN model's context window (often a
                    // smaller-window model than the generator). The
                    // evaluator crew's threshold override wins when
                    // set, else the generator-crew override, else the
                    // 300K default.
                    var compactor = memScope.ServiceProvider.GetRequiredService<MemoryCompactor>();
                    await compactor.CompactIfNeededAsync(
                        workItemId, evalAgentId, AgentMemoryRoles.Evaluator,
                        generatorModelId: evalContext.Model,
                        crewThresholdOverride: evalCrew?.CompactionTokenThreshold ?? crew.CompactionTokenThreshold,
                        ct: iterCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "AgentMemoryStore.UpsertWithTurnsAsync failed for {WorkItem}/{Agent}/eval at iter {Iter}.",
                        workItemId, evalAgentId, iter);
                }
            }
            else if (evalMemoryEnabled)
            {
                var sessionIdToPersist = !string.IsNullOrEmpty(capturedSessionId)
                    ? capturedSessionId
                    : evalContextForIter.Memory?.SessionId;
                if (!string.IsNullOrEmpty(sessionIdToPersist))
                {
                    try
                    {
                        using var memScope = this.scopeFactory.CreateScope();
                        var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                        await memStore.UpsertAsync(
                            workItemId, evalAgentId, AgentMemoryRoles.Evaluator,
                            backend: evalContext.Backend.ToString().ToLowerInvariant(),
                            invokeMode: evalContext.InvokeMode.ToString().ToLowerInvariant(),
                            sessionId: sessionIdToPersist, messagesJson: null,
                            lastIter: iter, approxInputTokens: 0, ct: iterCt);
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                        {
                            role = "evaluator",
                            agentId = evalAgentId,
                            sessionId = sessionIdToPersist,
                            iter,
                        }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger?.LogWarning(ex,
                            "AgentMemoryStore.UpsertAsync failed for {WorkItem}/{Agent}/eval at iter {Iter}.",
                            workItemId, evalAgentId, iter);
                    }
                }
            }

            // Phase 42 -- fire the per-iter post-completion hook AFTER the
            // evaluator has produced its text. The file's on disk; the
            // architect plan path uses this to commit the doc per iter.
            // Phase 81/83 -- GitIgnoredPathException MUST bubble so the
            // orchestrator halts at iter 1 instead of burning N iterations of
            // unrecoverable agent work. Other exceptions remain best-effort.
            if (onIterationCompleted is not null)
            {
                await SafeInvokeIterationCallbackAsync(
                    () => onIterationCompleted(iter, "post-eval"),
                    this.logger, workItemId, iter);
            }

            return new EvaluatorRunOutput(
                output.ToString(),
                cost,
                inTok,
                outTok,
                TurnSamplesJson: turnSamples.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(turnSamples),
                CacheReadTokens: evalCacheRead > 0 ? evalCacheRead : null,
                CacheWriteTokens: evalCacheWrite > 0 ? evalCacheWrite : null,
                ReasoningTokens: evalReasoning > 0 ? evalReasoning : null,
                ConversationTokens: evalConversationLatest > 0 ? evalConversationLatest : null,
                SessionId: capturedSessionId);
        }

        // Phase 44 -- claim agents before starting the gen/eval loop.
        await this.slots.TryClaimAsync(genAgentId, workItemId, "generator", ct);
        if (evalAgentId is not null)
        {
            await this.slots.TryClaimAsync(evalAgentId, workItemId, "evaluator", ct);
        }

        DeliverableLoopResult result;
        try
        {
            // Phase 56 -- wire pass-validator for impl-phase loops that
            // have a resolvable build / quality / test command set.
            // Architect / decompose / QA / review-fix phases skip the
            // validator entirely (their post-pass-of-generated-doc is
            // commit-the-file, not run-the-gates), so the loop behaves
            // exactly as before for them. For impl phases the validator
            // re-runs the unified gate chain (now identical to what the
            // per-iter gate runs every iter) and rejects pass on any
            // failing stage, falling back into iter N+1 with the
            // failure carried forward through the deliverable Notes →
            // priorChain → next-iter evaluator prompt path.
            Func<int, CancellationToken, Task<string?>>? onPassValidate = null;
            if (isImplPhase &&
                (!string.IsNullOrWhiteSpace(buildCommand)
                 || (qualityCommands is { Count: > 0 })
                 || !string.IsNullOrWhiteSpace(testCommand)))
            {
                onPassValidate = async (validIter, validCt) =>
                {
                    // 2026-06-03 — refresh the worktree to the CURRENT
                    // origin/{baseBranch} before the post-pass gate scores
                    // it, so a sibling workstream that squash-merged mid-drive
                    // is incorporated and cross-file symbol collisions are
                    // caught here (pass→iterate) instead of breaking the base
                    // build after merge. Best-effort: a throw here must not
                    // sink the pass — log and gate the pre-merge tree (no
                    // regression vs the pre-2026-06-03 behavior).
                    if (refreshBaseBeforePostPassGate is not null)
                    {
                        try
                        {
                            await refreshBaseBeforePostPassGate(validIter, validCt);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            this.logger?.LogWarning(ex,
                                "Pass-time base refresh threw for wi={Wi} iter={Iter}; "
                                + "proceeding with post-pass gate on the current worktree.",
                                workItemId, validIter);
                        }
                    }

                    var failure = await this.RunGateChainAsync(
                        workItemId,
                        workspace,
                        validIter,
                        stagePrefix: "post-pass",
                        buildCommand,
                        qualityCommands,
                        testCommand,
                        agentId: genAgentId,
                        crewId: crew.Id,
                        ct: validCt,
                        testBaseRef: testBaseRef,
                        testImpactedOnly: testImpactedOnly,
                        buildTimeout: buildTimeout,
                        testTimeout: testTimeout,
                        initCommand: initCommand,
                        restoreCommand: restoreCommand,
                        runTestsElevated: runTestsElevated);

                    // Phase 92 — durable mirror of the post-pass gate result onto the
                    // workstream row. This is the gate a completing workstream ends on,
                    // so its BUILD/TEST badges stay correct after the workstream finishes
                    // and its transcript gate events are pruned.
                    await this.PersistGateOutcomeAsync(workItemId, buildCommand, testCommand, failure, validCt);

                    if (failure is null)
                    {
                        // All gates green at pass-time: accept the pass.
                        // Also clear lastGateFailure so any subsequent
                        // operations on this loop (none today, but
                        // future hooks) don't see stale state.
                        return null;
                    }

                    // Host process-init failure (e.g. 0xC0000142) at post-pass
                    // time — same root cause as the per-iter branch above. The
                    // toolchain never ran, so a recovery attempt would re-run the
                    // same host failure; don't consume one. Stall immediately
                    // with the host-specific reason.
                    if (failure.Result.IsHostLaunchFailure)
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.gate.recovery-exhausted", new
                        {
                            iter = validIter,
                            attempts = postPassRecoveryAttempts,
                            max = MaxRecoveryAttempts,
                            reason = "host-process-init-failure",
                            gateStage = failure.Stage,
                            command = failure.Command,
                            exitCode = failure.Result.ExitCode,
                        }, agentId: genAgentId, crewId: crew.Id, ct: validCt);

                        throw new BuildFailedException(
                            $"Workstream {workItemId} post-pass {failure.Stage} gate (`{failure.Command}`) exited "
                            + $"{failure.Result.ExitCode} "
                            + $"(0x{unchecked((uint)failure.Result.ExitCode):X8}) — a Windows process-INITIALIZATION "
                            + "failure (final iter " + validIter + "). The toolchain process never ran; this is a HOST "
                            + "environment problem (commonly leaked build-process trees exhausting the Forge.Api "
                            + "host's desktop heap), NOT a defect in the diff. Re-running won't help — stalling for "
                            + "operator triage. Fix: clear leaked build processes / restart the Forge.Api host; a "
                            + $"workstream Restart will NOT resolve it.\n\n{failure.Result.ErrorSummary}")
                        {
                            ExitCode = failure.Result.ExitCode,
                            ErrorCount = failure.Result.ErrorCount,
                            WarningCount = failure.Result.WarningCount,
                            ErrorSummary = failure.Result.ErrorSummary,
                            TrippedByConsecutiveIters = false,
                            ConsecutiveBrokenIters = 0,
                            LastIteration = validIter,
                            Stage = failure.Stage,
                            Command = failure.Command,
                            HostLaunchFailure = true,
                        };
                    }

                    postPassRecoveryAttempts++;
                    await this.taskChannel.EmitAsync(workItemId, "task.gate.recovery-attempted", new
                    {
                        iter = validIter,
                        attempt = postPassRecoveryAttempts,
                        max = MaxRecoveryAttempts,
                        gateStage = failure.Stage,
                        command = failure.Command,
                        exitCode = failure.Result.ExitCode,
                        errorCount = failure.Result.ErrorCount,
                        warningCount = failure.Result.WarningCount,
                        timedOut = failure.Result.TimedOut,
                    }, agentId: genAgentId, crewId: crew.Id, ct: validCt);

                    if (failure.Stage == "test" && postPassRecoveryAttempts >= MaxRecoveryAttempts)
                    {
                        // TEST gate is BLOCKING but NOT TERMINAL (operator policy
                        // 2026-07-01): a red TEST gate must never ship a PR AND must
                        // never dead-end the workstream to "operator must Restart". Past
                        // the recovery budget we DON'T throw — we fall through to the
                        // demote-to-iterate return below so the loop keeps working the
                        // failing tests (never accepting the pass, so nothing red ships).
                        // The normal MaxIter / budget / time caps are the backstop.
                        // BUILD / QUALITY exhaustion still hard-stalls (else branch).
                        if (postPassRecoveryAttempts == MaxRecoveryAttempts)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.gate.test-non-terminal", new
                            {
                                stage = "post-pass",
                                gateStage = failure.Stage,
                                iter = validIter,
                                attempts = postPassRecoveryAttempts,
                                max = MaxRecoveryAttempts,
                                reason = "test-gate-blocking-non-terminal-keep-iterating",
                                command = failure.Command,
                                exitCode = failure.Result.ExitCode,
                                errorCount = failure.Result.ErrorCount,
                            }, agentId: genAgentId, crewId: crew.Id, ct: validCt);
                        }
                    }
                    else if (postPassRecoveryAttempts >= MaxRecoveryAttempts)
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.gate.recovery-exhausted", new
                        {
                            iter = validIter,
                            attempts = postPassRecoveryAttempts,
                            max = MaxRecoveryAttempts,
                            gateStage = failure.Stage,
                            command = failure.Command,
                        }, agentId: genAgentId, crewId: crew.Id, ct: validCt);

                        throw new BuildFailedException(
                            $"Workstream {workItemId} post-pass {failure.Stage} gate ({failure.Command}) "
                            + $"failed on {postPassRecoveryAttempts} consecutive pass attempts (final iter "
                            + $"{validIter}, exit {failure.Result.ExitCode}, {failure.Result.ErrorCount} "
                            + $"error(s)). The gen/eval loop kept reaching score≥90 but the post-pass gate "
                            + $"kept rejecting the diff. Operator must Restart.\n\n{failure.Result.ErrorSummary}")
                        {
                            ExitCode = failure.Result.ExitCode,
                            ErrorCount = failure.Result.ErrorCount,
                            WarningCount = failure.Result.WarningCount,
                            ErrorSummary = failure.Result.ErrorSummary,
                            TrippedByConsecutiveIters = false,
                            ConsecutiveBrokenIters = 0,
                            LastIteration = validIter,
                            Stage = failure.Stage,
                            Command = failure.Command,
                        };
                    }

                    // Seed lastGateFailure so the GeneratorDelegate's
                    // prompt-prepend on the NEXT iter sees the failing
                    // gate as if it had tripped per-iter. The per-iter
                    // gate on the next iter will overwrite this once
                    // the agent produces a new diff.
                    lastGateFailure = failure;

                    return BuildGateRejectionNote(failure, postPassRecoveryAttempts, MaxRecoveryAttempts);
                };
            }

            result = await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: workItemId,
                CrewId: crew.Id,
                GeneratorAgentId: genAgentId,
                EvaluatorAgentId: evalAgentId ?? genAgentId,
                Generator: GeneratorDelegate,
                Evaluator: EvaluatorDelegate,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                EvaluatorCrewId: evalCrewId,
                WorkItemPoints: storyPoints,
                OnPassValidate: onPassValidate,
                OnIterationCommit: onIterationCommit), ct);
        }
        finally
        {
            // Phase 44 -- always release agents, even on error/cancel.
            await this.slots.ReleaseAsync(genAgentId, workItemId, ct: CancellationToken.None);
            if (evalAgentId is not null)
            {
                await this.slots.ReleaseAsync(evalAgentId, workItemId, ct: CancellationToken.None);
            }

            // Phase 55 / Phase E -- release any pooled Copilot/SDK
            // sessions for this work item. A new gen-eval loop
            // (operator Restart, sloppy reassignment, etc.) gets a
            // fresh session; the in-process pool only exists to span
            // iterations within ONE attempt. Best-effort; pool's own
            // catch swallows per-entry dispose failures.
            if (genMemoryEnabled || evalMemoryEnabled)
            {
                try
                {
                    await this.copilotPool.DisposeForWorkItemAsync(workItemId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex,
                        "CopilotSessionPool.DisposeForWorkItemAsync failed for {WorkItem}; entries may leak until process exit.",
                        workItemId);
                }
            }
        }

        // The plan PR ships unattended (no human gate), so anything short of
        // a passing verdict is shipping subpar work. Throw on every non-pass
        // terminal verdict (iterate / stalled / stalled-budget / reject); the
        // caller (StorySpawnOrchestrator.DrivePlanAsync) records the failure
        // on the spawn so the operator sees why the lifecycle halted.
        //
        // Phase 66 exception: `stalled-questions` is a SOFT terminal — the
        // loop is parked awaiting operator answers, not failed. Returning
        // normally (no throw, no commit, no PR-open) lets the orchestrator
        // park the doc / workstream with a blue "awaiting answers" banner
        // instead of the red "orchestrator halted" banner. Once the
        // operator answers via the conversation-tab wizard, the answer
        // endpoint fire-and-forgets a resume; the next call to RunAsync
        // sees unansweredCount == 0 and continues at iter N+1.
        if (string.Equals(result.FinalVerdict, "stalled-questions", StringComparison.OrdinalIgnoreCase))
        {
            this.logger?.LogInformation(
                "Phase 66: {Phase} parked on stalled-questions at iter {Iter} (score {Score}); awaiting operator answers.",
                phaseTag, result.Iterations, result.FinalScore);
            return false;
        }

        // Phase 74 — DeliverableLoop refused to run because another
        // RunAsync is already in flight for this workItemId. Bail
        // silently: no commit, no PR, no status flip. The in-flight
        // caller owns the turn and will produce the real verdict.
        // Return true so an artifact-materialising caller distinguishes
        // this "another run owns it" no-op from a genuine "agent produced
        // nothing" failure (which must still hard-fail).
        if (string.Equals(result.FinalVerdict, DeliverableLoop.SkippedConcurrentVerdict, StringComparison.OrdinalIgnoreCase))
        {
            this.logger?.LogInformation(
                "Phase 74: {Phase} skipped (concurrent dispatch); another DeliverableLoop run owns this workItemId.",
                phaseTag);
            return true;
        }

        if (!string.Equals(result.FinalVerdict, "pass", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 88 — sloppy verdicts (iter-cap, agent-refusal,
            // score-plateau, token-cap, consecutive-zero,
            // no-progress-score-zero) get a typed exception so the
            // qa-e2e driver can branch to PendingReassign. Non-sloppy
            // fatal verdicts (stalled from generator transport failure,
            // etc.) still surface as the generic
            // InvalidOperationException so they need operator
            // attention.
            var msg = FailedLoopMessage(phaseTag, result);
            if (string.Equals(result.FinalVerdict, "sloppy", StringComparison.OrdinalIgnoreCase))
            {
                throw new SloppyVerdictException(result, msg);
            }

            throw new InvalidOperationException(msg);
        }

        // Terminal pass — not skipped; the caller proceeds to commit / PR.
        await this.MarkPlanDocCompletedAsync(workItemId, ct);
        return false;
    }

    /// <summary>
    /// A plan doc's work item runs without a task_queue row, so no queue
    /// completion ever ends the Live badge DeliverableLoop stamps on it. Left
    /// Live, the live-badge sweep later finds it idle with a terminal pass
    /// verdict and flips it to Failed — a doc that passed and merged shows a
    /// red "failed" card. Record the pass as Completed instead. Only plan-doc
    /// items: workstream items complete when their PR exists. Best-effort; a
    /// status write must never fail the doc that was just produced.
    /// </summary>
    private Task MarkPlanDocCompletedAsync(string workItemId, CancellationToken ct)
        => MarkPlanDocCompletedAsync(this.scopeFactory, workItemId, this.logger, ct);

    internal static async Task MarkPlanDocCompletedAsync(
        IServiceScopeFactory scopeFactory, string workItemId, ILogger? logger, CancellationToken ct)
    {
        if (!workItemId.StartsWith("plan-doc:", StringComparison.Ordinal)) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null || string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.Ordinal)) return;
            wi.ExecutionStatus = ExecutionStatuses.Completed;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Could not mark plan doc {WorkItem} completed after its passing verdict.", workItemId);
        }
    }

    /// <summary>
    /// Resolves the agent + invocation context for one role on a crew. When
    /// <paramref name="useEvaluatorRole"/>, prefers <c>DefaultEvaluatorAgentId</c>
    /// + <c>EvaluatorModel</c>; otherwise <c>DefaultGeneratorAgentId</c> /
    /// <c>Lead</c> and the crew's primary model. Throws when the role isn't
    /// configured so callers can fall back (e.g. the gen-eval loop's null
    /// evaluator path).
    ///
    /// <para><b>Exposed as <c>internal static</c></b> so the Phase-50-followup
    /// regression test (<c>Phase50PlanDocEvaluatorOverrideTests</c>) can pin
    /// the resolver's contract against a seeded in-memory DB without
    /// constructing the full strategy. The body has no <c>this.</c> usage
    /// -- it's pure on <paramref name="db"/> + <paramref name="crew"/> +
    /// <c>ForgeSeed.ResolveSoulPath</c> (static) -- so making it static is
    /// safe and keeps the test simple.</para>
    /// </summary>
    internal static async Task<(string AgentId, AgentContext Context, string BackendStr, string InvokeStr)> ResolveAgentRunConfigAsync(
        ControlDbContext control, ProductDbContext db, CrewRow crew, bool useEvaluatorRole,
        string workspace, TimeSpan? budget, CancellationToken ct,
        // 2026-05-13 root-cause fix -- per-task max-turns cap (Phase 21.5
        // semantics: 30 turns per story point). Without this, both the
        // generator and evaluator AgentContext fell back to the global
        // AgentRunnerOptions.MaxTurns = 60, capping a 13-pt workstream's
        // gen at turn 60 of an expected 390. Mirrors the
        // CrewQueueWorker.cs:1250-1252 and 1405/1702 plumbing pattern.
        // Null preserves prior behaviour (callers that don't know the
        // points still work; the dispatcher applies its global default).
        int? maxTurns = null)
    {
        var agentId = useEvaluatorRole
            ? (crew.DefaultEvaluatorAgentId ?? crew.Lead)
            : (crew.DefaultGeneratorAgentId ?? crew.Lead)
            ?? throw new InvalidOperationException(
                $"Crew '{crew.Id}' has no {(useEvaluatorRole ? "Evaluator" : "Generator")} agent set.");

        var agent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var preferEvalModel = useEvaluatorRole;
        var model = preferEvalModel
            ? (NonEmptyOrNull(agent?.EvaluatorModel) ?? NonEmptyOrNull(crew.EvaluatorModel) ?? NonEmptyOrNull(agent?.GeneratorModel) ?? crew.Model)
            : (NonEmptyOrNull(agent?.GeneratorModel) ?? crew.Model);
        if (string.IsNullOrEmpty(model))
        {
            throw new InvalidOperationException($"Crew '{crew.Id}' has no Model set for the {(useEvaluatorRole ? "evaluator" : "generator")} role.");
        }

        var backendStr = !string.IsNullOrEmpty(agent?.Backend) ? agent!.Backend : crew.Backend;
        var backend = string.Equals(backendStr, "claude", StringComparison.OrdinalIgnoreCase)
            ? AgentBackend.Claude : AgentBackend.Copilot;
        var invokeStr = !string.IsNullOrEmpty(agent?.InvokeMode) ? agent!.InvokeMode : crew.InvokeMode;
        var invokeMode = string.Equals(invokeStr, "cli", StringComparison.OrdinalIgnoreCase)
            ? AgentInvokeMode.Cli : AgentInvokeMode.Sdk;

        var soul = string.Empty;
        var resolvedSoul1 = ForgeSeed.ResolveSoulPath(agent?.SoulPath);
        if (!string.IsNullOrEmpty(resolvedSoul1))
        {
            try { soul = await File.ReadAllTextAsync(resolvedSoul1, ct); }
            catch { /* best effort */ }
        }

        var context = new AgentContext(
            CrewId: crew.Id,
            Model: model,
            SoulMd: soul,
            WorktreePath: workspace,
            AllowedTools: Array.Empty<string>(),
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            Backend: backend,
            InvokeMode: invokeMode,
            WallClockBudget: budget,
            MaxTurns: maxTurns);

        return (agentId, context, backendStr, invokeStr);
    }

    private static bool UsesMessageReplayMemory(AgentContext context) =>
        context.Backend == AgentBackend.Claude && context.InvokeMode == AgentInvokeMode.Sdk;

    private static bool UsesProviderSessionMemory(AgentContext context) =>
        context.Backend == AgentBackend.Copilot || context.InvokeMode == AgentInvokeMode.Cli;

    private static string? NonEmptyOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, CancellationToken ct)
        => await this.InvokeAsync(team, workItemId, phaseTag, prompt, workspace, budget: null, ct: ct);

    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct)
        => await this.InvokeAsync(team, workItemId, phaseTag, prompt, workspace, budget, ct: ct, agentIdOverride: null);

    /// <summary>
    /// Phase 49 follow-up -- <paramref name="workItemId"/> (formerly named
    /// <c>spawnId</c>) is the transcript stream the emitted
    /// <c>story.agent-*</c> events land on. Callers running spawn-scoped
    /// work (decompose, qa-features) pass <c>spawn.Id</c>; callers
    /// running workstream-scoped work (review-comments, review-fix,
    /// qa-steps, discover-targets) pass <c>workstream.Id</c> so those
    /// events appear in the workstream's Conversation tab (and the new
    /// Review subtab) instead of disappearing into the spawn-level
    /// stream that no UI surface renders.
    /// </summary>
    /// <summary>
    /// Phase 65 — optional <paramref name="agentIdOverride"/>. When non-null
    /// the invocation targets that specific agent (with config inherited from
    /// the agent's own crew row) instead of the team crew's default generator.
    /// Used by <see cref="ProduceReviewFixAsync"/> to dispatch the workstream's
    /// assigned engineer — the one who originally wrote the code — rather than
    /// the crew-wide default (which was a single-point-of-failure when that
    /// default agent's transport hung).
    /// </summary>
    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct, string? agentIdOverride = null, int? maxTurns = null)
    {
        var prefix = CrewIdPrefix(team);

        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
            ?? throw new InvalidOperationException(
                $"No crew found for team '{team}' (looked for id '{prefix}' or prefix). Configure crews in DB.");

        // Phase 65 — when an override agent is supplied (e.g. the workstream's
        // assigned generator for review-fix), use it instead of the crew
        // default. Validate the agent exists; fall back to crew default when
        // the override is stale / deleted so we don't silently break.
        AgentRow? agent = null;
        string agentId;
        if (!string.IsNullOrEmpty(agentIdOverride))
        {
            agent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentIdOverride, ct);
            if (agent is not null)
            {
                agentId = agentIdOverride;
            }
            else
            {
                this.logger?.LogWarning(
                    "InvokeAsync: agentIdOverride '{Override}' not found in agents table for phase '{Phase}' "
                    + "on workItem '{WiId}'; falling back to crew default.",
                    agentIdOverride, phaseTag, workItemId);
                agentId = crew.DefaultGeneratorAgentId ?? crew.Lead
                    ?? throw new InvalidOperationException(
                        $"Crew '{crew.Id}' has no Lead/DefaultGeneratorAgentId set.");
                agent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
            }
        }
        else
        {
            agentId = crew.DefaultGeneratorAgentId ?? crew.Lead
                ?? throw new InvalidOperationException(
                    $"Crew '{crew.Id}' has no Lead/DefaultGeneratorAgentId set.");
            agent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        }

        // When the override agent belongs to a different crew, inherit
        // model/backend/invokeMode from that crew so runtime config matches
        // the agent's real home (e.g. night-crew agents have different models).
        var configCrew = crew;
        if (agent is not null && !string.IsNullOrEmpty(agent.CrewId)
            && !string.Equals(agent.CrewId, crew.Id, StringComparison.Ordinal))
        {
            var agentCrew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == agent.CrewId, ct);
            if (agentCrew is not null) configCrew = agentCrew;
        }

        var model = !string.IsNullOrEmpty(agent?.GeneratorModel) ? agent!.GeneratorModel
            : !string.IsNullOrEmpty(configCrew.Model) ? configCrew.Model
            : throw new InvalidOperationException($"Crew '{configCrew.Id}' has no Model set.");

        var backendStr = !string.IsNullOrEmpty(agent?.Backend) ? agent!.Backend : configCrew.Backend;
        var backend = string.Equals(backendStr, "claude", StringComparison.OrdinalIgnoreCase)
            ? AgentBackend.Claude : AgentBackend.Copilot;

        var invokeStr = !string.IsNullOrEmpty(agent?.InvokeMode) ? agent!.InvokeMode : configCrew.InvokeMode;
        var invokeMode = string.Equals(invokeStr, "cli", StringComparison.OrdinalIgnoreCase)
            ? AgentInvokeMode.Cli : AgentInvokeMode.Sdk;

        var soul = string.Empty;
        var resolvedSoul2 = ForgeSeed.ResolveSoulPath(agent?.SoulPath);
        if (!string.IsNullOrEmpty(resolvedSoul2))
        {
            try { soul = await File.ReadAllTextAsync(resolvedSoul2, ct); }
            catch { /* best effort */ }
        }

        var context = new AgentContext(
            CrewId: crew.Id,
            Model: model,
            SoulMd: soul,
            // Per-invocation Forge-managed workspace. NOT %TEMP%, NOT the
            // repo root. The agent reads/writes here only.
            WorktreePath: workspace,
            AllowedTools: Array.Empty<string>(),
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            Backend: backend,
            InvokeMode: invokeMode,
            // Hard wall-clock cap so a runaway agent fails fast instead of
            // sitting in the orchestrator queue indefinitely. The runner
            // cancels the SDK session at this cap; we then surface as a
            // failure that the next scheduler tick can retry.
            WallClockBudget: budget,
            // 2026-05-29 root-cause fix — workstream-scoped agents (review,
            // review-fix, qa-steps, decompose, conflict-resolve) fell back to
            // the dispatcher's global MaxTurns=60 because this path never set
            // it, while the impl gen/eval path scales to points*30
            // (ResolveAgentRunConfigAsync). A reviewer reading a large diff
            // (git diff --stat → per-file diff → open files) blows 60 turns and
            // the workstream stalls with "Max turns (60) reached". Callers now
            // pass points*30 so review/qa get the same headroom as gen/eval.
            // Null preserves the prior global-default behaviour.
            MaxTurns: maxTurns);

        await this.taskChannel.EmitAsync(workItemId, "story.agent-prompt", new
        {
            phase = phaseTag,
            team,
            agentId,
            crewId = crew.Id,
            model,
            backend = backendStr,
            invokeMode = invokeStr,
            workspace,
            promptPreview = Trim(prompt, 600),
        }, agentId: agentId, crewId: crew.Id, ct: ct);

        var invocation = await this.runner.StartAsync(workItemId, agentId, prompt, context, ct);

        string? failure = null;
        await foreach (var evt in invocation.Events.WithCancellation(ct))
        {
            await this.taskChannel.EmitAsync(workItemId, $"story.agent-{evt.Kind.ToString().ToLowerInvariant()}", new
            {
                phase = phaseTag,
                team,
                agentId,
                invocationId = invocation.InvocationId,
                text = evt.Text,
                toolName = evt.ToolName,
                toolPayload = evt.ToolPayload,
                error = evt.Error,
                inputTokens = evt.InputTokens,
                outputTokens = evt.OutputTokens,
                costUsd = evt.CostUsd,
            }, agentId: agentId, crewId: crew.Id, ct: ct);

            if (evt.Kind == AgentEventKind.Failed)
            {
                failure = evt.Error ?? "agent reported failure";
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Agent {agentId} (team {team}) failed during phase '{phaseTag}': {failure}");
        }
    }

    // ── Engineer-target discovery (Phase 45 follow-up) ───────────────────────

    /// <summary>
    /// Matches paths the architect typically embeds in plan/workstream prose:
    /// either a backtick-wrapped path (`Foo/Bar.cs`) or a bare path with at
    /// least one separator and a known code-file extension. Restricting to
    /// known extensions filters out URL fragments, stack-trace tails, and
    /// generic "src/" mentions; requiring a separator rules out single
    /// identifiers like `IDistributedCache` that aren't paths.
    /// </summary>
    private static readonly Regex TargetPathRegex = new(
        @"`(?<bt>[A-Za-z0-9_./\\-]+\.(?:cs|csproj|sln|ts|tsx|js|jsx|json|md|yml|yaml|py|go|rs|java|kt|cpp|h|hpp|fs|fsproj|xml|ps1|sh|sql))`"
        + @"|(?<bare>(?<![A-Za-z0-9_])(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+\.(?:cs|csproj|sln|ts|tsx|js|jsx|json|md|yml|yaml|py|go|rs|java|kt|cpp|h|hpp|fs|fsproj|xml|ps1|sh|sql))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extract repo-relative file paths out of free-form architect/operator
    /// prose. Deduplicates, normalises to forward slashes, and skips paths
    /// that are obviously bogus (start with a slash, contain `..`, exceed
    /// 200 chars). Order is preserved so the engineer reads the most
    /// prominent file first.
    /// </summary>
    private static IReadOnlyList<string> ExtractTargetPaths(params string?[] sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            foreach (Match m in TargetPathRegex.Matches(source))
            {
                var raw = m.Groups["bt"].Success ? m.Groups["bt"].Value : m.Groups["bare"].Value;
                var path = raw.Replace('\\', '/').Trim();
                if (path.Length == 0 || path.Length > 200) continue;
                if (path.StartsWith('/')) continue;
                if (path.Contains("..", StringComparison.Ordinal)) continue;
                // Skip the plan-doc context (architecture.md / tech-spec.md /
                // implementation-plan.md / e2e-scenarios.md) -- it's not a code
                // edit target. Covers both the default `docs/stories/` base and
                // a per-repo override that still nests under it (e.g. the
                // OneBranch `src/docs/stories/` — Phase 83).
                if (path.StartsWith("docs/stories/", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains("/docs/stories/", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(path)) ordered.Add(path);
            }
        }
        return ordered;
    }

    /// <summary>
    /// Read the section of the merged <c>implementation-plan.md</c> that
    /// corresponds to this workstream's <see cref="WorkstreamRow.PlanAnchor"/>
    /// (e.g. <c>phase-2/stage-1</c>). Returns the raw markdown for that
    /// stage so target-path extraction can mine it for path mentions.
    /// Returns empty string when the plan can't be read or the anchor
    /// can't be located -- the caller falls back to text-only extraction
    /// and then to the discovery agent.
    /// </summary>
    private async Task<string> ReadPlanStageBodyAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workstream.PlanAnchor)) return string.Empty;
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        // Phase 62 — read implementation-plan.md from its canonical
        // per-doc branch in sequential mode (legacy shared `plan` branch
        // in legacy mode). Matches ProduceDecompositionAsync above.
        var planBranch = PlanDocStateHelpers.FindDocBranch(spawn, "implementation-plan");
        var planPath = $"{StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId)}/implementation-plan.md";
        string? planMd;
        try
        {
            planMd = await this.git.ReadFileAsync(repo, planBranch, planPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex,
                "Could not read implementation-plan.md for workstream {WsId}; skipping plan-stage extraction.",
                workstream.Id);
            return string.Empty;
        }
        if (string.IsNullOrEmpty(planMd)) return string.Empty;

        // Stage anchors are written as `<a id="phase-X/stage-Y"></a>` (or
        // `{#phase-X/stage-Y}` Markdown attribute). Locate the first
        // occurrence; the stage body runs until the next anchor or end of
        // file. Best-effort -- if the anchor format differs, we just return
        // empty and the discovery agent picks up.
        var anchor = workstream.PlanAnchor;
        var startIdx = planMd.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return string.Empty;
        // Find the next anchor (any phase-N/stage-M) after our match;
        // truncate body to that point so we don't bleed into the next stage.
        var nextAnchorMatch = Regex.Match(
            planMd[(startIdx + anchor.Length)..],
            @"phase-\d+/stage-\d+",
            RegexOptions.IgnoreCase);
        var endIdx = nextAnchorMatch.Success
            ? startIdx + anchor.Length + nextAnchorMatch.Index
            : planMd.Length;
        return planMd.Substring(startIdx, endIdx - startIdx);
    }

    /// <summary>
    /// Fallback when regex extraction yields no targets: run a short
    /// read-only architect-style agent that mines the workstream brief +
    /// plan stage for the file paths the engineer should touch first.
    /// Mirrors the <c>ProduceWorkstreamsAsync</c> sandbox pattern -- agent
    /// writes <c>targets.json</c>, we deserialise + return. Single LLM
    /// call with a tight 90-second budget so it doesn't itself eat the
    /// engineer's wall clock.
    /// </summary>
    private async Task<IReadOnlyList<string>> DiscoverEngineerTargetsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream,
        string workItemDescription, string planStageBody, CancellationToken ct)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"discover-{Slug(workstream.Id)}");
        var prompt = $$"""
            You are an Architect helper enumerating the source files an
            engineer should edit to implement workstream `{{workstream.Id}}`
            for story `{{spawn.StoryId}}`.

            ## Workstream
            **{{workstream.Name}}** -- {{workstream.Summary}}

            ## Detailed requirements
            {{workItemDescription}}

            ## Implementation-plan stage (verbatim)
            {{NonEmpty(planStageBody, "_(plan stage not available)_")}}

            ## Your task
            Return the repo-relative file paths the engineer should open
            first to deliver this workstream. Prefer paths the brief or
            plan stage names directly. Do NOT browse the repo, do NOT open
            files -- operate purely from the prose above. Pick at most 8
            paths; quality over quantity.

            ## How to deliver
            Use your filesystem tools to write a SINGLE file named
            `targets.json` inside your working directory. Content must be
            valid JSON conforming to:

            { "targets": ["path/one.cs", "path/two.cs"] }

            Reply `DONE` after writing the file. No other files, no
            subdirectories, no commentary.
            """;

        await this.InvokeAsync("Architect", spawn.Id, $"discover-targets:{workstream.Id}", prompt, workspace,
            TimeSpan.FromSeconds(90), ct);

        var diskPath = Path.Combine(workspace, "targets.json");
        if (!File.Exists(diskPath))
        {
            this.logger?.LogInformation(
                "Discovery agent for {WsId} did not write targets.json; engineer runs without hints.",
                workstream.Id);
            return Array.Empty<string>();
        }
        var json = await File.ReadAllTextAsync(diskPath, ct);
        try
        {
            var parsed = JsonSerializer.Deserialize<DiscoveryTargetsJson>(json, JsonOpts);
            if (parsed?.Targets is null) return Array.Empty<string>();
            // Re-run the same sanitiser ExtractTargetPaths uses so a
            // hallucinated `/etc/passwd` or `..` traversal can't leak
            // through.
            return ExtractTargetPaths(string.Join('\n', parsed.Targets.Select(t => $"`{t}`")));
        }
        catch (JsonException ex)
        {
            this.logger?.LogWarning(ex,
                "Discovery agent for {WsId} produced unparseable targets.json; engineer runs without hints.",
                workstream.Id);
            return Array.Empty<string>();
        }
    }

    private sealed class DiscoveryTargetsJson
    {
        public List<string>? Targets { get; set; }
    }

    /// <summary>
    /// 2026-05-26 — read up to <paramref name="contextLines"/> lines on either
    /// side of <paramref name="line"/> in <paramref name="filePath"/> and
    /// return as a numbered slice suitable for direct prompt embedding.
    /// Returns <c>null</c> on any IO error so callers can degrade gracefully
    /// (the broader file-seed pipeline must tolerate missing slices). Used by
    /// <see cref="ProduceReviewFixAsync"/> to give the engineer instant
    /// visibility into the code around the reviewer's comment without forcing
    /// an extra `view` round-trip.
    /// </summary>
    /// <summary>
    /// Phase 2 (2026-05-26) helper for <see cref="ProduceReviewFixAsync"/>:
    /// produce a numbered ±<paramref name="contextLines"/> slice around
    /// <paramref name="line"/> in <paramref name="filePath"/>. Used to embed
    /// a "## Code around line N" section directly in the engineer-fix prompt
    /// so the agent doesn't burn its wall-clock budget hunting the file.
    ///
    /// <para>Best-effort: any IO error returns <c>null</c> rather than
    /// propagating, because the caller falls back to a prompt without the
    /// slice (legacy "engineer hunts" behaviour). <paramref name="line"/>
    /// values ≤ 0 anchor at the file head; values past the file's last line
    /// clamp to the last line.</para>
    ///
    /// <para>Exposed as <c>internal</c> so
    /// <c>Phase90ReviewFixSeedAndCircuitBreakerTests</c> can exercise the
    /// edge cases (empty file, missing file, anchor clamping) without
    /// standing up an entire strategy.</para>
    /// </summary>
    internal static async Task<string?> TryBuildSliceAroundLineAsync(
        string filePath, int line, int contextLines, CancellationToken ct)
    {
        try
        {
            var allLines = await File.ReadAllLinesAsync(filePath, ct);
            if (allLines.Length == 0) return null;
            // Clamp `line` into [1..count]. `comment.Line` can be 0 when the
            // reviewer attached a file-level comment (no anchor); anchor the
            // slice at the file head in that case so we still emit something
            // useful.
            var anchor = line > 0 ? Math.Min(line, allLines.Length) : 1;
            var start = Math.Max(1, anchor - contextLines);
            var end = Math.Min(allLines.Length, anchor + contextLines);
            var sb = new StringBuilder();
            for (var i = start; i <= end; i++)
            {
                sb.Append(i.ToString().PadLeft(4)).Append(": ").AppendLine(allLines[i - 1]);
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Phase 2 (2026-05-26) helper for <see cref="ProduceReviewFixAsync"/>:
    /// copy the file under review from the on-disk worktree into the
    /// engineer's scratch <paramref name="workspace"/> at the SAME relative
    /// <paramref name="safePath"/>, and produce a numbered slice around
    /// <paramref name="line"/> for direct prompt embedding.
    ///
    /// <para>The return tuple matches the three states the prompt-builder
    /// distinguishes:</para>
    /// <list type="bullet">
    ///   <item><c>(WorktreeHintPath=path, SeededFileAbsent=false, SeededFileSlice=...)</c>
    ///     — file was copied; prompt renders "## File context (already seeded)" +
    ///     "## Code around line N".</item>
    ///   <item><c>(WorktreeHintPath=path, SeededFileAbsent=true, SeededFileSlice=null)</c>
    ///     — worktree exists but the named file does not (rename / move);
    ///     prompt renders the "moved or renamed" warning section.</item>
    ///   <item><c>(WorktreeHintPath=null, …)</c> — no worktree on disk (PR
    ///     branch already cleaned up); prompt has no file-context section
    ///     and the engineer falls back to legacy filesystem search.</item>
    /// </list>
    ///
    /// <para>Pure filesystem semantics — no DB, no logger. The caller in
    /// <see cref="ProduceReviewFixAsync"/> wraps this in a try/catch that
    /// downgrades any IO error to a warning log + legacy fallback, so this
    /// helper does NOT need its own try/catch around the copy.</para>
    /// </summary>
    internal static async Task<(string? WorktreeHintPath, bool SeededFileAbsent, string? SeededFileSlice)>
        TrySeedReviewFileAsync(
            string workspace,
            string? resolvedWorktreePath,
            string safePath,
            int line,
            CancellationToken ct)
    {
        if (string.IsNullOrEmpty(resolvedWorktreePath) || !Directory.Exists(resolvedWorktreePath))
        {
            return (null, false, null);
        }
        var sourceFile = Path.Combine(resolvedWorktreePath, safePath);
        if (!File.Exists(sourceFile))
        {
            return (resolvedWorktreePath, true, null);
        }
        var seedTarget = Path.Combine(workspace, safePath);
        var seedDir = Path.GetDirectoryName(seedTarget);
        if (!string.IsNullOrEmpty(seedDir)) Directory.CreateDirectory(seedDir);
        File.Copy(sourceFile, seedTarget, overwrite: true);
        var slice = await TryBuildSliceAroundLineAsync(sourceFile, line, contextLines: 25, ct);
        return (resolvedWorktreePath, false, slice);
    }

    // ── Filesystem helpers ───────────────────────────────────────────────────

    /// <summary>Read every file under <paramref name="root"/> recursively into a relative-path → content map.</summary>
    internal static IReadOnlyDictionary<string, string> ReadAllFiles(string root)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return dict;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            // Phase 43 -- exclude operator-uploaded attachments and any
            // local git-exclude artifacts that producers may have written
            // into a scratch workspace. These are input context for the
            // agent, never deliverables, and must not leak into committed
            // PR diffs (e.g. tests/Features/.../.forge-attachments/spec.md).
            if (rel.StartsWith(".forge-attachments/", StringComparison.Ordinal)) continue;
            // Forge iteration scratch (iter-notes, conversation notes).
            if (rel.StartsWith(".forge/", StringComparison.Ordinal)) continue;
            // Local git metadata. The producer scratch workspace
            // (`storyworkspaces/{spawnId}/{kind}-…`) can end up with a real
            // `.git/` directory when an agent tool runs `git init` (or any
            // command that implicitly creates one, e.g. some `dotnet new`
            // templates). Walking those files into the returned dict feeds
            // them to `RealStoryGitOps.CommitFilesAsync`, which then calls
            // `Directory.CreateDirectory($"{wtPath}\.git")` — and that throws
            // `IOException: a file or directory with the same name already
            // exists` because the REAL worktree's `.git` is the 97-byte
            // git-worktree pointer FILE, not a directory. Observed
            // code-intelligence:CLEAN-CODE stage 9.2-e2e 2026-05-28: after
            // iter 4 passed at score 92, every post-pass spawn looped on
            // this exception until the operator unstuck.
            if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            // Build output the gate (`dotnet test` / `go test` / …) wrote into
            // the workspace — bin/, obj/, target/, node_modules/, etc. These
            // are never deliverables; without this filter a blanket
            // `git add -A` at commit time lands generated binaries in the PR
            // (smartpcr/code-intelligence#51: 276 files, mostly tests/bin/...).
            if (BuildArtifactPaths.IsBuildArtifact(rel)) continue;
            dict[rel] = File.ReadAllText(path);
        }
        return dict;
    }

    private static readonly Regex SlugRegex = new(@"[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private static string Slug(string s) => SlugRegex.Replace(s, "-").Trim('-');

    /// <summary>Filesystem-safe slug: keep ascii alnum / dash / underscore / dot.</summary>
    private static string SlugForFs(string s) => SlugRegex.Replace(s, "_").Trim('_');

    private static string NonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        }
        return string.Empty;
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    // ── JSON DTOs ────────────────────────────────────────────────────────────

    private sealed class DecomposeJsonRow
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }
        public List<string>? Deps { get; set; }
        public List<string>? Deliverables { get; set; }
        public string? Timeline { get; set; }
        public string? EstimatedEffort { get; set; }
    }

    private sealed class ReviewCommentJsonRow
    {
        public string? FilePath { get; set; }
        public int? Line { get; set; }
        public string? Body { get; set; }
        public string? Severity { get; set; }
    }

    /// <summary>
    /// Phase 38 (Layer B.3) -- fetch the WorkItem.Points value the gen↔eval
    /// loop should size its iteration budget against. Returns
    /// <paramref name="fallback"/> when the row hasn't been inserted yet
    /// (legacy spawns that predate <c>EnsurePlanDocWorkItemsAsync</c> /
    /// <c>Phase38Migration</c>).
    /// </summary>
    private async Task<int> LookupWorkItemPointsAsync(string workItemId, int fallback, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var pts = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => (int?)w.Points)
            .FirstOrDefaultAsync(ct);
        return pts is > 0 ? pts.Value : Math.Max(1, fallback);
    }

    /// <summary>
    /// Phase 43 (extended) -- copies operator-uploaded attachments from the
    /// canonical storage dir (<c>E:\forge\attachments\{uploadId}\</c>) into
    /// the agent's working directory at <c>.forge-attachments/</c> so the
    /// agent can read them with its file tools. The target dir is rebuilt
    /// from scratch on each call so replaced/deleted files stay current.
    /// </summary>
    /// <param name="attachmentsJson">
    /// Caller-supplied snapshot of <see cref="StorySpawnRow.AttachmentsJson"/>.
    /// Passing the value (instead of the row) prevents accidental mutation of
    /// the orchestrator's tracked spawn instance and lets callers refresh from
    /// the DB without dragging the whole row through the call chain.
    /// </param>
    /// <param name="worktreePath">
    /// Either a real git worktree root or a scratch dir from
    /// <see cref="PrepareWorkspace"/>. Determines whether the helper writes
    /// to <c>.git/info/exclude</c> (worktree) or skips the exclude step.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="excludeMode">
    /// <see cref="AttachmentExcludeMode.GitInfoExclude"/> adds
    /// <c>.forge-attachments/</c> to <c>.git/info/exclude</c> for the
    /// worktree's git dir (per-worktree local exclude, never committed --
    /// keeps the workstream PR diff free of unrelated <c>.gitignore</c>
    /// mods). <see cref="AttachmentExcludeMode.None"/> is the right
    /// default for non-repo scratch dirs.
    /// </param>
    internal static async Task<IReadOnlyList<string>> CopyAttachmentsToWorktreeAsync(
        string? attachmentsJson,
        string worktreePath,
        CancellationToken ct,
        AttachmentExcludeMode excludeMode = AttachmentExcludeMode.GitInfoExclude,
        string? stateDir = null)
    {
        var paths = new List<string>();

        List<AttachmentEntry>? entries = null;
        if (!string.IsNullOrEmpty(attachmentsJson))
        {
            try { entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(attachmentsJson, CamelCase); }
            catch { entries = null; }
        }

        // 2026-06-03 — ALWAYS reconcile the operator inbox to exactly the
        // operator manifest. `.forge-attachments/` is operator-write-only by
        // contract; the orchestrator is the only legitimate writer (it copies
        // operator-uploaded reference docs here). Any OTHER file in this dir
        // was authored by the engineer agent -- e.g. a self-produced
        // "gate-output-*.log" written to satisfy a phantom attachment pin --
        // and MUST be scrubbed so the evaluator can never read a fabricated
        // "operator artifact". Deleting the dir up front (even when the
        // manifest is empty) guarantees the inbox holds EXACTLY the
        // operator-uploaded files, or nothing at all. The previous
        // early-return-on-empty path skipped this scrub, which let the
        // engineer's self-made files persist and accumulate across iters.
        var targetDir = Path.Combine(worktreePath, ".forge-attachments");
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort */ }
        }

        if (entries is null || entries.Count == 0)
        {
            // No operator attachments. Still register the git exclude so a dir
            // the agent re-creates mid-iter can't pollute the PR diff, then
            // leave the inbox absent.
            if (excludeMode == AttachmentExcludeMode.GitInfoExclude)
            {
                await TryAddGitInfoExcludeAsync(worktreePath, ".forge-attachments/", ct);
            }
            return paths;
        }

        Directory.CreateDirectory(targetDir);

        // Per-worktree local exclude. Avoids modifying the tracked
        // .gitignore (which `git add -A` in CommitFilesAsync would
        // otherwise commit into the workstream PR). `info/exclude` lives
        // under the worktree's git dir and is never versioned.
        if (excludeMode == AttachmentExcludeMode.GitInfoExclude)
        {
            await TryAddGitInfoExcludeAsync(worktreePath, ".forge-attachments/", ct);
        }

        // Phase A — production callers must pass `stateDir` (resolved
        // through IForgeDbPaths.Folder). The FORGE_DB_PATH env-var
        // fallback is preserved for the few test sites that haven't
        // migrated yet; once those move to explicit `stateDir` the
        // env-var branch (and its hardcoded path literal) can be
        // removed entirely.
        var dbDir = stateDir
            ?? Path.GetDirectoryName(Environment.GetEnvironmentVariable("FORGE_DB_PATH"))
            ?? throw new InvalidOperationException(
                "CopyAttachmentsToWorktreeAsync: `stateDir` parameter is required when FORGE_DB_PATH " +
                "env var is unset. Production callers resolve this via IForgeDbPaths.Folder.");

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(dbDir, entry.StoragePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath)) continue;

            var destPath = Path.Combine(targetDir, entry.FileName);
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                paths.Add($".forge-attachments/{entry.FileName}");
            }
            catch { /* best-effort -- agent can still work without this file */ }
        }

        return paths;
    }

    /// <summary>
    /// 2026-06-03 — parses the operator attachment manifest JSON into the
    /// set of expected file names: the ONLY files allowed to exist under
    /// the worktree's <c>.forge-attachments/</c> operator inbox. Returns an
    /// empty set when the manifest is null / blank / malformed (which the
    /// scrub treats as "operator attached nothing -> remove everything").
    /// </summary>
    internal static HashSet<string> ParseAttachmentManifestFileNames(string? attachmentsJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(attachmentsJson)) return names;
        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(attachmentsJson, CamelCase);
            if (entries is not null)
            {
                foreach (var e in entries)
                {
                    if (!string.IsNullOrEmpty(e.FileName)) names.Add(e.FileName);
                }
            }
        }
        catch { /* malformed -> empty manifest -> scrub everything */ }
        return names;
    }

    /// <summary>
    /// 2026-06-03 — deterministically removes any file under the worktree's
    /// <c>.forge-attachments/</c> operator inbox that is NOT part of the
    /// operator upload manifest. The inbox is operator-write-only by
    /// contract; any other file there was authored by the engineer agent
    /// (e.g. a self-produced <c>gate-output-*.log</c> written to satisfy a
    /// phantom attachment pin) and must never be visible to the evaluator
    /// as an "operator artifact". Run immediately before the evaluator reads
    /// the worktree so a SAME-iter fabrication cannot bait the score. When
    /// the manifest is empty the whole inbox is engineer-authored, so the
    /// directory is removed wholesale. Best-effort: file-system races/locks
    /// are swallowed.
    /// </summary>
    internal static void ScrubNonManifestAttachments(string worktreePath, string? attachmentsJson)
    {
        if (string.IsNullOrEmpty(worktreePath)) return;
        var targetDir = Path.Combine(worktreePath, ".forge-attachments");
        if (!Directory.Exists(targetDir)) return;

        var manifest = ParseAttachmentManifestFileNames(attachmentsJson);
        if (manifest.Count == 0)
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort */ }
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                if (manifest.Contains(Path.GetFileName(file))) continue;
                try { File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
        catch { /* enumeration race -- best-effort */ }
    }

    /// <summary>
    /// For a turn that changed no file: when the generator declared background
    /// work in <c>.forge/waiting.json</c> that is still running, hold here (no
    /// agent runs) until it ends, and return true so the turn does not count
    /// toward the zero-change stall. Returns false (the turn counts) when there
    /// is no declaration, it does not parse, every job is already done, or the
    /// hold reaches the declared limit.
    /// </summary>
    private async Task<bool> HoldOnDeclaredBackgroundWorkAsync(
        string workItemId, int iter, string workspace, string? agentId, string crewId, CancellationToken ct)
    {
        var wait = BackgroundWait.Read(workspace, out var error);
        if (wait is null)
        {
            if (error is not null)
            {
                await this.taskChannel.EmitAsync(workItemId, "task.waiting.invalid", new
                {
                    iter,
                    error,
                    note = $"{BackgroundWait.RelativePath} was ignored, so this turn counts toward the no-progress stall.",
                }, agentId: agentId, crewId: crewId, ct: ct);
            }

            return false;
        }

        bool IsDone(BackgroundJob j) => BackgroundWait.IsDone(j, BackgroundWait.ProcessAlive, BackgroundWait.ReadLogTail);
        var running = wait.Jobs.Where(j => !IsDone(j)).Select(j => j.Name).ToList();
        if (running.Count == 0 || DateTimeOffset.UtcNow >= wait.Deadline)
        {
            // Stale: the agent ended a turn with nothing running and nothing changed.
            return false;
        }

        await this.taskChannel.EmitAsync(workItemId, "task.waiting.background", new
        {
            iter,
            reason = wait.Reason,
            running,
            deadline = wait.Deadline,
            note = "The agent changed nothing and is waiting on background work it declared, so Forge holds the next iteration until that work ends.",
        }, agentId: agentId, crewId: crewId, ct: ct);

        var outcome = await wait.HoldAsync(
            IsDone,
            () => DateTimeOffset.UtcNow,
            (d, token) => Task.Delay(d, token),
            poll: TimeSpan.FromSeconds(30),
            heartbeat: TimeSpan.FromMinutes(5),
            onHeartbeat: pending => this.taskChannel.EmitAsync(workItemId, "task.waiting.heartbeat", new
            {
                iter,
                running = pending,
            }, agentId: agentId, crewId: crewId, ct: ct),
            ct);

        await this.taskChannel.EmitAsync(workItemId, "task.waiting.finished", new
        {
            iter,
            timedOut = outcome.TimedOut,
            stillRunning = outcome.Pending,
            waitedSeconds = (long)outcome.Waited.TotalSeconds,
        }, agentId: agentId, crewId: crewId, ct: ct);

        this.logger?.LogInformation(
            "Background wait for {WorkItem} iter {Iter} ended after {Waited:g} (timed out: {TimedOut}).",
            workItemId, iter, outcome.Waited, outcome.TimedOut);
        return !outcome.TimedOut;
    }

    /// archived working-notes history so the next iter's prompt sees
    /// every prior iter's reflection, not just the last one. Scans
    /// <c>.forge/notes/iter-*.md</c>, sorts by iter descending (newest
    /// first), and concatenates with <c>### Iteration N</c> headers,
    /// hard-capped at 16 KB. When the cap forces older iters out, an
    /// explicit truncation marker tells the agent how many were
    /// dropped so it can re-derive context from code if it needs to.
    ///
    /// <para>2026-05-13 (Phase 60) — add an explicit iter-count cap
    /// (most-recent 3 entries) BEFORE the byte cap to bound per-turn
    /// prompt growth across long-running loops. When entries are
    /// dropped, list their iter numbers so the agent knows there's
    /// older context it could re-derive from the file/PR history.
    /// The byte cap remains as a defense-in-depth tail.</para>
    ///
    /// <para>Backward compat: when no archive exists yet but the
    /// legacy single-file <c>.forge/iter-notes.md</c> is present
    /// (workstreams mid-flight at the A.1 → A.2 boundary), fall through
    /// to single-iter mode so continuity isn't lost on the transition.
    /// The next iter's <see cref="GeneratorDelegate"/> archive step
    /// will start populating <c>.forge/notes/</c> normally from there.</para>
    /// </summary>
    internal static async Task<string> ReadPriorIterNotesAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workspace)) return string.Empty;

        const int TotalCap = 16_000;
        // Phase 60 — keep only the most-recent N iter-notes entries so
        // prompt growth is bounded predictably independent of total
        // iter count. The Copilot SDK already passes full conversation
        // history each turn within a session; on top of that, this
        // archive grows monotonically across sessions. Capping at 3
        // gives the agent ~current iter context without unbounded
        // growth contributing to `generator-max-turns` failures on
        // long-running plan-doc loops.
        const int MaxArchiveEntries = 3;
        var archiveDir = Path.Combine(workspace, ".forge", "notes");
        var entries = new List<(int Iter, string Path)>();
        if (Directory.Exists(archiveDir))
        {
            foreach (var file in Directory.GetFiles(archiveDir, "iter-*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dashIdx = name.IndexOf('-');
                if (dashIdx >= 0
                    && int.TryParse(name[(dashIdx + 1)..], out var n)
                    && n > 0)
                {
                    entries.Add((n, file));
                }
            }
        }

        if (entries.Count == 0)
        {
            // A.1 fallback for in-flight workstreams.
            var legacyPath = Path.Combine(workspace, ".forge", "iter-notes.md");
            if (!File.Exists(legacyPath)) return string.Empty;
            try
            {
                var content = await File.ReadAllTextAsync(legacyPath, ct);
                if (string.IsNullOrWhiteSpace(content)) return string.Empty;
                if (content.Length > TotalCap)
                {
                    content = content[..TotalCap]
                        + "\n…(truncated; full notes in .forge/iter-notes.md)";
                }
                return content;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        entries.Sort((a, b) => b.Iter.CompareTo(a.Iter));

        // Phase 60 — apply count cap. Stash dropped iter numbers so we
        // can surface them as a tail marker; the agent can re-derive
        // earlier context from code/PR history if it needs them.
        List<int>? droppedByCount = null;
        if (entries.Count > MaxArchiveEntries)
        {
            droppedByCount = entries.Skip(MaxArchiveEntries).Select(e => e.Iter).ToList();
            entries = entries.Take(MaxArchiveEntries).ToList();
        }

        var sb = new StringBuilder();
        var included = 0;
        var truncatedByBytes = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var (n, file) = entries[i];
            string body;
            try
            {
                body = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(body)) continue;
            body = body.TrimEnd();
            var section = $"### Iteration {n}\n\n{body}\n\n";
            if (sb.Length + section.Length > TotalCap)
            {
                var dropped = entries.Count - included;
                if (dropped > 0)
                {
                    sb.AppendLine($"_(truncated: {dropped} earlier iteration(s) omitted to keep this section under {TotalCap / 1000} KB; re-derive earlier context from code if needed.)_");
                }
                truncatedByBytes = true;
                break;
            }
            sb.Append(section);
            included++;
        }

        // Phase 60 — append a separate marker for entries dropped by the
        // count cap. Distinct from the byte-cap truncation marker above
        // so the agent (and future debuggers) can distinguish whether
        // they ran out of room or out of recent history.
        if (!truncatedByBytes && droppedByCount is { Count: > 0 })
        {
            var iterList = string.Join(", ", droppedByCount);
            sb.AppendLine();
            sb.AppendLine($"_(only the most-recent {MaxArchiveEntries} iteration(s) are included; earlier iter(s) omitted: {iterList}. Re-derive earlier context from code/PR history if needed.)_");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Phase 82.2 -- wraps the raw prior-iter-notes content from
    /// <see cref="ReadPriorIterNotesAsync"/> in the canonical boxed
    /// banner block. Returns empty when the workspace has no notes yet.
    ///
    /// <para>Extracted from <c>BuildIterPromptAsync</c> so
    /// <see cref="CrewQueueWorker"/> (which doesn't inherit from this
    /// strategy) can reuse the exact same formatting on impl-path
    /// prompts. Without it, audit goal #2 (prior-iter context) is
    /// honoured only on plan-doc + qa-e2e generator prompts; impl-path
    /// generators re-discover the codebase every iter.</para>
    ///
    /// <para>Caller is responsible for appending a separator
    /// (────...) + blank line after the block when concatenating with
    /// downstream prompt sections.</para>
    /// </summary>
    internal static async Task<string> BuildPriorIterNotesPromptBlockAsync(string workspace, CancellationToken ct)
    {
        var raw = await ReadPriorIterNotesAsync(workspace, ct);
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  NOTES FROM PRIOR ITERATIONS (your own working state)    ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("You wrote these notes across the prior iterations of this");
        sb.AppendLine("workstream, newest first. The agent sessions that produced");
        sb.AppendLine("them are gone -- this archive is your only memory of what");
        sb.AppendLine("you already learned, decided, or tried. Forge appends a new");
        sb.AppendLine("entry every iter; you do NOT need to copy prior entries");
        sb.AppendLine("forward yourself.");
        sb.AppendLine();
        sb.AppendLine(raw);
        sb.AppendLine();
        sb.AppendLine("Overwrite `.forge/iter-notes.md` with THIS iter's reflection");
        sb.AppendLine("only -- Forge will archive it and prepend the cumulative");
        sb.AppendLine("history to your next prompt.");
        return sb.ToString().TrimEnd();
    }
    /// under whatever git dir the worktree maps to -- `git worktree add`
    /// sub-dirs point at <c>{cloneRoot}/.git/worktrees/{name}/</c>, the
    /// main checkout at <c>{cloneRoot}/.git/</c>) and appends the pattern
    /// if not already present. Best-effort: any failure is swallowed so
    /// the agent at least sees the attachment files even if the exclude
    /// can't be written.
    /// </summary>
    internal static async Task TryAddGitInfoExcludeAsync(string worktreePath, string pattern, CancellationToken ct)
    {
        try
        {
            string? gitDir = null;
            // Prefer git's own resolution so worktree sub-checkouts hit the
            // right info/exclude. Falls back to a heuristic on failure.
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --git-dir")
                {
                    WorkingDirectory = worktreePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0)
                    {
                        var raw = (await stdoutTask).Trim();
                        if (!string.IsNullOrEmpty(raw))
                        {
                            gitDir = Path.IsPathRooted(raw)
                                ? raw
                                : Path.GetFullPath(Path.Combine(worktreePath, raw));
                        }
                    }
                }
            }
            catch { /* swallow -- fall back to heuristic */ }

            if (string.IsNullOrEmpty(gitDir))
            {
                // Heuristic fallback: .git is either a dir (main checkout) or
                // a file containing `gitdir: <relative path>` (worktree).
                var dotGitPath = Path.Combine(worktreePath, ".git");
                if (Directory.Exists(dotGitPath))
                {
                    gitDir = dotGitPath;
                }
                else if (File.Exists(dotGitPath))
                {
                    var contents = (await File.ReadAllTextAsync(dotGitPath, ct)).Trim();
                    const string prefix = "gitdir:";
                    if (contents.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var rel = contents.Substring(prefix.Length).Trim();
                        gitDir = Path.IsPathRooted(rel)
                            ? rel
                            : Path.GetFullPath(Path.Combine(worktreePath, rel));
                    }
                }
            }

            if (string.IsNullOrEmpty(gitDir)) return;

            var infoDir = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");
            var existing = File.Exists(excludePath)
                ? await File.ReadAllTextAsync(excludePath, ct)
                : string.Empty;
            // Match either the bare pattern on its own line or the trailing-slash form.
            if (!existing.Split('\n').Any(line => line.Trim().Equals(pattern, StringComparison.Ordinal)))
            {
                var sep = (existing.Length == 0 || existing.EndsWith('\n')) ? string.Empty : "\n";
                await File.AppendAllTextAsync(excludePath, $"{sep}{pattern}\n", ct);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Phase 43 -- builds the prompt section that tells the agent about
    /// available reference documents. Returns empty string when no
    /// attachments exist.
    /// </summary>
    /// <param name="attachmentsJson">
    /// Caller-supplied snapshot of <see cref="StorySpawnRow.AttachmentsJson"/>,
    /// typically loaded fresh via <see cref="LoadFreshAttachmentsJsonAsync"/>
    /// at producer entry so attachments uploaded mid-spawn show up on the
    /// next orchestrator tick.
    /// </param>
    internal static string BuildAttachmentPromptSection(string? attachmentsJson)
    {
        if (string.IsNullOrEmpty(attachmentsJson)) return string.Empty;

        List<AttachmentEntry>? entries;
        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(attachmentsJson, CamelCase); }
        catch { return string.Empty; }
        if (entries is null || entries.Count == 0) return string.Empty;

        var lines = entries.Select(e => $"  - `.forge-attachments/{e.FileName}` ({e.SizeBytes:N0} bytes)");
        return $"""

            ## Story attachments -- IMPORTANT
            The operator uploaded reference documents for this story. Read ALL
            of them FIRST before writing your artifact -- they contain critical
            context, requirements, and design constraints that your output must
            reflect. Do NOT ignore them or treat them as optional.
            Paths (relative to your working directory):
            {string.Join("\n", lines)}

            """;
    }

    /// <summary>
    /// Loads the current <see cref="StorySpawnRow.AttachmentsJson"/> from
    /// the DB without tracking. Producer methods call this on entry so the
    /// attachment snapshot reflects any operator uploads that landed after
    /// the orchestrator's outer tick loaded the spawn -- without mutating
    /// the orchestrator's tracked spawn instance.
    /// </summary>
    internal async Task<string?> LoadFreshAttachmentsJsonAsync(string spawnId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spawnId)) return null;
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            return await db.StorySpawns.AsNoTracking()
                .Where(s => s.Id == spawnId)
                .Select(s => s.AttachmentsJson)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "LoadFreshAttachmentsJsonAsync: failed to read attachments for spawn {SpawnId}; "
                + "falling back to the in-memory snapshot.", spawnId);
            return null;
        }
    }

    /// <summary>Controls whether <see cref="CopyAttachmentsToWorktreeAsync"/>
    /// updates the worktree's local <c>info/exclude</c>.</summary>
    internal enum AttachmentExcludeMode
    {
        /// <summary>Scratch dir -- skip git exclude work entirely.</summary>
        None,

        /// <summary>Real worktree -- write to per-worktree <c>info/exclude</c>.</summary>
        GitInfoExclude,
    }

    private static readonly System.Text.Json.JsonSerializerOptions CamelCase = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record AttachmentEntry(
        string Id,
        string FileName,
        string StoragePath,
        string MimeType,
        long SizeBytes);

    /// <summary>
    /// The operator-facing failure text for a non-pass gen-eval loop; it
    /// becomes the spawn's LastError (the halted banner). A verdict replayed
    /// from history at the iteration cap gets its own wording: no agent ran,
    /// and Retry only re-drives the loop, which replays the same verdict
    /// forever. The fix is the item's Restart (clears iterations), so say so
    /// instead of "bump max-iterations or restart the architect crew".
    /// </summary>
    internal static string FailedLoopMessage(string phaseTag, DeliverableLoopResult result)
    {
        var reason = string.IsNullOrEmpty(result.SloppyReason) ? string.Empty : $" (reason={result.SloppyReason})";
        if (result.ReplayedFromHistory)
        {
            var doc = phaseTag.StartsWith("plan:", StringComparison.Ordinal) ? phaseTag["plan:".Length..] : phaseTag;
            var cap = result.IterationCap is { } c ? $"{result.Iterations}/{c}" : result.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{doc} is already at its iteration cap ({cap}) with verdict={result.FinalVerdict}{reason}, "
                + $"final score {result.FinalScore}, from earlier runs. No agent ran: Retry re-drives the loop, "
                + "which replays this saved verdict. Open the document and use Restart (Conversation tab) to clear "
                + "its iterations and start again at iteration 1.";
        }

        return $"Gen-eval loop for {phaseTag} ended with verdict={result.FinalVerdict}{reason}"
            + $" after {result.Iterations} iterations (final score {result.FinalScore}). "
            + "The plan PR will not be opened -- bump max-iterations or restart the architect crew.";
    }

    /// <summary>
    /// Phase 56 — one stage of the unified per-iter / post-pass gate
    /// chain (build → quality → test) failed. Carries the stage name,
    /// the exact command that ran, and the underlying
    /// <see cref="StoryBuildResult"/> so the generator+evaluator prompt
    /// prepends, the recovery telemetry, and the
    /// <see cref="BuildFailedException"/> on exhaustion can all
    /// reference the same data without repeated parsing.
    /// </summary>
    /// <param name="Stage">One of <c>"build"</c>, <c>"quality"</c>, <c>"test"</c>.</param>
    /// <param name="Command">Exact command that ran (e.g. <c>cargo fmt --check --all</c>).</param>
    /// <param name="Result">Underlying runner result with exit code, error counts, and error summary.</param>
    private sealed record GateFailure(string Stage, string Command, StoryBuildResult Result);
}

/// <summary>
/// 2026-05-20 — transient signal thrown by the plan-doc producer when the
/// gen-eval loop was a no-op because another <c>DeliverableLoop.RunAsync</c>
/// already owns the work item (Phase 74 skipped-concurrent — typically an
/// operator Restart racing the <c>SpawnDependencyScheduler</c> tick). The
/// winning in-flight run produces the artifact; this run produced nothing
/// and its worktree may not hold the file yet. The orchestrator catches it
/// and retries on the next tick instead of recording an "ORCHESTRATOR
/// HALTED" failure.
/// </summary>
public sealed class PlanDocInFlightException : Exception
{
    public PlanDocInFlightException(string repoPath, string workItemId)
        : base($"Plan doc '{repoPath}' (workItem {workItemId}) skipped: another in-flight run owns it; retry next tick.")
    {
        this.RepoPath = repoPath;
        this.WorkItemId = workItemId;
    }

    public string RepoPath { get; }

    public string WorkItemId { get; }
}

/// <summary>
/// 2026-05-27 — transient signal thrown by the impl workstream producer when
/// the gen-eval loop was a no-op because another <c>DeliverableLoop.RunAsync</c>
/// already owns the same work item (Phase 74 skipped-concurrent). The winning
/// in-flight run produces the deliverable; this loser must not continue into
/// stale-file collection, PR creation, or Phase 75 stall handling.
/// </summary>
public sealed class WorkstreamInFlightException : Exception
{
    public WorkstreamInFlightException(string workstreamId)
        : base($"Workstream {workstreamId} skipped: another in-flight run owns it; retry after the active run finishes.")
    {
        this.WorkstreamId = workstreamId;
    }

    public string WorkstreamId { get; }
}
