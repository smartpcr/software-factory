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

                Structure MUST follow this hierarchy EXACTLY:

                # Implementation Plan — {story title}

                ## Phase N: {phase name}

                ### Stage N.M: {stage name}

                #### Implementation Steps
                - [ ] Step description (concrete, actionable — one PR-sized unit of work)
                - [ ] Another step
                - …

                #### Test Scenarios
                - [ ] Scenario: {name} — Given {context}, When {action}, Then {expected outcome}
                - [ ] Scenario: {name} — …

                Hard requirements (the evaluator will refuse pass if any are violated):
                - Each Phase MUST contain Stage subsections (`### Stage N.M: …`); no "naked" steps directly under a phase.
                - Each Stage MUST contain BOTH an `#### Implementation Steps` block AND a `#### Test Scenarios` block.
                - At most 20 Stages per Phase. At most 20 Implementation Steps per Stage. (Spec cap: forge_implementation.md §16.5 Plan-tree validator.)
                - Each step changes ≤ 20 files in the eventual PR.
                - Every Implementation Step is a checkbox `- [ ]` (not bullets, not numbered).
                - Every Test Scenario is a checkbox `- [ ]` (not bullets, not numbered) and is verifiable pass/fail (not subjective).
                - Include at least 2 test scenarios per Stage.
                - Order phases so each builds on the previous; call out hard sequencing dependencies and parallel-safe items inline.
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

        // ── Assign different generator agents per doc (round-robin) ────
        // Load all available architect crew agents so each doc gets a
        // distinct generator. This improves diversity — four agents writing
        // in parallel each bring a different perspective, and the evaluator
        // catches cross-doc inconsistencies (siblings live at their real
        // repo paths in the shared worktree, so each agent's tools see them).
        var generatorAgentIds = await this.LoadArchitectRosterAsync(specs.Length, ct);

        var tasks = specs.Select((spec, idx) =>
            this.ProducePlanFileAsync(
                spawn, story, spec, budget, forceResume,
                generatorAgentOverride: generatorAgentIds.Count > idx ? generatorAgentIds[idx] : null,
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
    /// Loads distinct architect crew agents for round-robin assignment.
    /// Returns up to <paramref name="count"/> agent IDs from the day-shift
    /// architect crew, excluding the evaluator default so gen ≠ eval.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadArchitectRosterAsync(int count, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crew = await db.Crews.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == "crew-arch", ct);
        if (crew is null) return Array.Empty<string>();

        var evalAgentId = crew.DefaultEvaluatorAgentId;

        // All agents in the architect day crew, excluding the evaluator
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.CrewId == "crew-arch")
            .OrderBy(a => a.Sort)
            .Select(a => a.Id)
            .ToListAsync(ct);

        // Exclude the evaluator so we don't accidentally pair gen=eval
        if (!string.IsNullOrEmpty(evalAgentId))
        {
            agents = agents.Where(a => !string.Equals(a, evalAgentId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Put the crew default generator first, then round-robin the rest
        var defaultGen = crew.DefaultGeneratorAgentId;
        if (!string.IsNullOrEmpty(defaultGen))
        {
            agents.Remove(defaultGen);
            agents.Insert(0, defaultGen);
        }

        return agents.Take(count).ToList();
    }

    private async Task<string> ProducePlanFileAsync(
        StorySpawnRow spawn, WorkItemRow story,
        (string FileName, string Brief, string RepoPath) spec,
        TimeSpan budget, bool forceResume,
        string? generatorAgentOverride,
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

        // Resume case — a previous tick's architect already wrote this file
        // and committed it to the branch. Skip re-invoking the LLM unless
        // forceResume is set OR the doc has no deliverables (PENDING — the
        // previous run's loop was cancelled/timed out before the evaluator
        // could score, so the file exists but the doc needs to be scored).
        if (!forceResume && File.Exists(diskPath))
        {
            using var checkScope = this.scopeFactory.CreateScope();
            var checkDb = checkScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var hasDeliverables = await checkDb.Deliverables.AsNoTracking()
                .AnyAsync(d => d.WorkItemId == docWorkItemId, ct);
            if (hasDeliverables)
            {
                var existing = await File.ReadAllTextAsync(diskPath, ct);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    this.logger?.LogInformation(
                        "Plan file '{File}' already in worktree ({Bytes} bytes) with deliverables; skipping architect invocation.",
                        spec.FileName, existing.Length);
                    return existing;
                }
            }
        }

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

            Reply with three short sections in this order:
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

            End with a fenced JSON block:
            ```json
            { "score": <0-100>, "verdict": "<pass|iterate|reject>", "notes": "<one-line gist>" }
            ```
            score >= 90 = pass, 70-89 = iterate (be specific in §2),
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
        var roster = await this.LoadArchitectRosterAsync(allKeys.Count, ct);
        var generatorOverride = roster.Count > docIndex ? roster[docIndex] : null;

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
            // Plan-derived path — deterministic. Each Stage becomes one
            // workstream; deps stay empty by default (the plan's stated
            // sequencing is captured in stage ordering, not as cross-stage
            // dependencies, at v0). The reconciler can only join stages back
            // to rows when the title matches what the plan parser produced,
            // so use the canonical name verbatim.
            return stages.Select(s => new DecompositionWorkstream(
                Title: s.Name,
                Description: s.Steps.Count == 0
                    ? $"Stage {s.PhaseNumber}.{s.StageNumber}"
                    : string.Join(" ", s.Steps.Take(3).Select(st => st.Text)),
                AcceptanceCriteria: (IReadOnlyList<string>)s.TestScenarios.ToList(),
                Deps: (IReadOnlyList<string>)Array.Empty<string>(),
                Deliverables: (IReadOnlyList<string>)Array.Empty<string>(),
                Timeline: string.Empty,
                EstimatedEffort: string.Empty,
                PlanAnchor: s.Anchor,
                PlanContentHash: s.ContentHash)).ToList();
        }

        // Fallback — plan file is missing or unparseable. Keep the original
        // LLM prompt path so the orchestrator still makes forward progress
        // (the reconciler will re-align the rows once the plan lands).
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
            `workstreams.json` inside your working directory. Its content must
            be valid JSON conforming exactly to this schema:

            [
              {
                "title": "short imperative title",
                "description": "1-3 sentences of scope",
                "acceptanceCriteria": ["bullet", "..."],
                "deps": ["title of upstream workstream", "..."],
                "deliverables": ["file or artifact", "..."],
                "timeline": "e.g. day 1-2",
                "estimatedEffort": "e.g. S | M | L or hours"
              }
            ]

            Do NOT write any other files. Do NOT create subdirectories. Reply
            `DONE` after writing the file and stop.
            """;

        await this.InvokeAsync("Architect", spawn.Id, "decompose", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "decompose"), ct);

        var diskPath = Path.Combine(workspace, "workstreams.json");
        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Architect did not write workstreams.json. Workspace: {workspace}");
        }
        var json = await File.ReadAllTextAsync(diskPath, ct);
        try
        {
            var rows = JsonSerializer.Deserialize<List<DecomposeJsonRow>>(json, JsonOpts);
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
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Architect workstreams.json failed to parse: {ex.Message}. Workspace: {workspace}", ex);
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
        var workspace = await this.git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch, ct);

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

            End with a fenced JSON block:
            ```json
            { "score": <0-100>, "notes": "<technical feedback>" }
            ```
            score >= 90 = pass, 70-89 = iterate (be specific in notes),
            < 70 = reject.
            """;

        // Phase 38 (Layer B.3) — read per-workstream points from the real
        // WorkItemRow inserted by DriveDecomposeAsync. Falls back to the
        // story-level points when the row is missing (legacy spawns).
        var wsPoints = await this.LookupWorkItemPointsAsync(
            workstream.Id, fallback: Math.Max(1, spawn.StoryPoints), ct);

        await this.RunGenEvalLoopAsync(
            team: "Engineer",
            workItemId: workstream.Id,
            phaseTag: $"impl:{workstream.Id}",
            generatorPrompt: generatorPrompt,
            evaluatorPrompt: evaluatorPrompt,
            workspace: workspace,
            budget: BudgetForPhase(spawn.StoryPoints, "impl"),
            storyPoints: wsPoints,
            ct: ct);

        // Phase 45 follow-up — collect only files the agent actually changed
        // in the worktree (via `git status --porcelain`), not every file in
        // the repo. Read each one back from disk; return the dict keyed by
        // its REAL repo-relative path. The orchestrator's `CommitFilesAsync`
        // re-materialises the same content (idempotent since files already
        // live in the worktree) and produces a single commit.
        var changedPaths = await this.git.GetChangedFilesAsync(repo, workspace, ct);
        if (changedPaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no file changes for workstream {workstream.Id}. Worktree: {workspace}. " +
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

        await this.InvokeAsync("Review", spawn.Id, $"review:{workstream.Id}:r{round}", prompt, workspace,
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
            You are the Engineer agent fixing review feedback on workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Reviewer comment
            File: `{{comment.FilePath}}` line {{comment.Line}}
            Body: {{comment.Body}}

            ## How to deliver
            1. Write the FIXED file at the SAME relative path as the comment's
               file. Inside your working directory, create whatever
               subdirectories are needed and write the full updated content to
               `{{comment.FilePath}}`.
            2. Write a SECOND file at the working-directory root named
               `reply.md` containing a 1-3 sentence reply for the reviewer.

            Reply `DONE` when both files are written.
            """;

        await this.InvokeAsync("Engineer", spawn.Id, $"review-fix:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review-fix"), ct);

        var fixedFile = Path.Combine(workspace, safePath);
        if (!File.Exists(fixedFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write fixed file '{comment.FilePath}'. Workspace: {workspace}");
        }
        var replyFile = Path.Combine(workspace, "reply.md");
        if (!File.Exists(replyFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write reply.md. Workspace: {workspace}");
        }
        var content = await File.ReadAllTextAsync(fixedFile, ct);
        var reply = (await File.ReadAllTextAsync(replyFile, ct)).Trim();
        return new ReviewFix(comment.FilePath, content, reply);
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
        string workspace;
        try
        {
            workspace = await this.git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "ReadCachedWorkstreamFilesAsync: could not ensure worktree for {Branch}; returning empty.",
                workstream.Branch);
            return dict;
        }

        var changedPaths = await this.git.GetChangedFilesAsync(repo, workspace, ct);
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

        async Task<GeneratorRunOutput> GeneratorDelegate(int iter, string evalNotes, CancellationToken iterCt)
        {
            string iterPrompt;
            if (iter == 1 || string.IsNullOrWhiteSpace(evalNotes))
            {
                iterPrompt = generatorPrompt;
            }
            else
            {
                // Place evaluator feedback BEFORE the original prompt so it
                // gets maximum attention weight. The generator must address
                // every critique before re-reading the original brief.
                iterPrompt = $"""
                    ╔══════════════════════════════════════════════════════════╗
                    ║  EVALUATOR FEEDBACK — YOU MUST ADDRESS EVERY ITEM BELOW ║
                    ╚══════════════════════════════════════════════════════════╝

                    This is iteration {iter}. Your prior attempts scored below the
                    pass threshold. The evaluator's feedback below tells you EXACTLY
                    what to fix. Do NOT re-read the original brief and start from
                    scratch — read the feedback, open the file, and make TARGETED
                    edits that address each critique. If the same critique appears
                    in multiple iterations, your prior fix did not work — try a
                    fundamentally different approach.

                    {evalNotes}

                    ════════════════════════════════════════════════════════════
                    Now here is the original task brief (for reference only —
                    the feedback above takes priority):
                    ════════════════════════════════════════════════════════════

                    {generatorPrompt}
                    """;
            }

            // Phase 41 — snapshot the protected file before invoking the
            // generator so an agent that deletes it mid-turn (observed:
            // architect ran `Remove-Item tech-spec.md` then forgot to write
            // it back) gets restored from the prior iteration's content.
            // Also snapshot on iter 1 so we have a baseline if the generator
            // nukes the seed file.
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
                try { File.Delete(snapshotPath); } catch { /* best-effort */ }
            }

            return new GeneratorRunOutput(output.ToString(), cost, inTok, outTok);
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

    private async Task InvokeAsync(string team, string spawnId, string phaseTag, string prompt, string workspace, CancellationToken ct)
        => await this.InvokeAsync(team, spawnId, phaseTag, prompt, workspace, budget: null, ct);

    private async Task InvokeAsync(string team, string spawnId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct)
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

        await this.taskChannel.EmitAsync(spawnId, "story.agent-prompt", new
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

        var invocation = await this.runner.StartAsync(spawnId, agentId, prompt, context, ct);

        string? failure = null;
        await foreach (var evt in invocation.Events.WithCancellation(ct))
        {
            await this.taskChannel.EmitAsync(spawnId, $"story.agent-{evt.Kind.ToString().ToLowerInvariant()}", new
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
