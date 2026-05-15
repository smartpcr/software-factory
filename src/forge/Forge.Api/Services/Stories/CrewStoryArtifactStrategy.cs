// -----------------------------------------------------------------------
// <copyright file="CrewStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
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

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IAgentRunner runner;
    private readonly TaskChannel taskChannel;
    private readonly IConfiguration config;
    private readonly IStoryGitOps git;
    private readonly AgentSlotService slots;
    private readonly CopilotSessionPool copilotPool;
    private readonly ILogger<CrewStoryArtifactStrategy>? logger;

    public CrewStoryArtifactStrategy(
        IServiceScopeFactory scopeFactory,
        IAgentRunner runner,
        TaskChannel taskChannel,
        IConfiguration config,
        IStoryGitOps git,
        AgentSlotService slots,
        CopilotSessionPool copilotPool,
        ILogger<CrewStoryArtifactStrategy>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.taskChannel = taskChannel;
        this.config = config;
        this.git = git;
        this.slots = slots;
        this.copilotPool = copilotPool;
        this.logger = logger;
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
                "Components and their responsibilities, data model (entities + key fields), interfaces between components, end-to-end sequence flows for the primary scenarios.",
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
                - [ ] Scenario: {name} -- Given {context}, When {action}, Then {expected outcome}
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
                - Each Stage MUST contain THREE subsections in this exact order: `### Implementation Steps`, `### Dependencies`, `### Test Scenarios`.
                - Dep refs use anchor form, NOT titles. Stage anchor: `phase-{phase-name-slug}/stage-{stage-name-slug}` (no phase or stage numbers -- slugs are derived from the heading name only). Phase anchor: `phase-{phase-name-slug}`.
                - The `### Dependencies` body MUST list one bullet per upstream stage anchor (e.g. `- phase-foo/stage-bar`). An empty body MUST read `- _none -- start stage_`.
                - Phase deps may name a phase anchor (e.g. `- phase-foo`); the orchestrator expands phase deps as "every stage in this phase depends on every stage in the upstream phase".
                - Stage TITLES must be unique across the entire plan; a duplicate title is rejected as `plan.duplicate_stage_title`.
                - At most 20 Stages per Phase. At most 20 Implementation Steps per Stage. Each step changes ≤ 20 files in the eventual PR.
                - Every Implementation Step is a checkbox `- [ ]` (not bullets, not numbered).
                - Every Test Scenario is a checkbox `- [ ]` and is verifiable pass/fail (not subjective).
                - Include at least 2 test scenarios per Stage.
                - Each Implementation Step must be small enough for a single agent iteration to complete.
                """,
                $"{docDir}/implementation-plan.md"),

            ["e2e-scenarios"] = ("e2e-scenarios.md",
                "Gherkin-style feature scenarios (Feature / Scenario / Given-When-Then) covering happy paths and the edge cases QA will run.",
                $"{docDir}/e2e-scenarios.md"),
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
    {
        var docDir = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}";
        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        var specs = BuildPlanDocSpecs(docDir).Values.ToArray();

        // Phase 42 -- set up the real git worktree for plan work. All four
        // architects share this worktree (git refuses two worktrees on one
        // branch), with per-iter commits serialised by the IStoryGitOps lock.
        // The agent's working dir is now the actual repo root, so its tools
        // can read source files / prior docs / etc. -- no more isolated
        // scratch dir + sibling-copy trick.
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
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
        var attachmentPaths = await CopyAttachmentsToWorktreeAsync(attachmentsJson, worktreePath, ct);

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

        var docDir = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}";
        var specs = BuildPlanDocSpecs(docDir);
        if (!specs.TryGetValue(docKey, out var spec))
        {
            throw new InvalidOperationException($"BuildPlanDocSpecs missing entry for '{docKey}'.");
        }

        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        // Phase 47 / 53 -- pick a (generator, evaluator) pair fresh per call.
        // Calling LoadArchitectAndEvaluatorRostersAsync re-queries
        // agent_usage so a long-running upstream doc doesn't re-pin the
        // same agents to the next doc in the chain.
        var (generatorAgentIds, evaluatorAgentIds) =
            await this.LoadArchitectAndEvaluatorRostersAsync(1, ct);

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
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, worktreePath, ct);

        var content = await this.ProducePlanFileAsync(
            spawn, story, spec, budget, forceResume,
            generatorAgentOverride: generatorAgentIds.Count > 0 ? generatorAgentIds[0] : null,
            evaluatorAgentOverride: evaluatorAgentIds.Count > 0 ? evaluatorAgentIds[0] : null,
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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct);
    }

    /// <summary>
    /// Resolve the on-disk state directory (where forge.db lives and
    /// where storyworkspaces / attachments are rooted). Mirrors the
    /// same resolution rule the rest of the codebase uses.
    /// </summary>
    private string ResolveStateDir()
    {
        using var scope = this.scopeFactory.CreateScope();
        var cfg = scope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var dbPath = cfg?["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        return Path.GetDirectoryName(dbPath) ?? @"E:\forge";
    }

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
    private async Task<string> BuildDecisionPinsBlockAsync(string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return string.Empty;

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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
        CancellationToken ct)
    {
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
            StoryBuildResult? build = null;
            try
            {
                build = await StoryBuildRunner.RunBuildAsync(
                    workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: buildCommand);
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
                }, agentId: agentId, crewId: crewId, ct: ct);
            }
        }

        // ----- QUALITY stage(s) -----
        if (qualityCommands is { Count: > 0 })
        {
            for (var i = 0; i < qualityCommands.Count; i++)
            {
                var qc = qualityCommands[i];
                if (string.IsNullOrWhiteSpace(qc)) continue;

                StoryBuildResult? q = null;
                try
                {
                    q = await StoryBuildRunner.RunBuildAsync(
                        workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: qc);
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
            StoryBuildResult? t = null;
            try
            {
                t = await StoryBuildRunner.RunBuildAsync(
                    workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: testCommand);
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
                        workspace,
                    }, agentId: agentId, crewId: crewId, ct: ct);
                    return new GateFailure("test", testCommand, t);
                }

                await this.taskChannel.EmitAsync(workItemId, "task.test-gate.passed", new
                {
                    stage = stagePrefix,
                    gateStage = "test",
                    iter,
                    durationSec = t.Duration.TotalSeconds,
                    warningCount = t.WarningCount,
                    testCommand,
                    workspace,
                }, agentId: agentId, crewId: crewId, ct: ct);
            }
        }

        return null;
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
        CancellationToken ct) =>
        this.RunGateChainAsync(
            workItemId, workspace, iter, "per-iter",
            buildCommand, qualityCommands, testCommand,
            agentId, crewId, ct);

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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await WorkstreamWorktreeSlug.ComputeAsync(workstream, db, ct);
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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crew = await db.Crews.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == "crew-arch", ct);
        if (crew is null)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var defaultGen = crew.DefaultGeneratorAgentId;
        var defaultEval = crew.DefaultEvaluatorAgentId;

        var allAgents = await db.Agents.AsNoTracking()
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
            var passDb = passScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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

            ## Open questions -- structured (REQUIRED when you have any)
            If the story description left ambiguity that you had to guess on,
            do NOT just mention it in prose. Emit a fenced JSON block right
            after `## Iteration Summary` so the operator can answer via the
            wizard and the next iteration gets a definitive answer:

            ```json open-questions
            { "openQuestions": [
                { "id": "<short-stable-slug>", "text": "<question>", "type": "choice", "choices": ["A","B","C"] },
                { "id": "<other-slug>",        "text": "<question>", "type": "open"   }
            ] }
            ```

            Rules:
            - Use `type: "choice"` with explicit `choices` whenever you have
              2-5 reasonable options the operator can pick from.
            - Use `type: "open"` only when the answer is genuinely free-form.
            - Re-emit the same `id` if a previous answer wasn't sufficient.
            - Omit the block entirely when you have no questions.
            - The evaluator WILL hold you below pass while any unanswered
              question exists for this file -- surfacing them is how you
              unblock yourself, not a sign of weakness.

            Reply `DONE` after the summary and stop.
            """;

        var evaluatorPrompt = $$"""
            You are the Architect-reviewer scoring another architect's draft of
            `{{spec.RepoPath}}` for story `{{spawn.StoryId}}`.

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

        await this.RunGenEvalLoopAsync(
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
                try
                {
                    await this.git.CommitPathInWorktreeAsync(
                        repo, planBranch, spec.RepoPath,
                        $"plan({spec.FileName}): iter {iter} ({verdict})", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "Per-iter commit failed for {File} iter {Iter}; loop continues.",
                        spec.FileName, iter);
                }

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
            ct: ct);

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
        var purgeDb = purgeScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
        var docDir = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}";
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
        var allKeys = specs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var docIndex = allKeys.IndexOf(key);
        if (docIndex < 0) docIndex = 0;
        // Phase 47 -- load BOTH rosters (gen + eval) so a single-doc resume
        // doesn't accidentally re-pin the original (potentially still busy)
        // evaluator. The doc keeps its position in both rosters via
        // docIndex so a Restart of doc-3 picks the same gen+eval pair the
        // full run would have assigned to doc-3.
        var (genRoster, evalRoster) = await this.LoadArchitectAndEvaluatorRostersAsync(allKeys.Count, ct);
        var generatorOverride = genRoster.Count > docIndex ? genRoster[docIndex] : null;
        var evaluatorOverride = evalRoster.Count > docIndex ? evalRoster[docIndex] : null;

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
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
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
            ? PlanDocStateHelpers.CanonicalDocWorktreeSlug(key)
            : PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug();
        var worktreePath = await this.git.EnsureWorktreeAsync(
            repo, planBranch, worktreeBase, ct, worktreeSlug: worktreeSlug);

        // Phase 43 -- copy operator-uploaded attachments into the worktree.
        // Refresh from DB so attachments uploaded since the orchestrator
        // loaded `spawn` are reflected in this resume run.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, worktreePath, ct);

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
        var planPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/implementation-plan.md";

        RepoRow? repo = null;
        if (!string.IsNullOrEmpty(spawn.RepoId))
        {
            using var lookup = this.scopeFactory.CreateScope();
            var db = lookup.ServiceProvider.GetRequiredService<ForgeDbContext>();
            repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);
        }

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

            return stages.Select(s =>
            {
                resolved.TryGetValue(s.Anchor, out var rd);
                var deps = rd?.Deps ?? Array.Empty<string>();
                var unresolved = rd?.UnresolvedDeps ?? Array.Empty<string>();
                // Phase 46 -- cache the phase-{slug} prefix so the swarm
                // DAG can group cards without re-parsing the plan.
                var slash = s.Anchor.IndexOf('/');
                var phaseAnchor = slash > 0 ? s.Anchor[..slash] : s.Anchor;

                return new DecompositionWorkstream(
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
                    UnresolvedDeps: unresolved.Count == 0 ? null : unresolved);
            }).ToList();
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
            BudgetForPhase(spawn.StoryPoints, "decompose"), ct);

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
                EstimatedEffort: r.EstimatedEffort ?? string.Empty)).ToList();
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
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.Audit.Add(new AuditRow
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
            await db.SaveChangesAsync(ct);
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
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        // Look up the work item's description -- the operator may have edited
        // it with more specific requirements (e.g. "use C# not TypeScript").
        // Falls back to workstream.Summary when no work item row exists.
        string workItemDescription;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
                { "id": "<short-stable-slug>", "text": "<question>", "type": "choice", "choices": ["A","B","C"] },
                { "id": "<other-slug>",        "text": "<question>", "type": "open"   }
            ] }
            ```

            Use `type: "choice"` with explicit options whenever 2-5 sensible
            answers exist; use `type: "open"` only for free-form. The
            evaluator will hold you below pass while any unanswered question
            exists for this workstream.
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
            - Internal consistency with any operator-uploaded attachments under
              `.forge-attachments/` (read them; flag any divergence between the
              implementation and the operator's reference docs).
            - Obvious bugs, missing error paths, untested edge cases.

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

        await this.RunGenEvalLoopAsync(
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
            ct: ct);

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
        var workspace = this.PrepareWorkspace(spawn.Id, $"review-{Slug(workstream.Id)}-r{round}");

        // Phase 43 (extended) -- surface operator attachments to the reviewer
        // so divergence between the implementation and the operator's
        // reference docs becomes a review comment. Scratch dir → no git
        // exclude needed; ReadAllFiles filters .forge-attachments/ from any
        // output collection paths defensively.
        var attachmentsJson = await this.LoadFreshAttachmentsJsonAsync(spawn.Id, ct);
        await CopyAttachmentsToWorktreeAsync(attachmentsJson, workspace, ct,
            excludeMode: AttachmentExcludeMode.None);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var prompt = $$"""
            You are the Reviewer agent reviewing PR `{{prId}}` for workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

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
            Write a single file `comments.json` inside your working directory
            containing valid JSON of this shape:

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
            BudgetForPhase(spawn.StoryPoints, "review"), ct);

        var diskPath = Path.Combine(workspace, "comments.json");
        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Reviewer did not write comments.json. Workspace: {workspace}");
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
            var stalePrDb = stalePrScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
            excludeMode: AttachmentExcludeMode.None);
        var attachmentPromptSection = BuildAttachmentPromptSection(attachmentsJson);

        var prompt = $$"""
            You are the Engineer agent responding to review feedback on workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Reviewer comment
            File: `{{comment.FilePath}}` line {{comment.Line}}
            Body: {{comment.Body}}
            {{attachmentPromptSection}}
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
            BudgetForPhase(spawn.StoryPoints, "review-fix"), ct, agentIdOverride: fixAgentId);

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
            excludeMode: AttachmentExcludeMode.None);
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
            BudgetForPhase(spawn.StoryPoints, "qa-features"), ct);

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
            excludeMode: AttachmentExcludeMode.None);
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
            BudgetForPhase(spawn.StoryPoints, "qa-steps"), ct);

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
        var stateDir = ResolveStateDir(this.config);

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

    private static string ResolveStateDir(IConfiguration config)
    {
        var dbPath = config["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        return Path.GetDirectoryName(dbPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge");
    }

    /// <summary>Map team name → crew row id pattern (<c>crew-arch</c> etc.).</summary>
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
    /// </summary>
    internal static TimeSpan BudgetForPhase(int points, string phaseTag)
    {
        // Base budget scales with story points: 60s per point, floor 180s.
        var baseSeconds = Math.Max(180.0, points * 60.0);
        var prefix = phaseTag.Split(':', 2)[0];
        var multiplier = prefix switch
        {
            "plan" => 1.0,
            "decompose" => 0.7,
            "impl" => 2.0,
            "review" => 1.0,
            "review-fix" => 1.0,
            "qa-features" => 1.0,
            "qa-steps" => 0.8,
            _ => 1.0,
        };
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
    private async Task RunGenEvalLoopAsync(
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
        CancellationToken ct = default)
    {
        var prefix = CrewIdPrefix(team);
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();

        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
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
            db, crew, useEvaluatorRole: false, workspace, budget, ct,
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
            ? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evalPrefix, ct)
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
                    db, evalCrew, useEvaluatorRole: true, workspace, budget, ct,
                    maxTurns: storyPoints > 0 ? storyPoints * 30 : null);
                evalAgentId = eId;
                evalContext = eCtx;
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
        // Copilot/CLI has no equivalent native continuity surface and
        // falls back to Phase A scratch notes alone. Each side is
        // gated on its OWN crew row (the paired evaluator crew may
        // opt in independently). When false, the delegate skips the
        // load/upsert pair and the runner sees a null ctx.Memory --
        // exactly the pre-Phase-55 shape.
        // Phase 55 / Phase G -- merge crew default with per-agent
        // override. Agent override takes precedence when non-null so
        // an operator can flip a single agent on/off without touching
        // the rest of the crew (e.g. A/B comparing memory-enabled
        // generator against a non-memory sibling).
        var genAgentRow = await db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == genAgentId, ct);
        AgentRow? evalAgentRow = null;
        if (!string.IsNullOrEmpty(evalAgentId))
        {
            evalAgentRow = await db.Agents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == evalAgentId, ct);
        }
        var genCrewWantsMemory = genAgentRow?.AgentMemoryOverride ?? crew.EnableAgentMemory;
        var evalCrewWantsMemory = evalAgentRow?.AgentMemoryOverride
            ?? (evalCrew?.EnableAgentMemory ?? false);

        var genMemoryEnabled = genCrewWantsMemory
            && (genContext.Backend == AgentBackend.Claude
                || (genContext.Backend == AgentBackend.Copilot
                    && genContext.InvokeMode == AgentInvokeMode.Sdk));
        var evalMemoryEnabled = evalCrewWantsMemory
            && evalContext is not null
            && (evalContext.Backend == AgentBackend.Claude
                || (evalContext.Backend == AgentBackend.Copilot
                    && evalContext.InvokeMode == AgentInvokeMode.Sdk));

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
            var decisionPinsBlock = await this.BuildDecisionPinsBlockAsync(workItemId, iterCt);

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
                                "  3. If the error is in a file OUTSIDE your workstream's target list, it's still your responsibility to make the workspace build -- edit it (don't delete it) or raise it as an Open Question if the fix is genuinely out of scope.",
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
            if (genMemoryEnabled)
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    loadedGenHandle = await memStore.GetHandleAsync(
                        workItemId, genAgentId, AgentMemoryRoles.Generator, iterCt);
                    if (loadedGenHandle is not null)
                    {
                        genContextForIter = genContextForIter with { Memory = loadedGenHandle };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.resumed", new
                        {
                            role = "generator",
                            agentId = genAgentId,
                            sessionId = loadedGenHandle.SessionId,
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
            // 2026-05-13 root-cause fix -- capture the dispatcher's
            // Failed-event error so DeliverableLoop can short-circuit
            // before the evaluator runs against a cap-aborted partial
            // output. See DeliverableLoop's FailureReason short-circuit
            // and GeneratorFailureKinds.Classify for the receiving end.
            string? failureReason = null;
            // Phase 54 -- capture per-AssistantUsageEvent samples so the
            // per-invocation row on agent_usage retains the sub-invocation
            // curve for richer UI later. Cleared on each invocation; null
            // is preserved if the runner emits no usage event (CLI path).
            var turnSamples = new List<object>();
            int turnIndex = 0;
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
                }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
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
                    FailureKind: GeneratorFailureKinds.Classify(failureReason));
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
            if (genMemoryEnabled && genContext.InvokeMode == AgentInvokeMode.Sdk)
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
            else if (genMemoryEnabled && !string.IsNullOrEmpty(capturedSessionId))
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    await memStore.UpsertAsync(
                        workItemId, genAgentId, AgentMemoryRoles.Generator,
                        backend: genBackendStr, invokeMode: genInvokeStr,
                        sessionId: capturedSessionId, messagesJson: null,
                        lastIter: iter, approxInputTokens: 0, ct: iterCt);
                    await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                    {
                        role = "generator",
                        agentId = genAgentId,
                        sessionId = capturedSessionId,
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
                try
                {
                    var changed = await this.git.GetChangedFilesAsync(
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

                if (changedCount == 0)
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
                        iterCt);

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

                        if (consecutiveGateFailure >= BuildFailThreshold)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.gate-failed.aborted", new
                            {
                                stage = "per-iter",
                                gateStage = perIterFailure.Stage,
                                iter,
                                consecutiveGateFailure,
                                threshold = BuildFailThreshold,
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
                FailureKind: failureReason is null ? null : GeneratorFailureKinds.Classify(failureReason));
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
                try
                {
                    changedFiles = await this.git.GetChangedFilesAsync(
                        repo: null, worktreePath: workspace, ct: iterCt);
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
                diffHeaderSb.AppendLine($"## Engineer changes this iter — GROUND TRUTH (from `git status --porcelain` + ahead-of-tracking)");
                diffHeaderSb.AppendLine();
                diffHeaderSb.AppendLine($"The engineer changed {changedFiles.Count} file(s) this iter:");
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

            // Phase 56 — prepend the ground-truth diff header to the
            // evaluator's user prompt for THIS iter. The base evaluatorPrompt
            // (workstream brief + rubric + instructions) stays unchanged so
            // the evaluator's task definition is stable across iters; only
            // the iter-specific ground-truth header varies.
            var iterEvaluatorPrompt = diffHeader is null
                ? evaluatorPrompt
                : diffHeader + "\n" + evaluatorPrompt;

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
            if (evalMemoryEnabled)
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    loadedEvalHandle = await memStore.GetHandleAsync(
                        workItemId, evalAgentId, AgentMemoryRoles.Evaluator, iterCt);
                    if (loadedEvalHandle is not null)
                    {
                        evalContextForIter = evalContextForIter with { Memory = loadedEvalHandle };
                        await this.taskChannel.EmitAsync(workItemId, "task.memory.resumed", new
                        {
                            role = "evaluator",
                            agentId = evalAgentId,
                            sessionId = loadedEvalHandle.SessionId,
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
            // Phase 54 -- mirror the generator-side per-AssistantUsageEvent
            // sample capture (see GeneratorDelegate above). Evaluator runs
            // are usually single-turn so the array typically has one entry,
            // but we keep the symmetric shape for the snapshot endpoint.
            var turnSamples = new List<object>();
            int turnIndex = 0;
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
                }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

                if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
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
            if (evalMemoryEnabled && evalContext.InvokeMode == AgentInvokeMode.Sdk)
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
            else if (evalMemoryEnabled && !string.IsNullOrEmpty(capturedSessionId))
            {
                try
                {
                    using var memScope = this.scopeFactory.CreateScope();
                    var memStore = memScope.ServiceProvider.GetRequiredService<AgentMemoryStore>();
                    await memStore.UpsertAsync(
                        workItemId, evalAgentId, AgentMemoryRoles.Evaluator,
                        backend: evalContext.Backend.ToString().ToLowerInvariant(),
                        invokeMode: evalContext.InvokeMode.ToString().ToLowerInvariant(),
                        sessionId: capturedSessionId, messagesJson: null,
                        lastIter: iter, approxInputTokens: 0, ct: iterCt);
                    await this.taskChannel.EmitAsync(workItemId, "task.memory.upserted", new
                    {
                        role = "evaluator",
                        agentId = evalAgentId,
                        sessionId = capturedSessionId,
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

            // Phase 42 -- fire the per-iter post-completion hook AFTER the
            // evaluator has produced its text. The file's on disk; the
            // architect plan path uses this to commit the doc per iter.
            // Best-effort -- exceptions bubble up but the surrounding
            // try/catch wrapper at the call site keeps the loop alive.
            if (onIterationCompleted is not null)
            {
                try { await onIterationCompleted(iter, "post-eval"); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "onIterationCompleted callback threw at iter {Iter} for {WorkItemId}; loop continues.",
                        iter, workItemId);
                }
            }

            return new EvaluatorRunOutput(
                output.ToString(),
                cost,
                inTok,
                outTok,
                TurnSamplesJson: turnSamples.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(turnSamples));
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
                        ct: validCt);

                    if (failure is null)
                    {
                        // All gates green at pass-time: accept the pass.
                        // Also clear lastGateFailure so any subsequent
                        // operations on this loop (none today, but
                        // future hooks) don't see stale state.
                        return null;
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

                    if (postPassRecoveryAttempts >= MaxRecoveryAttempts)
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
                OnPassValidate: onPassValidate), ct);
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
        if (!string.Equals(result.FinalVerdict, "pass", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Gen-eval loop for {phaseTag} ended with verdict={result.FinalVerdict} after {result.Iterations} iterations (final score {result.FinalScore}). The plan PR will not be opened -- bump max-iterations or restart the architect crew.");
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
        ForgeDbContext db, CrewRow crew, bool useEvaluatorRole,
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

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);

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
    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct, string? agentIdOverride = null)
    {
        var prefix = CrewIdPrefix(team);

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
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
            agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentIdOverride, ct);
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
                agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
            }
        }
        else
        {
            agentId = crew.DefaultGeneratorAgentId ?? crew.Lead
                ?? throw new InvalidOperationException(
                    $"Crew '{crew.Id}' has no Lead/DefaultGeneratorAgentId set.");
            agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        }

        // When the override agent belongs to a different crew, inherit
        // model/backend/invokeMode from that crew so runtime config matches
        // the agent's real home (e.g. night-crew agents have different models).
        var configCrew = crew;
        if (agent is not null && !string.IsNullOrEmpty(agent.CrewId)
            && !string.Equals(agent.CrewId, crew.Id, StringComparison.Ordinal))
        {
            var agentCrew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == agent.CrewId, ct);
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
            WallClockBudget: budget);

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
                // Skip docs/stories/... -- that's the plan-doc context, not
                // a code edit target.
                if (path.StartsWith("docs/stories/", StringComparison.OrdinalIgnoreCase)) continue;
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
        var planPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/implementation-plan.md";
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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
        AttachmentExcludeMode excludeMode = AttachmentExcludeMode.GitInfoExclude)
    {
        var paths = new List<string>();
        if (string.IsNullOrEmpty(attachmentsJson)) return paths;

        List<AttachmentEntry>? entries;
        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(attachmentsJson, CamelCase); }
        catch { return paths; }
        if (entries is null || entries.Count == 0) return paths;

        var targetDir = Path.Combine(worktreePath, ".forge-attachments");
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort */ }
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

        var dbDir = Path.GetDirectoryName(
            System.Environment.GetEnvironmentVariable("FORGE_DB_PATH") ?? @"E:\forge\forge.db") ?? @"E:\forge";

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
    /// Phase 55 / Phase A.2 (docs/forge/sloppy_guard.md) -- read the
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
    /// Resolves the worktree-local git exclude file (<c>info/exclude</c>
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
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
