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
/// Real-LLM <see cref="IStoryArtifactStrategy"/> — invokes the same
/// <see cref="IAgentRunner"/> rail that <c>CrewQueueWorker.RunAgentAsync</c>
/// uses for live work-item execution. Per-phase agents (Architect / Engineer
/// / Review / QA) are resolved via the matching <c>crew-{role}</c> row and
/// its <c>Lead</c> agent; their crew row supplies model + backend +
/// invoke-mode; their <c>soul.md</c> is loaded as the system prompt.
///
/// <para>The agent runs against a per-invocation Forge-managed workspace
/// (<c>{stateDir}/storyworkspaces/{spawnIdSlug}/{phaseTag}/</c>) — never
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
    private readonly ILogger<CrewStoryArtifactStrategy>? logger;

    public CrewStoryArtifactStrategy(
        IServiceScopeFactory scopeFactory,
        IAgentRunner runner,
        TaskChannel taskChannel,
        IConfiguration config,
        IStoryGitOps git,
        AgentSlotService slots,
        ILogger<CrewStoryArtifactStrategy>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.taskChannel = taskChannel;
        this.config = config;
        this.git = git;
        this.slots = slots;
        this.logger = logger;
    }

    // ── Per-phase entry points ───────────────────────────────────────────────

    /// <summary>
    /// Single source of truth for the four plan-doc briefs the architect
    /// agent receives in its user prompt. Both <see cref="ProducePlanArtifactsAsync"/>
    /// (full plan run) and <see cref="ResumeSingleArtifactAsync"/> (operator
    /// re-runs one doc) MUST consume this so the brief stays consistent —
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
                This file is a LIVEDOC — it tracks implementation progress with checkboxes.
                Operators tick boxes as work lands; the doc is the source of truth for "what's done".

                Structure MUST follow this hierarchy EXACTLY (Phase 46 layout):

                ---
                title: "{story title}"
                storyId: "{WI-id}"
                ---

                # Phase 1: {phase name}

                ## Dependencies
                - _none — start phase_

                ## Stage 1.1: {stage name}

                ### Implementation Steps
                - [ ] Step description (concrete, actionable — one PR-sized unit of work)
                - [ ] Another step

                ### Dependencies
                - _none — start stage_

                ### Test Scenarios
                - [ ] Scenario: {name} — Given {context}, When {action}, Then {expected outcome}
                - [ ] Scenario: {name} — …

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
                - Each Phase MUST contain a `## Dependencies` section directly under the H1 heading, before any stage. An empty body MUST read `- _none — start phase_`.
                - Each Stage MUST contain THREE subsections in this exact order: `### Implementation Steps`, `### Dependencies`, `### Test Scenarios`.
                - Dep refs use anchor form, NOT titles. Stage anchor: `phase-{phase-name-slug}/stage-{stage-name-slug}` (no phase or stage numbers — slugs are derived from the heading name only). Phase anchor: `phase-{phase-name-slug}`.
                - The `### Dependencies` body MUST list one bullet per upstream stage anchor (e.g. `- phase-foo/stage-bar`). An empty body MUST read `- _none — start stage_`.
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

        // Phase 42 — set up the real git worktree for plan work. All four
        // architects share this worktree (git refuses two worktrees on one
        // branch), with per-iter commits serialised by the IStoryGitOps lock.
        // The agent's working dir is now the actual repo root, so its tools
        // can read source files / prior docs / etc. — no more isolated
        // scratch dir + sibling-copy trick.
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        // Phase 45 — root the worktree at the spawn's chosen base branch
        // (defaults to main). Plan branch was branched off this base, so
        // worktree create here mirrors that ancestry.
        var worktreeBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreePath = await this.git.EnsureWorktreeAsync(repo, planBranch, worktreeBase, ct);

        // Phase 43 — copy operator-uploaded attachments into the worktree
        // so architect agents can read them with their file tools.
        var attachmentPaths = await CopyAttachmentsToWorktreeAsync(spawn, worktreePath, ct);

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
                ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < specs.Length; i++)
        {
            dict[specs[i].RepoPath] = results[i];
        }
        return dict;
    }

    /// <summary>
    /// Phase 42 — resolve the <see cref="RepoRow"/> for a spawn so we can
    /// drive the real-repo worktree. Returns null when the spawn isn't
    /// bound to a repo (legacy / synthetic cases — the strategy then
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
    /// Phase 49 follow-up — resolve the build command the build gates
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
    ///         identified — caller skips the gate with telemetry rather
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
    /// Phase 49 follow-up — resolve the quality-gate commands (run AFTER
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
    /// column yet — if an operator needs to disable a noisy quality gate
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
    /// Phase 49 follow-up — resolve the test command for the post-pass
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
    /// pass a refactor that compiles but silently broke a unit test —
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
    /// Phase 49 follow-up — deserialize <see cref="WorkstreamRow.AgentsJson"/>
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
    /// Phase 49 follow-up — load the same-spawn workstream set and run
    /// <see cref="WorkstreamWorktreeSlug.Compute"/> to produce the short,
    /// semantic worktree directory name
    /// (<c>stage-{phaseNum}.{stageNum}-{anchorSlug}</c>). Returns null
    /// when the workstream has no <see cref="WorkstreamRow.StorySpawnId"/>
    /// — the orchestrator-side hot path is never null in production, but
    /// guard so handcrafted ad-hoc workstreams keep working via the
    /// legacy <c>spawn-{branch-slug}</c> fallback inside
    /// <see cref="RealStoryGitOps.EnsureWorktreeAsync"/>.
    /// </summary>
    private async Task<string?> ComputeWorkstreamWorktreeSlugAsync(
        WorkstreamRow workstream, CancellationToken ct)
    {
        if (workstream is null || string.IsNullOrWhiteSpace(workstream.StorySpawnId))
        {
            return null;
        }

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var siblings = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == workstream.StorySpawnId)
            .ToListAsync(ct);

        // The current row may not be in `siblings` (caller is operating on a
        // possibly-detached entity); ensure it's present so the position
        // computation finds it.
        if (siblings.All(w => w.Id != workstream.Id))
        {
            siblings.Add(workstream);
        }

        return WorkstreamWorktreeSlug.Compute(workstream, siblings);
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
    /// Phase 47 — load TWO distinct rosters (generators + evaluators) so
    /// the parallel plan-doc fan-out (4 docs running concurrently via
    /// <c>Task.WhenAll</c>) can assign a different (generator, evaluator)
    /// pair to each doc. Without this, every doc's evaluator side is
    /// pinned to <c>crew.DefaultEvaluatorAgentId</c> and the parallel
    /// invocations serialise behind a single agent.
    ///
    /// <para>Disjointness — generator and evaluator rosters do NOT
    /// overlap. The default evaluator agent leads the evaluator roster;
    /// remaining evaluator-side seats come from agents not used in the
    /// generator roster. When the crew is too small to fill both
    /// rosters, the evaluator roster falls back to the default
    /// evaluator alone (and parallel docs end up sharing it again).</para>
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

        // Generator roster: default generator first, then everyone else
        // (minus the default evaluator so the two rosters don't overlap
        // by default).
        var generators = allAgents
            .Where(a => string.IsNullOrEmpty(defaultEval)
                || !string.Equals(a, defaultEval, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(defaultGen))
        {
            generators.Remove(defaultGen);
            generators.Insert(0, defaultGen);
        }
        var generatorPicks = generators.Take(count).ToList();
        var generatorSet = new HashSet<string>(generatorPicks, StringComparer.OrdinalIgnoreCase);

        // Evaluator roster: default evaluator first, then any remaining
        // architect agents not already assigned as generators. When the
        // crew is too small to fill both, the evaluator side ends up
        // shorter than count; the caller falls back to the default
        // evaluator for the over-run docs (they share, which is the
        // pre-Phase-47 behaviour and not a regression).
        var evaluators = new List<string>();
        if (!string.IsNullOrEmpty(defaultEval))
        {
            evaluators.Add(defaultEval);
        }
        foreach (var a in allAgents)
        {
            if (generatorSet.Contains(a)) continue;
            if (evaluators.Contains(a, StringComparer.OrdinalIgnoreCase)) continue;
            evaluators.Add(a);
            if (evaluators.Count >= count) break;
        }

        return (generatorPicks, evaluators);
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
        CancellationToken ct)
    {
        // Phase 42 — agent's working dir IS the git worktree root. Doc lives
        // at its real repo path inside the worktree so the agent's tools see
        // both the doc AND the rest of the codebase (siblings, source files,
        // prior docs) at their real locations.
        var diskPath = Path.Combine(worktreePath, spec.RepoPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);

        var docWorkItemId = $"plan-doc:{spawn.Id}:{spec.FileName}";

        // Phase 45 follow-up — pass-verdict guard. If the latest deliverable
        // for this doc already evaluated to `pass`, do NOT spin up another
        // LLM iteration. The previous file content is already in the worktree;
        // returning it short-circuits the loop. forceResume (operator clicked
        // Retry on a stalled doc) bypasses; Restart works because it deletes
        // deliverables first, so the check finds none. Without this guard,
        // any path that re-enters the strategy (Resume click, scheduler tick,
        // base-branch-change replay, boot recovery) re-iterates a doc that
        // was already done — burning tokens and confusing the UI's status.
        if (!forceResume)
        {
            using var passScope = this.scopeFactory.CreateScope();
            var passDb = passScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var latestVerdict = (await passDb.Deliverables.AsNoTracking()
                    .Where(d => d.WorkItemId == docWorkItemId)
                    .ToListAsync(ct))
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

        // Resume case — historical guard from before the Phase 45 pass-verdict
        // check above. The pre-Phase-45 logic skipped the gen-eval loop whenever
        // the file existed AND any deliverable existed, regardless of whether
        // the latest verdict was a terminal `pass`. That misfired on the common
        // resume path: process restart (or in-process loop crash) mid-iteration
        // leaves the file on disk + N `iterate`-verdict deliverables, and the
        // 15s SpawnDependencyScheduler tick re-enters DrivePlanAsync →
        // ProducePlanArtifactsAsync → here. With the old `hasDeliverables`
        // short-circuit the doc was silently abandoned (observed
        // 2026-05-09 on failover-cluster:XRAFT architecture.md iter 7 — the
        // generator started, the process was restarted, and no further iters
        // ever ran because every subsequent tick returned the cached file
        // content). The Phase 45 guard above now correctly handles the
        // already-passed case, so we no longer need a second short-circuit
        // here. Falling through means the gen-eval loop's own resume logic
        // (DeliverableLoop picks up at the next iter via lastPersisted.Iter)
        // takes over and the doc continues toward `pass`.

        // Phase 43 — build attachment prompt section if the story has uploaded reference docs.
        var attachmentPromptSection = BuildAttachmentPromptSection(spawn);

        // Phase 42 — sibling docs live at their real repo paths in the
        // shared worktree (no copy-into-siblings/ trick anymore). Some may
        // not exist yet on iter 1 of parallel execution; the agent reads
        // whichever are present at the time.
        var siblingPathsList = string.Join("\n", siblingSpecs.Select(p => $"  - `{p}`"));

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
            production-quality markdown — not a placeholder, not an outline.
            Anchor every section on the story description's concrete details.

            ### Brief for this file
            {{spec.Brief}}

            ## Repository context — IMPORTANT
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
            Don't duplicate their content — reference shared concepts by
            name. If you spot an inconsistency, note it in your Iteration
            Summary so the next iteration of EITHER doc can align.

            ## How to deliver — CRITICAL
            You MUST invoke your filesystem write tool to save the file at
            `{{spec.RepoPath}}` (relative to working directory — no leading
            slash, no extra path components). Content typed in your chat
            reply is NOT saved — at end of turn the workspace is checked
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
            4. NEVER delete `{{spec.RepoPath}}` — if you need to restructure,
               edit in place. Deleting the file causes the evaluator to
               score 0 and the iteration is wasted.

            End your chat reply with a `## Iteration Summary` section that
            lists (a) the path you wrote to and the byte count, (b) which
            parts of the story description the file covers, (c) any open
            questions for the next iteration. The orchestrator extracts
            this into the per-iteration UI summary.

            ## Open questions — structured (REQUIRED when you have any)
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
              question exists for this file — surfacing them is how you
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

            ## Your job
            Read the draft at `{{spec.RepoPath}}` using your read-only tools
            (view / ls / grep). Do NOT modify the file. Score on:
            - Anchored to the story description AND to the real repo (no
              invented packages / modules / files; references resolve).
            - Production-quality markdown (no placeholders, no outline-only
              sections, no "TODO" markers).
            - Coverage of the file's stated brief.
            - Internal consistency (no contradictions between sections,
              and no contradictions with sibling plan docs that already
              exist on disk).

            Structure your `notes` field (inside the JSON block below) into
            three labeled parts in this order:
            1. **What improved this iteration** — one bullet list.
            2. **What still needs work** — one bullet list (be specific:
               cite section names + sentences).
            3. **Why this score** — two sentences max.

            ## Open-questions hard gate
            If the generator surfaced open questions in its
            `## Open Questions` JSON block AND the operator hasn't answered
            them yet, you MUST return verdict `iterate` regardless of doc
            quality. The orchestrator will block pass anyway via the
            server-side gate, but your verdict needs to match so the
            iteration history reads correctly.

            ## Output

            Output **only** a fenced JSON block — no prose outside it. Put
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

        // Phase 38 (Layer B.3) — read per-doc points from the real
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
            // Phase 41 — revoke shell access. The architect's job is file I/O.
            disallowedTools: ArchitectDisallowedTools,
            // Phase 41 — restore from snapshot if the generator destroys the file.
            protectedFile: diskPath,
            // Phase 42 — commit the doc per iteration so the Worktree tab
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

                // Phase 42 — when the implementation-plan committed, kick the
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
    /// Phase 41 — Claude-Code-SDK tool names the architect MUST NOT use when
    /// producing plan docs. Today: <c>Bash</c> only (the agent has no
    /// legitimate need for a shell when writing a markdown spec, and
    /// permitting it produced an observed delete-then-forget-to-rewrite
    /// failure mode). Reused by all four plan-doc loops; resume calls
    /// flow through <see cref="ProducePlanFileAsync"/> too.
    /// </summary>
    private static readonly string[] ArchitectDisallowedTools = new[] { "Bash" };

    public async Task ResumeSingleArtifactAsync(
        StorySpawnRow spawn, WorkItemRow story, string key, CancellationToken ct = default)
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

        // Assign a stable generator per doc key — uses the roster index
        // so each doc gets a different agent even on resume. The key index
        // is deterministic: architecture=0, tech-spec=1, etc.
        var allKeys = specs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var docIndex = allKeys.IndexOf(key);
        if (docIndex < 0) docIndex = 0;
        // Phase 47 — load BOTH rosters (gen + eval) so a single-doc resume
        // doesn't accidentally re-pin the original (potentially still busy)
        // evaluator. The doc keeps its position in both rosters via
        // docIndex so a Restart of doc-3 picks the same gen+eval pair the
        // full run would have assigned to doc-3.
        var (genRoster, evalRoster) = await this.LoadArchitectAndEvaluatorRostersAsync(allKeys.Count, ct);
        var generatorOverride = genRoster.Count > docIndex ? genRoster[docIndex] : null;
        var evaluatorOverride = evalRoster.Count > docIndex ? evalRoster[docIndex] : null;

        // Phase 42 — same git-worktree plumbing as the full plan run.
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        // Phase 45 — root the worktree at the spawn's chosen base branch
        // (defaults to main). Plan branch was branched off this base, so
        // worktree create here mirrors that ancestry.
        var worktreeBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var worktreePath = await this.git.EnsureWorktreeAsync(repo, planBranch, worktreeBase, ct);

        // Phase 43 — copy operator-uploaded attachments into the worktree.
        await CopyAttachmentsToWorktreeAsync(spawn, worktreePath, ct);

        await this.ProducePlanFileAsync(
            spawn, story, spec, budget, forceResume: true,
            generatorAgentOverride: generatorOverride,
            evaluatorAgentOverride: evaluatorOverride,
            siblingSpecs: siblingPaths,
            worktreePath: worktreePath,
            repo: repo,
            planBranch: planBranch,
            ct: ct);
    }

    public async Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        // Phase 42 — derive the decomposition directly from the merged
        // implementation-plan.md. The plan is the contract; the architect's
        // earlier "make up 2-6 workstreams" prompt produced output that
        // could disagree with the plan. Now we read the plan, parse its
        // Phase/Stage tree, and emit one workstream per Stage. The
        // PlanReconciler uses the same canonicalisation, so initial decomp
        // and any later reconcile pass produce identical workstream sets
        // for the same plan.
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
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
            // Phase 46 — refuse decomposition when the parser surfaced a
            // structural violation (subsection order, duplicate title).
            // The orchestrator's exception handler captures this in the
            // spawn's LastError so the operator sees the concrete code.
            if (canonical.ParseErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Plan refused at decomposition: {string.Join(", ", canonical.ParseErrors)}");
            }

            // Phase 46 — resolve every stage's dep refs (explicit stage
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
                // Phase 46 — cache the phase-{slug} prefix so the swarm
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

        // Fallback — plan file is missing or unparseable. Keep the original
        // LLM prompt path so the orchestrator still makes forward progress
        // (the reconciler will re-align the rows once the plan lands).
        // Phase 46 — artefact is workstreams.yaml; legacy workstreams.json
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

        // Phase 46 — legacy migration. If the architect (or a previous run)
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
    /// Phase 46 — read a legacy <c>workstreams.json</c> and emit the
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
                "Phase 46 — failed to migrate legacy workstreams.json at {Path}; the YAML check that follows will throw if the architect didn't write the YAML.",
                legacyJsonPath);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Phase 45 follow-up — engineer agent works directly in the
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

        // Phase 49 follow-up — compute the short, semantic worktree slug
        // (e.g. "stage-1.1-project-scaffolding-stage-cargo-workspace-and-crate-layout")
        // so the directory under {cloneRoot}/.worktree/ stays under
        // Windows MAX_PATH once cargo/npm/dotnet build artifacts land
        // underneath. The same-spawn workstream list is required to
        // compute the (phase, stage) position; WorkstreamWorktreeSlug
        // falls back to the legacy `spawn-{branch-slug}` when PlanAnchor
        // is missing (handcrafted rows from the create-WS endpoint).
        var worktreeSlug = await this.ComputeWorkstreamWorktreeSlugAsync(workstream, ct);
        var workspace = await this.git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch, ct, worktreeSlug: worktreeSlug);

        // Phase 49 follow-up — bring the workstream branch up to date with
        // the spawn's base branch BEFORE the agent runs. Without this, a
        // workstream whose branch was created before a sibling workstream
        // merged into base inherits a stale snapshot — observed
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
        // the "nudge empty commit" path since no PR exists yet —
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

        // Look up the work item's description — the operator may have edited
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

        // Phase 45 follow-up — bake target file paths into the prompt so the
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
        //      targets.json — DiscoverEngineerTargetsAsync. One extra LLM
        //      call before the engineer iter, but vastly faster than
        //      letting the engineer flail through ls/grep for 6 minutes.
        //
        // Targets are advisory — the engineer can edit other files when the
        // implementation legitimately needs them — but giving it a concrete
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
            ? "_No specific targets identified — locate the relevant files via `grep`/`ls` against the workstream summary above before editing._"
            : string.Join("\n", targets.Select(t => $"- `{t}`"));

        var generatorPrompt = $$"""
            You are the Engineer agent implementing workstream `{{workstream.Id}}`
            for story `{{spawn.StoryId}}`.

            ## Story
            {{spawn.StoryTitle}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription)}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}
            Branch: `{{workstream.Branch}}` (branched from `{{baseBranch}}`)

            ## Detailed requirements
            {{workItemDescription}}

            ## Target files (start here)
            These are the repo-relative paths the workstream's brief and the
            implementation-plan stage explicitly call out. Open them first;
            only branch out to other files when the implementation genuinely
            requires it.

            {{targetsBlock}}

            ## Repository context — IMPORTANT
            Your working directory IS a git worktree of the actual repo, on
            branch `{{workstream.Branch}}`. You can READ AND WRITE ANY FILE in
            this repo using your filesystem tools, AND you have a real shell
            via `bash` / `run_command`. To implement this workstream:
              - **Edit existing source files in place** at the target paths
                listed above. If the workstream is "Stop body buffering in
                HttpMetricsHandler", open `HttpMetricsHandler.cs` and modify
                it directly — do not scaffold a parallel copy.
              - Read the merged plan artifacts under
                `docs/stories/{{StoryPaths.Safe(spawn.StoryId)}}/` for context
                (architecture / tech-spec / implementation-plan / e2e-scenarios).
              - Use `grep -r 'symbol' src/` (via your shell) to locate
                additional files when the targets list isn't sufficient —
                this is far faster than walking the tree with `ls`.
              - Add new files only when the implementation genuinely needs
                a new file (e.g. a new test class, a new helper).
              - Run real builds (`dotnet build {project}.csproj`) and tests
                (`dotnet test {testproj}.csproj`) against the actual project
                files via your shell to verify your changes.

            DO NOT scaffold a sandbox copy under `src/.story-artifacts/` —
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
                as an Open Question at the end of your output and stop —
                do NOT act unilaterally.
              - For *integration-test* / *e2e-test* / *qa-test* / *test*
                workstreams (this one's name contains those words if so),
                your edits should land **primarily under the test project**
                (e.g. `src/CodeIntelligence.IntegrationTests/`,
                `src/*.UnitTests/`, `src/*.IntegrationTests/`). Refactoring
                production code under `src/ast/` or similar to make a test
                pass is out of scope — propose it as a follow-up workstream
                via Open Questions.
              - The build gate applies to ALL workstreams, not just test
                stages: every iter's diff must compile.

            ## You MUST verify your work compiles before claiming DONE
            Before replying `DONE`, run `dotnet build --nologo --verbosity minimal`
            from the worktree root and confirm exit 0. If the build fails,
            FIX it before responding — do not claim DONE on broken code.
            The Forge build gate will reject and block the workstream
            otherwise.

            ## Don't run git yourself
            DO NOT run `git add`, `git commit`, `git push`, `git stash`,
            `git reset`, or any other git command that mutates state.
            Forge stages, commits, and pushes your changes for you after
            the evaluator scores the work. Just leave your edits as
            uncommitted changes in the worktree — that's all Forge needs.
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
            the detailed requirements above. Use real implementation — types,
            methods, tests — not placeholders. Follow the repository's
            existing conventions, naming, and project layout.

            ## How to deliver
            Edit and create files in place inside your working directory
            (which is the git worktree root). Forge picks up every changed
            file via `git status` after you finish, so you don't need to
            "copy results to a workspace" — there is no separate workspace.
            Reply `DONE` when finished.

            ## Open questions — structured (REQUIRED when you have any)
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
            **{{workstream.Name}}** — {{workstream.Summary}}

            ## Detailed requirements
            {{workItemDescription}}

            ## Story
            {{spawn.StoryTitle}}
            {{NonEmpty(spawn.StoryDescription)}}

            ## Your job
            Read every file the engineer wrote inside the working directory
            (use your read-only tools — view, ls, grep). Do NOT modify
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

            ## Open-questions hard gate
            If the engineer surfaced open questions in their
            `## Open Questions` JSON block AND the operator hasn't answered
            them yet, you MUST score below pass and return verdict `iterate`.
            Server-side gate enforces this regardless; your verdict needs to
            match so the iteration history reads correctly.

            ## Output

            Output **only** a fenced JSON block — no prose outside it. Put
            your full technical feedback inside the `notes` field. Example
            shape:

            ```json
            {
              "score": 87,
              "dims": { "correctness": 35, "quality": 22, "tests": 13, "scope": 9, "risk": 8 },
              "verdict": "iterate",
              "notes": "Improvements this iteration: ...  Still needs improvement: ...  Why this score: ..."
            }
            ```

            Dimension caps (each dim must stay within these bounds — values
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

        // Phase 38 (Layer B.3) — read per-workstream points from the real
        // WorkItemRow inserted by DriveDecomposeAsync. Falls back to the
        // story-level points when the row is missing (legacy spawns). Use
        // the same wsPoints for BOTH iter cap and wall-clock budget so a
        // points bump on the workstream's WI propagates to the agent's hard
        // timeout, not just the iter cap.
        var wsPoints = await this.LookupWorkItemPointsAsync(
            workstream.Id, fallback: Math.Max(1, spawn.StoryPoints), ct);

        // Phase 49 follow-up — read the agent pair the orchestrator picked
        // at claim time (StorySpawnOrchestrator.ResolveImplPairAsync ran
        // PairDiversityResolver against the live busy set). The legacy
        // call to RunGenEvalLoopAsync without overrides made
        // workstream.AgentsJson a UI-only display field — the actual
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
            // Phase 49 — pass the resolved build command so the per-iter
            // gate runs the right tool for the repo's ecosystem (cargo /
            // npm / go / mvn / dotnet) instead of always invoking
            // `dotnet build`. Null = no detectable build system, gate
            // is skipped per-iter (mirrors the post-pass skip).
            buildCommand: ResolveBuildCommand(repo, workspace),
            ct: ct);

        // Phase 45 follow-up — collect only files the agent actually changed
        // in the worktree (via `git status --porcelain`), not every file in
        // the repo. Read each one back from disk; return the dict keyed by
        // its REAL repo-relative path. The orchestrator's `CommitFilesAsync`
        // re-materialises the same content (idempotent since files already
        // live in the worktree) and produces a single commit.
        // Phase 49 — diff against the merge target (spawn.BaseBranch)
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
                $"The agent finished without editing any files in place — check the transcript for tool-call activity.");
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

        // Phase 47e — post-pass build gate. The gen↔eval loop returned a
        // diff the evaluator scored; that score does NOT prove the diff
        // compiles. Run the repo's build command on the worktree and
        // refuse to commit if it fails. The text-only evaluator was
        // observed (2026-05-09) scoring 68 on a diff that deleted
        // production EvolutionEngine.cs along with 17 other files; the
        // compiler is the only honest source of truth for "does this
        // code parse". BuildFailedException is caught by
        // DriveImplementAsync's spawn task and routed to Status="stalled"
        // (Phase 49 split — was "blocked" before) so the operator can
        // triage.
        //
        // Phase 49 — resolve the build command per repo (dotnet/cargo/
        // npm/go/mvn) instead of hard-coding `dotnet build`. A Rust
        // workspace (e.g. failover-cluster:XRAFT) was hitting a 200 ms
        // exit-1 here from `dotnet build` failing to find a .sln,
        // routing every parallel stage to blocked despite passing
        // evaluator scores. When the build system is unknown or
        // ambiguous, skip the gate and emit telemetry so the operator
        // can configure repo.BuildCommand explicitly.
        var resolvedBuildCommand = ResolveBuildCommand(repo, workspace);
        if (string.IsNullOrWhiteSpace(resolvedBuildCommand))
        {
            var detected = BuildSystemDetector.Detect(workspace);
            await this.taskChannel.EmitAsync(workstream.Id, "task.build-gate.skipped", new
            {
                stage = "post-pass",
                reason = detected.Ambiguous ? "ambiguous-build-system" : "no-build-system-detected",
                ecosystem = detected.Ecosystem,
                ambiguous = detected.Ambiguous,
                ambiguityDetail = detected.AmbiguityDetail,
                workspace,
            }, ct: ct);
            this.logger?.LogInformation(
                "Post-pass build gate skipped for {WorkItem}: ecosystem={Ecosystem} ambiguous={Ambiguous}. "
                + "Set repo.BuildCommand explicitly to enable the gate.",
                workstream.Id, detected.Ecosystem, detected.Ambiguous);
        }
        else
        {
            var postPassBuild = await StoryBuildRunner.RunBuildAsync(
                workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: resolvedBuildCommand);
            if (!postPassBuild.Success)
            {
                await this.taskChannel.EmitAsync(workstream.Id, "task.build-gate.failed", new
                {
                    stage = "post-pass",
                    exitCode = postPassBuild.ExitCode,
                    errorCount = postPassBuild.ErrorCount,
                    warningCount = postPassBuild.WarningCount,
                    durationSec = postPassBuild.Duration.TotalSeconds,
                    timedOut = postPassBuild.TimedOut,
                    errorSummary = postPassBuild.ErrorSummary,
                    buildCommand = resolvedBuildCommand,
                    workspace,
                }, ct: ct);

                throw new BuildFailedException(
                    $"Workstream {workstream.Id} post-pass build gate failed (exit {postPassBuild.ExitCode}, "
                    + $"{postPassBuild.ErrorCount} error(s)) running `{resolvedBuildCommand}`. The gen/eval "
                    + $"loop returned a diff the evaluator scored at pass, but the build shows the diff "
                    + $"doesn't compile. Refusing to commit broken code.\n\n{postPassBuild.ErrorSummary}")
                {
                    ExitCode = postPassBuild.ExitCode,
                    ErrorCount = postPassBuild.ErrorCount,
                    WarningCount = postPassBuild.WarningCount,
                    ErrorSummary = postPassBuild.ErrorSummary,
                    TrippedByConsecutiveIters = false,
                    ConsecutiveBrokenIters = 0,
                    LastIteration = 0,
                };
            }

            // Phase 49 follow-up — emit the "passed" event symmetrically
            // with the "failed" / "skipped" paths so the Build tab in
            // the Conversation pane can render a green tick. Previously
            // only failure / skipped fired, leaving the operator with
            // no visible "build is healthy" signal between iterations.
            await this.taskChannel.EmitAsync(workstream.Id, "task.build-gate.passed", new
            {
                stage = "post-pass",
                durationSec = postPassBuild.Duration.TotalSeconds,
                warningCount = postPassBuild.WarningCount,
                buildCommand = resolvedBuildCommand,
                workspace,
            }, ct: ct);

            // Phase 49 follow-up — quality gates after build success.
            // Mirrors the upstream CI shape (`ci.yml` Check job for
            // Cargo workspaces runs fmt + clippy in addition to build).
            // Each quality command runs sequentially; first non-zero
            // exit becomes a BuildFailedException with
            // stage="post-pass-quality" telemetry naming the failed
            // command, so the operator (and engineer agent on the next
            // restart) can distinguish a real compile failure from a
            // formatting / lint failure. The exception is caught by
            // StorySpawnOrchestrator.DriveImplementAsync and routes the
            // workstream to Status="stalled" with StallReason set.
            // Quality failures are NOT auto-fixed today — the engineer
            // agent must be Restarted; a future enhancement could route
            // them into a dedicated quality-fix loop on the same PR.
            var qualityCommands = ResolveQualityCommands(repo, workspace);
            for (var i = 0; i < qualityCommands.Count; i++)
            {
                var qc = qualityCommands[i];
                if (string.IsNullOrWhiteSpace(qc)) continue;

                var qResult = await StoryBuildRunner.RunBuildAsync(
                    workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: qc);
                if (qResult.Success)
                {
                    // Phase 49 follow-up — emit per-quality-command passed
                    // event so the Build tab can show each gate tick
                    // (fmt ✓, clippy ✓) independently. Without this the
                    // operator only sees the BUILD passed tick + the
                    // final TEST passed tick, with the quality gates
                    // silently in between.
                    await this.taskChannel.EmitAsync(workstream.Id, "task.build-gate.passed", new
                    {
                        stage = "post-pass-quality",
                        qualityCommand = qc,
                        qualityCommandIndex = i,
                        durationSec = qResult.Duration.TotalSeconds,
                        warningCount = qResult.WarningCount,
                        buildCommand = resolvedBuildCommand,
                        workspace,
                    }, ct: ct);
                    continue;
                }

                await this.taskChannel.EmitAsync(workstream.Id, "task.build-gate.failed", new
                {
                    stage = "post-pass-quality",
                    qualityCommand = qc,
                    qualityCommandIndex = i,
                    exitCode = qResult.ExitCode,
                    errorCount = qResult.ErrorCount,
                    warningCount = qResult.WarningCount,
                    durationSec = qResult.Duration.TotalSeconds,
                    timedOut = qResult.TimedOut,
                    errorSummary = qResult.ErrorSummary,
                    buildCommand = resolvedBuildCommand,
                    workspace,
                }, ct: ct);

                throw new BuildFailedException(
                    $"Workstream {workstream.Id} post-pass quality gate failed (exit {qResult.ExitCode}) "
                    + $"running `{qc}`. The build itself passed (`{resolvedBuildCommand}` ok), but the "
                    + $"upstream-CI-mirroring quality gate rejected the diff. This is what blocks merge "
                    + $"on the remote (e.g. cargo fmt --check or cargo clippy -D warnings is a required "
                    + $"status check). Refusing to commit code that the remote will reject.\n\n{qResult.ErrorSummary}")
                {
                    ExitCode = qResult.ExitCode,
                    ErrorCount = qResult.ErrorCount,
                    WarningCount = qResult.WarningCount,
                    ErrorSummary = qResult.ErrorSummary,
                    TrippedByConsecutiveIters = false,
                    ConsecutiveBrokenIters = 0,
                    LastIteration = 0,
                };
            }

            // Phase 49 follow-up — post-pass TEST gate. Final gate
            // before Forge opens the PR: build compiles, quality gates
            // (fmt / clippy) are clean, NOW run the test suite to
            // ensure the diff hasn't silently regressed an existing
            // test. Same UX as build / quality gate failures —
            // BuildFailedException(stage="post-pass-test") routes the
            // workstream to Status="stalled" via
            // DriveImplementAsync's catch; no PR is opened.
            // <para>Resolution: operator override on
            // <see cref="RepoRow.TestCommand"/> wins; else
            // <see cref="BuildSystemDetector"/> picks the ecosystem
            // default (cargo test, dotnet test {sln}, npm test,
            // go test ./..., mvn test, pytest). Null → no detectable
            // test command → skip the gate rather than fail closed.</para>
            var resolvedTestCommand = ResolveTestCommand(repo, workspace);
            if (string.IsNullOrWhiteSpace(resolvedTestCommand))
            {
                await this.taskChannel.EmitAsync(workstream.Id, "task.test-gate.skipped", new
                {
                    stage = "post-pass",
                    reason = "no test command resolved (set repo.TestCommand to enable)",
                    workspace,
                }, ct: ct);
            }
            else
            {
                var testResult = await StoryBuildRunner.RunBuildAsync(
                    workspace, ct, StoryBuildRunner.DefaultTimeout, buildCommand: resolvedTestCommand);
                if (!testResult.Success)
                {
                    await this.taskChannel.EmitAsync(workstream.Id, "task.test-gate.failed", new
                    {
                        stage = "post-pass",
                        exitCode = testResult.ExitCode,
                        errorCount = testResult.ErrorCount,
                        warningCount = testResult.WarningCount,
                        durationSec = testResult.Duration.TotalSeconds,
                        timedOut = testResult.TimedOut,
                        errorSummary = testResult.ErrorSummary,
                        testCommand = resolvedTestCommand,
                        workspace,
                    }, ct: ct);

                    throw new BuildFailedException(
                        $"Workstream {workstream.Id} post-pass test gate failed (exit {testResult.ExitCode}, "
                        + $"{testResult.ErrorCount} error(s)) running `{resolvedTestCommand}`. The build + "
                        + $"quality gates passed, but the test suite has regressions. Refusing to commit code "
                        + $"that breaks tests.\n\n{testResult.ErrorSummary}")
                    {
                        ExitCode = testResult.ExitCode,
                        ErrorCount = testResult.ErrorCount,
                        WarningCount = testResult.WarningCount,
                        ErrorSummary = testResult.ErrorSummary,
                        TrippedByConsecutiveIters = false,
                        ConsecutiveBrokenIters = 0,
                        LastIteration = 0,
                    };
                }

                await this.taskChannel.EmitAsync(workstream.Id, "task.test-gate.passed", new
                {
                    stage = "post-pass",
                    durationSec = testResult.Duration.TotalSeconds,
                    warningCount = testResult.WarningCount,
                    testCommand = resolvedTestCommand,
                    workspace,
                }, ct: ct);
            }
        }

        return dict;
    }

    public async Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"review-{Slug(workstream.Id)}-r{round}");
        var prompt = $$"""
            You are the Reviewer agent reviewing PR `{{prId}}` for workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Story
            {{spawn.StoryTitle}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}

            ## Round
            {{round}} (0 = first pass; if there are no real issues, return an
            empty array `[]` to approve.)

            ## Your task
            Produce review comments. Each comment must point at a real
            file/line and describe a concrete issue worth fixing — not nits.

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

        // Phase 49 follow-up — emit transcript events under workstream.Id
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
        var workspace = this.PrepareWorkspace(spawn.Id, $"fix-{Slug(workstream.Id)}-{Guid.NewGuid():N}");
        var safePath = comment.FilePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var prompt = $$"""
            You are the Engineer agent responding to review feedback on workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Reviewer comment
            File: `{{comment.FilePath}}` line {{comment.Line}}
            Body: {{comment.Body}}

            ## Decide first
            Read the comment carefully. EVERY comment needs an explicit response —
            either a code change OR a written reply explaining why no change is
            needed. Silently resolving without responding is not acceptable.

            Choose ONE of two paths:

            ### Path A — Code change
            Use this when the reviewer's feedback is correct and the file needs
            an update.

              1. Write the FIXED file at the SAME relative path as the comment's
                 file. Inside your working directory, create whatever
                 subdirectories are needed and write the FULL updated content to
                 `{{comment.FilePath}}`.
              2. Write a SECOND file at the working-directory root named
                 `reply.md` containing a 1-3 sentence reply for the reviewer
                 summarising what you changed and why.

            ### Path B — Reply only (no code change)
            Use this when the comment doesn't require a code change — e.g. the
            reviewer misread the diff, the behaviour is intentional, the concern
            is addressed elsewhere, or it's out of scope for this PR.

              1. **Do NOT write** the fixed file at `{{comment.FilePath}}`.
              2. Write `reply.md` at the working-directory root containing a 1-3
                 sentence reply explaining WHY no change is needed. Be specific
                 — point at the existing code / test / doc that already addresses
                 the concern, or explain the design choice.

            `reply.md` is **mandatory** in BOTH paths.

            Reply `DONE` when finished.
            """;

        // Phase 49 follow-up — emit fix events under workstream.Id so the
        // engineer's iteration on review feedback appears in the same
        // Conversation tab as the original impl work.
        await this.InvokeAsync("Engineer", workstream.Id, $"review-fix:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review-fix"), ct);

        // reply.md is mandatory — every comment needs a written response.
        var replyFile = Path.Combine(workspace, "reply.md");
        if (!File.Exists(replyFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write reply.md. Workspace: {workspace}. " +
                $"reply.md is mandatory for both code-change and reply-only paths.");
        }
        var reply = (await File.ReadAllTextAsync(replyFile, ct)).Trim();

        // Path A (code change) vs Path B (reply only) — determined by
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
        var wsList = string.Join("\n", workstreams.Select(w => $"- `{w.Id}`: {w.Name} — {w.Summary}"));
        var repoPrefix = $"tests/Features/{StoryPaths.Safe(spawn.StoryId)}";
        var prompt = $$"""
            You are the QA agent for story `{{spawn.StoryId}}`.

            ## Story
            {{story.Title}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Workstreams
            {{wsList}}

            ## Your task
            Produce one Gherkin `.feature` file per workstream that describes
            the user-visible behavior the workstream must deliver. Real
            scenarios — Given/When/Then — that step definitions can be written
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
        var prompt = $$"""
            You are the Engineer agent writing step definitions for the QA
            feature file of workstream `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}

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
    /// (sibling of the SQLite DB). Idempotent — does NOT wipe an existing
    /// workspace so the calling strategy can resume from cached agent output
    /// when a downstream step (commit/push/PR-create) failed and the
    /// orchestrator re-ticks. Each individual producer method is responsible
    /// for deciding whether existing files in the workspace are reusable.
    /// </summary>
    private string PrepareWorkspace(string spawnId, string phaseTag)
    {
        var stateDir = ResolveStateDir(this.config);

        // Check both slug conventions — older workspaces may use dashes
        // (Slug) while newer code uses underscores (SlugForFs). Prefer the
        // existing directory to avoid duplicating workspaces.
        var slugUnderscore = SlugForFs(spawnId);
        var slugDash = Slug(spawnId);
        var pathUnderscore = Path.Combine(stateDir, "storyworkspaces", slugUnderscore, phaseTag);
        var pathDash = Path.Combine(stateDir, "storyworkspaces", slugDash, phaseTag);

        if (Directory.Exists(pathDash)) return pathDash;
        if (Directory.Exists(pathUnderscore)) return pathUnderscore;

        // Neither exists — create with the dash convention (matches existing data)
        Directory.CreateDirectory(pathDash);
        return pathDash;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Phase 45 follow-up — same fix as ProduceWorkstreamChangesAsync.
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

        // EnsureWorktreeAsync is idempotent — if the worktree already
        // exists from the earlier producer pass, returns its path
        // unchanged. If it doesn't (e.g. operator wiped .worktree/ between
        // ticks), this re-creates it from origin/{branch} which means
        // changes the agent left uncommitted are gone — handled below.
        // Phase 49 — pass the same short slug as the producer path so we
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

        // Phase 49 — diff against spawn.BaseBranch so cached files
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
    /// Formula: <c>base = max(2 minutes, points * 30s)</c>; per-phase
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
        // Plan docs need a higher floor — the architect must read repo
        // context + siblings + write a large markdown file. 3 minutes
        // (the 0-point default) is too tight and causes timeouts.
        if (prefix == "plan" && seconds < 600)
            seconds = 600;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Runs a generator↔evaluator iter-and-score loop for one story phase.
    /// Each iteration the generator writes/updates files in
    /// <paramref name="workspace"/>; the evaluator inspects them with
    /// read-only tools and emits a JSON score. Loop continues until the
    /// evaluator passes or hits <c>ScoringConstants.MaxIterations</c>. All
    /// transcript events are emitted on <paramref name="workItemId"/>'s
    /// channel so the workstream UI card lights up "live" — single-shot
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
        // Phase 47 — evaluator agent override. When set, the crew's
        // DefaultEvaluatorAgentId is temporarily replaced so
        // ResolveAgentRunConfigAsync (useEvaluatorRole=true) picks up
        // the override. Allows the parallel plan-doc fan-out to assign
        // a different evaluator per doc instead of all 4 docs sharing
        // the crew's default evaluator.
        string? evaluatorAgentOverride = null,
        // Phase 41 — Claude-Code tool names to revoke for this invocation
        // (forwarded to the CLI as `--disallowedTools`). Architect plan-doc
        // generators pass `["Bash"]` so an agent can't `Remove-Item` its
        // own artifact mid-turn.
        IReadOnlyList<string>? disallowedTools = null,
        // Phase 41 — single artifact to snapshot before each generator turn
        // and restore from snapshot if the agent loses it. Catches the
        // "delete-then-forget-to-rewrite" failure mode regardless of which
        // tool was used to delete (defense in depth alongside disallowedTools).
        string? protectedFile = null,
        // Phase 42 — invoked after each iteration's deliverable row is
        // persisted. Used by the architect plan path to commit the doc per
        // iteration so the Worktree tab shows real progress. Args are
        // (iter, verdict). Best-effort — exceptions log but don't break
        // the loop. Null = no per-iter side effects (engineer crews don't
        // need this; their CommitFilesAsync runs once at the end).
        Func<int, string, Task>? onIterationCompleted = null,
        // Phase 49 — resolved build command for the per-iter build gate
        // (impl phase only). When null, the gate is skipped: either the
        // repo's ecosystem isn't recognised by BuildSystemDetector or
        // the caller is a non-impl phase that doesn't run a build at all
        // (architect plan-doc, decompose, qa, review-fix). Passed through
        // to StoryBuildRunner.RunBuildAsync; null/empty there falls back
        // to `dotnet build`, which is the wrong default for non-.NET
        // ecosystems — we explicitly skip instead.
        string? buildCommand = null,
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

        // Phase 47 — same trick for the evaluator: temporarily replace
        // the crew's DefaultEvaluatorAgentId so the evaluator-side
        // ResolveAgentRunConfigAsync (called below with
        // useEvaluatorRole=true) picks up the per-doc override. The
        // crew is AsNoTracking so this mutation is safe — it only
        // affects the in-memory copy used by this call.
        if (!string.IsNullOrEmpty(evaluatorAgentOverride))
        {
            crew.DefaultEvaluatorAgentId = evaluatorAgentOverride;
        }

        var (genAgentId, genContext, genBackendStr, genInvokeStr) = await this.ResolveAgentRunConfigAsync(
            db, crew, useEvaluatorRole: false, workspace, budget, ct);

        // Phase 41 — apply per-loop tool restrictions (Bash revoked for
        // architect plan-doc loops). Records are immutable; layer the field
        // on with `with`.
        if (disallowedTools is { Count: > 0 })
        {
            genContext = genContext with { DisallowedTools = disallowedTools };
        }

        // Evaluator is best-effort — when crew has no evaluator wiring, the
        // loop's null delegate path returns an auto-pass score so we still
        // exit after one generator iteration.
        string? evalAgentId = null;
        AgentContext? evalContext = null;
        string? evalCrewId = null;
        var evalPrefix = crew.PairedWithCrewId;
        CrewRow? evalCrew = !string.IsNullOrEmpty(evalPrefix)
            ? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evalPrefix, ct)
            : crew;
        if (evalCrew is not null)
        {
            try
            {
                var (eId, eCtx, _, _) = await this.ResolveAgentRunConfigAsync(
                    db, evalCrew, useEvaluatorRole: true, workspace, budget, ct);
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
        // and surface the misconfiguration on the spawn — silent same-model
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
                $"Set crew.{nameof(CrewRow.EvaluatorModel)} on the evaluator crew (or agent.{nameof(AgentRow.EvaluatorModel)} override) to a different model — typically gpt-5.5/sonnet against an opus generator.");
        }

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

        // Phase 47 — per-iter no-progress detection. Track consecutive
        // iterations whose post-generator worktree has zero uncommitted
        // changes (impl phase only — plan/decompose write to scratch dirs
        // that aren't git worktrees, so `git status` would always show 0
        // there and false-trigger the guard). After NoProgressThreshold
        // such iterations in a row, throw NoProgressException so the
        // orchestrator can mark the workstream `blocked` instead of
        // burning the full maxIter budget. Set up here, mutated inside
        // GeneratorDelegate's closure.
        var consecutiveNoChange = 0;
        // Phase 47e — separate counter for "agent edited files but they
        // don't compile". Tripped after three consecutive broken-build
        // iters; routes to BuildFailedException → blocked. Distinct from
        // no-progress so the operator can tell which failure mode hit.
        var consecutiveBrokenBuild = 0;
        // Phase 49 follow-up — when the per-iter build gate fails we
        // capture the result so the NEXT iteration's generator prompt
        // can quote the actual compiler / test output. Without this the
        // generator only sees the evaluator's text-only feedback and
        // has no way to know its code doesn't compile (the build runs
        // AFTER the generator returns its diff, so the agent itself
        // never observes the failure mid-turn unless it volunteered a
        // build inside the tool stream). Cleared back to null on the
        // first successful build so a stale failure doesn't haunt the
        // prompt forever once the agent fixes it.
        StoryBuildResult? lastBuildFailure = null;
        string? lastBuildFailureCommand = null;
        const int NoProgressThreshold = 3;
        const int BuildFailThreshold = 3;
        var isImplPhase = phaseTag.StartsWith("impl:", StringComparison.Ordinal);

        async Task<GeneratorRunOutput> GeneratorDelegate(int iter, string evalNotes, CancellationToken iterCt)
        {
            // Phase 49 follow-up — assemble the iter prompt from up to
            // THREE layered sections so the generator sees the most
            // actionable feedback first:
            //   1. Build / test output from the prior iter (if any) —
            //      compiler-truth, highest signal. Forge already
            //      decided this iter's diff doesn't build; the agent
            //      MUST fix that before doing anything else.
            //   2. Evaluator feedback (if any) — text critique on
            //      what's wrong with the design / approach. Useful
            //      once the code at least compiles.
            //   3. The original brief — for reference. Same exact
            //      content as iter 1; never gets "lost" but takes
            //      bottom priority once feedback exists.
            // iter 1 has neither section (1) nor (2), so it sees only
            // the brief — the unchanged historic shape.
            var hasBuildFeedback = lastBuildFailure is not null;
            var hasEvalFeedback = !string.IsNullOrWhiteSpace(evalNotes);

            string iterPrompt;
            if (iter == 1 || (!hasBuildFeedback && !hasEvalFeedback))
            {
                iterPrompt = generatorPrompt;
            }
            else
            {
                var sb = new StringBuilder();
                if (hasBuildFeedback)
                {
                    var bf = lastBuildFailure!;
                    sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
                    sb.AppendLine("║  BUILD FAILURE FROM PRIOR ITERATION — FIX THIS FIRST    ║");
                    sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
                    sb.AppendLine();
                    sb.AppendLine($"Your iteration {iter - 1} diff DID NOT COMPILE.");
                    sb.AppendLine($"  command  : {lastBuildFailureCommand ?? "<resolved at runtime>"}");
                    sb.AppendLine($"  exit code: {bf.ExitCode}");
                    sb.AppendLine($"  errors   : {bf.ErrorCount}");
                    sb.AppendLine($"  warnings : {bf.WarningCount}");
                    sb.AppendLine($"  duration : {bf.Duration.TotalSeconds:F2}s{(bf.TimedOut ? " (TIMED OUT)" : "")}");
                    sb.AppendLine();
                    sb.AppendLine("BUILD ERROR OUTPUT (verbatim from the compiler — fix EVERY error):");
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
                    sb.AppendLine($"Three consecutive broken-build iterations will trip the build gate and stall the workstream. You are currently {consecutiveBrokenBuild} of {BuildFailThreshold}.");
                    sb.AppendLine();
                    sb.AppendLine("How to fix:");
                    sb.AppendLine("  1. Read the error output above. The compiler tells you the exact file, line, and reason.");
                    sb.AppendLine("  2. Open the file at the line indicated and make the targeted fix.");
                    sb.AppendLine("  3. If the error is in a file OUTSIDE your workstream's target list, it's still your responsibility to make the workspace build — edit it (don't delete it) or raise it as an Open Question if the fix is genuinely out of scope.");
                    sb.AppendLine($"  4. Run `{lastBuildFailureCommand ?? "the build command"}` yourself before responding to confirm exit 0.");
                    sb.AppendLine();
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                if (hasEvalFeedback)
                {
                    sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
                    sb.AppendLine("║  EVALUATOR FEEDBACK — YOU MUST ADDRESS EVERY ITEM BELOW ║");
                    sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
                    sb.AppendLine();
                    sb.AppendLine($"This is iteration {iter}. Your prior attempts scored below the");
                    sb.AppendLine("pass threshold. The evaluator's feedback below tells you EXACTLY");
                    sb.AppendLine("what to fix. Do NOT re-read the original brief and start from");
                    sb.AppendLine("scratch — read the feedback, open the file, and make TARGETED");
                    sb.AppendLine("edits that address each critique. If the same critique appears");
                    sb.AppendLine("in multiple iterations, your prior fix did not work — try a");
                    sb.AppendLine("fundamentally different approach.");
                    sb.AppendLine();
                    sb.AppendLine(evalNotes);
                    sb.AppendLine();
                    sb.AppendLine("────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                }
                sb.AppendLine("Now here is the original task brief (for reference only — the");
                sb.AppendLine("feedback above takes priority):");
                sb.AppendLine("────────────────────────────────────────────────────────────");
                sb.AppendLine();
                sb.Append(generatorPrompt);
                iterPrompt = sb.ToString();
            }

            // Phase 41 — snapshot the protected file before invoking the
            // generator so an agent that deletes it mid-turn (observed:
            // architect ran `Remove-Item tech-spec.md` then forgot to write
            // it back) gets restored from the prior iteration's content.
            // Also snapshot on iter 1 so we have a baseline if the generator
            // nukes the seed file.
            //
            // Phase 49 follow-up — the snapshot file MUST be deleted at the
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
            // task.prompt — same shape CrewQueueWorker emits, so the live-exec /
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

            var invocation = await this.runner.StartAsync(workItemId, genAgentId, iterPrompt, genContext, iterCt);
            var output = new StringBuilder();
            decimal cost = 0m;
            long inTok = 0, outTok = 0;
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
            }

            // Phase 41 — restore the protected file from snapshot if the
            // generator destroyed it (deleted OR emptied/truncated). A file
            // that shrinks to <10% of the snapshot size is treated as
            // truncated — the generator's "rewrite completely" pattern often
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

            // Phase 47 — no-progress fail-fast. After the generator finishes
            // editing the worktree this iteration, count uncommitted file
            // changes via `git status --porcelain`. If the count is zero for
            // NoProgressThreshold consecutive impl iterations, abort the
            // gen↔eval loop with a NoProgressException — the orchestrator
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
                    // git status failed — treat as inconclusive (don't trip
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

                    // Phase 47e — agent edited files this iter. Validate
                    // the build BEFORE the evaluator scores the diff. The
                    // evaluator (text-only) was observed scoring 68 on a
                    // diff that deleted EvolutionEngine.cs along with 17
                    // other production files — the compiler is the only
                    // honest check on "does this diff make sense as code".
                    //
                    // Phase 49 — when buildCommand is null the repo has
                    // no detectable build system (or the operator hasn't
                    // configured repo.BuildCommand yet). Skip the gate
                    // rather than running `dotnet build` against an
                    // unknown workspace and false-failing in 200 ms.
                    //
                    // Phase 49 follow-up² — emit telemetry on the
                    // skipped + passed paths too, not just failure.
                    // Pre-fix the per-iter gate was silent on success
                    // and silent when buildCommand resolved to null;
                    // operators looking at the Build tab for a
                    // long-running workstream saw zero events even
                    // though the gate had run 20+ times and passed
                    // every iter. Now every iter that touched files
                    // emits exactly one task.build-gate.{passed,
                    // failed,skipped} per the gate's outcome so the
                    // Build tab shows a continuous green/red/gray
                    // timeline. The events flag stage="per-iter" so
                    // the UI can distinguish per-iter ticks from the
                    // single post-pass tick at the end of the loop.
                    StoryBuildResult? build = null;
                    if (string.IsNullOrWhiteSpace(buildCommand))
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.build-gate.skipped", new
                        {
                            stage = "per-iter",
                            iter,
                            reason = "no build command resolved (set repo.BuildCommand to enable)",
                            workspace,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    else
                    {
                        try
                        {
                            build = await StoryBuildRunner.RunBuildAsync(
                                workspace, iterCt, StoryBuildRunner.DefaultTimeout,
                                buildCommand: buildCommand);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            this.logger?.LogWarning(ex,
                                "Per-iter build check threw for {Workspace}; skipping this iter's build gate.",
                                workspace);
                            build = null;
                            await this.taskChannel.EmitAsync(workItemId, "task.build-gate.skipped", new
                            {
                                stage = "per-iter",
                                iter,
                                reason = $"build runner threw: {ex.GetType().Name}: {ex.Message}",
                                buildCommand,
                                workspace,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                        }
                    }

                    if (build is not null && !build.Success)
                    {
                        consecutiveBrokenBuild++;
                        // Phase 49 follow-up — capture this build's
                        // output so iter N+1's GeneratorDelegate can
                        // prepend it to the prompt. Cleared back to
                        // null on the FIRST successful build below
                        // (and on the success branch of changedCount
                        // == 0 elsewhere) so the agent only sees
                        // CURRENT build state, not a stale failure
                        // from a fix it already shipped.
                        lastBuildFailure = build;
                        lastBuildFailureCommand = buildCommand;
                        await this.taskChannel.EmitAsync(workItemId, "task.build-failed.detected", new
                        {
                            stage = "per-iter",
                            iter,
                            consecutiveBrokenBuild,
                            threshold = BuildFailThreshold,
                            exitCode = build.ExitCode,
                            errorCount = build.ErrorCount,
                            warningCount = build.WarningCount,
                            durationSec = build.Duration.TotalSeconds,
                            timedOut = build.TimedOut,
                            errorSummary = build.ErrorSummary,
                            buildCommand,
                            workspace,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                        if (consecutiveBrokenBuild >= BuildFailThreshold)
                        {
                            await this.taskChannel.EmitAsync(workItemId, "task.build-failed.aborted", new
                            {
                                stage = "per-iter",
                                iter,
                                consecutiveBrokenBuild,
                                threshold = BuildFailThreshold,
                            }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);

                            throw new BuildFailedException(
                                $"Engineer's diff failed `{buildCommand}` in {consecutiveBrokenBuild} consecutive "
                                + $"iterations (through iter {iter}, exit {build.ExitCode}, "
                                + $"{build.ErrorCount} error(s)). Aborting gen/eval loop; operator must Restart "
                                + $"to retry.\n\n{build.ErrorSummary}")
                            {
                                ExitCode = build.ExitCode,
                                ErrorCount = build.ErrorCount,
                                WarningCount = build.WarningCount,
                                ErrorSummary = build.ErrorSummary,
                                TrippedByConsecutiveIters = true,
                                ConsecutiveBrokenIters = consecutiveBrokenBuild,
                                LastIteration = iter,
                            };
                        }
                    }
                    else if (build is not null)
                    {
                        consecutiveBrokenBuild = 0;
                        // Phase 49 follow-up — the agent fixed the
                        // build (or it was never broken). Clear the
                        // feedback so iter N+1 doesn't see stale
                        // compiler output.
                        lastBuildFailure = null;
                        lastBuildFailureCommand = null;
                        // Phase 49 follow-up² — emit a per-iter passed
                        // tick so the Build tab shows a green dot for
                        // every successful per-iter gate run. Without
                        // this the Build tab is empty for any
                        // long-running workstream that builds cleanly
                        // each iter — the post-pass passed event only
                        // fires once at the end of a passing iter, and
                        // a workstream that never reaches `pass`
                        // (verdict=iterate every iter) shows zero
                        // ticks despite every iter's diff actually
                        // compiling. Observed 2026-05-11 on
                        // failover-cluster:XRAFT stage 3.1: 23
                        // iterations, every diff compiled cleanly via
                        // the agent's own `cargo build` invocations,
                        // but the Build tab was completely empty.
                        await this.taskChannel.EmitAsync(workItemId, "task.build-gate.passed", new
                        {
                            stage = "per-iter",
                            iter,
                            durationSec = build.Duration.TotalSeconds,
                            warningCount = build.WarningCount,
                            buildCommand,
                            workspace,
                        }, agentId: genAgentId, crewId: crew.Id, ct: iterCt);
                    }
                    // build == null → check threw; leave counter as-is.
                    // (The skipped event above already records the throw
                    // so the Build tab still has a row for this iter.)
                }
                // changedCount == -1 → inconclusive, leave counter as-is.
            }

            return new GeneratorRunOutput(output.ToString(), cost, inTok, outTok);
            }
            finally
            {
                // Phase 49 — ALWAYS delete the snapshot file. Runs after
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
                // No evaluator configured — auto-pass so the loop exits after
                // one generator iteration.
                return new EvaluatorRunOutput(
                    "{\"score\":95,\"verdict\":\"pass\",\"notes\":\"No evaluator configured; auto-pass.\"}",
                    0m);
            }

            await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
            {
                agentId = evalAgentId,
                crewId = evalCrewId,
                systemPrompt = string.IsNullOrEmpty(evalContext.SoulMd) ? (string?)null : evalContext.SoulMd,
                userPrompt = evaluatorPrompt,
                model = evalContext.Model,
                backend = evalContext.Backend.ToString().ToLowerInvariant(),
                invokeMode = evalContext.InvokeMode.ToString().ToLowerInvariant(),
                role = "evaluator",
                iter,
            }, agentId: evalAgentId, crewId: evalCrewId, ct: iterCt);

            var invocation = await this.runner.StartAsync(workItemId, evalAgentId, evaluatorPrompt, evalContext, iterCt);
            var output = new StringBuilder();
            decimal cost = 0m;
            long inTok = 0, outTok = 0;
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
            }

            // Phase 42 — fire the per-iter post-completion hook AFTER the
            // evaluator has produced its text. The file's on disk; the
            // architect plan path uses this to commit the doc per iter.
            // Best-effort — exceptions bubble up but the surrounding
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

            return new EvaluatorRunOutput(output.ToString(), cost, inTok, outTok);
        }

        // Phase 44 — claim agents before starting the gen/eval loop.
        await this.slots.TryClaimAsync(genAgentId, workItemId, "generator", ct);
        if (evalAgentId is not null)
        {
            await this.slots.TryClaimAsync(evalAgentId, workItemId, "evaluator", ct);
        }

        DeliverableLoopResult result;
        try
        {
            result = await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: workItemId,
                CrewId: crew.Id,
                GeneratorAgentId: genAgentId,
                EvaluatorAgentId: evalAgentId ?? genAgentId,
                Generator: GeneratorDelegate,
                Evaluator: EvaluatorDelegate,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                EvaluatorCrewId: evalCrewId,
                WorkItemPoints: storyPoints), ct);
        }
        finally
        {
            // Phase 44 — always release agents, even on error/cancel.
            await this.slots.ReleaseAsync(genAgentId, workItemId, ct: CancellationToken.None);
            if (evalAgentId is not null)
            {
                await this.slots.ReleaseAsync(evalAgentId, workItemId, ct: CancellationToken.None);
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
                $"Gen-eval loop for {phaseTag} ended with verdict={result.FinalVerdict} after {result.Iterations} iterations (final score {result.FinalScore}). The plan PR will not be opened — bump max-iterations or restart the architect crew.");
        }
    }

    /// <summary>
    /// Resolves the agent + invocation context for one role on a crew. When
    /// <paramref name="useEvaluatorRole"/>, prefers <c>DefaultEvaluatorAgentId</c>
    /// + <c>EvaluatorModel</c>; otherwise <c>DefaultGeneratorAgentId</c> /
    /// <c>Lead</c> and the crew's primary model. Throws when the role isn't
    /// configured so callers can fall back (e.g. the gen-eval loop's null
    /// evaluator path).
    /// </summary>
    private async Task<(string AgentId, AgentContext Context, string BackendStr, string InvokeStr)> ResolveAgentRunConfigAsync(
        ForgeDbContext db, CrewRow crew, bool useEvaluatorRole,
        string workspace, TimeSpan? budget, CancellationToken ct)
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
            WallClockBudget: budget);

        return (agentId, context, backendStr, invokeStr);
    }

    private static string? NonEmptyOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, CancellationToken ct)
        => await this.InvokeAsync(team, workItemId, phaseTag, prompt, workspace, budget: null, ct);

    /// <summary>
    /// Phase 49 follow-up — <paramref name="workItemId"/> (formerly named
    /// <c>spawnId</c>) is the transcript stream the emitted
    /// <c>story.agent-*</c> events land on. Callers running spawn-scoped
    /// work (decompose, qa-features) pass <c>spawn.Id</c>; callers
    /// running workstream-scoped work (review-comments, review-fix,
    /// qa-steps, discover-targets) pass <c>workstream.Id</c> so those
    /// events appear in the workstream's Conversation tab (and the new
    /// Review subtab) instead of disappearing into the spawn-level
    /// stream that no UI surface renders.
    /// </summary>
    private async Task InvokeAsync(string team, string workItemId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct)
    {
        var prefix = CrewIdPrefix(team);

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
            ?? throw new InvalidOperationException(
                $"No crew found for team '{team}' (looked for id '{prefix}' or prefix). Configure crews in DB.");

        var agentId = crew.DefaultGeneratorAgentId ?? crew.Lead
            ?? throw new InvalidOperationException(
                $"Crew '{crew.Id}' has no Lead/DefaultGeneratorAgentId set.");

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var model = !string.IsNullOrEmpty(agent?.GeneratorModel) ? agent!.GeneratorModel
            : !string.IsNullOrEmpty(crew.Model) ? crew.Model
            : throw new InvalidOperationException($"Crew '{crew.Id}' has no Model set.");

        var backendStr = !string.IsNullOrEmpty(agent?.Backend) ? agent!.Backend : crew.Backend;
        var backend = string.Equals(backendStr, "claude", StringComparison.OrdinalIgnoreCase)
            ? AgentBackend.Claude : AgentBackend.Copilot;

        var invokeStr = !string.IsNullOrEmpty(agent?.InvokeMode) ? agent!.InvokeMode : crew.InvokeMode;
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
                // Skip docs/stories/... — that's the plan-doc context, not
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
    /// can't be located — the caller falls back to text-only extraction
    /// and then to the discovery agent.
    /// </summary>
    private async Task<string> ReadPlanStageBodyAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workstream.PlanAnchor)) return string.Empty;
        var repo = await this.LookupRepoAsync(spawn.RepoId, ct);
        var planBranch = spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
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
        // file. Best-effort — if the anchor format differs, we just return
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
    /// Mirrors the <c>ProduceWorkstreamsAsync</c> sandbox pattern — agent
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
            **{{workstream.Name}}** — {{workstream.Summary}}

            ## Detailed requirements
            {{workItemDescription}}

            ## Implementation-plan stage (verbatim)
            {{NonEmpty(planStageBody, "_(plan stage not available)_")}}

            ## Your task
            Return the repo-relative file paths the engineer should open
            first to deliver this workstream. Prefer paths the brief or
            plan stage names directly. Do NOT browse the repo, do NOT open
            files — operate purely from the prose above. Pick at most 8
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
    private static IReadOnlyDictionary<string, string> ReadAllFiles(string root)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return dict;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
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
    /// Phase 38 (Layer B.3) — fetch the WorkItem.Points value the gen↔eval
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
    /// Phase 43 — copies operator-uploaded attachments from the canonical
    /// storage dir (<c>E:\forge\attachments\{uploadId}\</c>) into the git
    /// worktree at <c>.forge-attachments/</c> so architect agents can read
    /// them with their file tools. The target dir is rebuilt from scratch on
    /// each call so replaced/deleted files stay current.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CopyAttachmentsToWorktreeAsync(
        StorySpawnRow spawn, string worktreePath, CancellationToken ct)
    {
        var paths = new List<string>();
        if (string.IsNullOrEmpty(spawn.AttachmentsJson)) return paths;

        List<AttachmentEntry>? entries;
        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(spawn.AttachmentsJson, CamelCase); }
        catch { return paths; }
        if (entries is null || entries.Count == 0) return paths;

        var targetDir = Path.Combine(worktreePath, ".forge-attachments");
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort */ }
        }
        Directory.CreateDirectory(targetDir);

        // Also ensure .gitignore contains .forge-attachments/
        var gitignorePath = Path.Combine(worktreePath, ".gitignore");
        var gitignoreEntry = ".forge-attachments/";
        try
        {
            var existing = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath, ct) : string.Empty;
            if (!existing.Contains(gitignoreEntry))
            {
                await File.AppendAllTextAsync(gitignorePath, $"\n{gitignoreEntry}\n", ct);
            }
        }
        catch { /* best-effort */ }

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
            catch { /* best-effort — agent can still work without this file */ }
        }

        return paths;
    }

    /// <summary>
    /// Phase 43 — builds the prompt section that tells the architect agent
    /// about available reference documents. Returns empty string when no
    /// attachments exist.
    /// </summary>
    private static string BuildAttachmentPromptSection(StorySpawnRow spawn)
    {
        if (string.IsNullOrEmpty(spawn.AttachmentsJson)) return string.Empty;

        List<AttachmentEntry>? entries;
        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<AttachmentEntry>>(spawn.AttachmentsJson, CamelCase); }
        catch { return string.Empty; }
        if (entries is null || entries.Count == 0) return string.Empty;

        var lines = entries.Select(e => $"  - `.forge-attachments/{e.FileName}` ({e.SizeBytes:N0} bytes)");
        return $"""

            ## Story attachments — IMPORTANT
            The operator uploaded reference documents for this story. Read ALL
            of them FIRST before writing your artifact — they contain critical
            context, requirements, and design constraints that your output must
            reflect. Do NOT ignore them or treat them as optional.
            Paths (relative to your working directory):
            {string.Join("\n", lines)}

            """;
    }

    private static readonly System.Text.Json.JsonSerializerOptions CamelCase = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record AttachmentEntry(
        string Id,
        string FileName,
        string StoragePath,
        string MimeType,
        long SizeBytes);
}
