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
    private readonly ILogger<CrewStoryArtifactStrategy>? logger;

    public CrewStoryArtifactStrategy(
        IServiceScopeFactory scopeFactory,
        IAgentRunner runner,
        TaskChannel taskChannel,
        IConfiguration config,
        IStoryGitOps git,
        ILogger<CrewStoryArtifactStrategy>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.taskChannel = taskChannel;
        this.config = config;
        this.git = git;
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
        var worktreePath = await this.git.EnsureWorktreeAsync(repo, planBranch, "main", ct);

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

        // Resume case — a previous tick's architect already wrote this file
        // and committed it to the branch. Skip re-invoking the LLM unless
        // forceResume is set.
        if (!forceResume && File.Exists(diskPath))
        {
            var existing = await File.ReadAllTextAsync(diskPath, ct);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                this.logger?.LogInformation(
                    "Plan file '{File}' already in worktree ({Bytes} bytes); skipping architect invocation.",
                    spec.FileName, existing.Length);
                return existing;
            }
        }

        var docWorkItemId = $"plan-doc:{spawn.Id}:{spec.FileName}";

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

        await this.RunGenEvalLoopAsync(
            team: "Architect",
            workItemId: docWorkItemId,
            phaseTag: $"plan:{spec.FileName}",
            generatorPrompt: generatorPrompt,
            evaluatorPrompt: evaluatorPrompt,
            workspace: worktreePath,
            budget: budget,
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
        var worktreePath = await this.git.EnsureWorktreeAsync(repo, planBranch, "main", ct);

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
        var workspace = this.PrepareWorkspace(spawn.Id, $"impl-{Slug(workstream.Id)}");
        var repoPrefix = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{workstream.Id}";

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

        var generatorPrompt = $$"""
            You are the Engineer agent implementing workstream `{{workstream.Id}}`
            for story `{{spawn.StoryId}}`.

            ## Story
            {{spawn.StoryTitle}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription)}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}
            Branch: `{{workstream.Branch}}`

            ## Detailed requirements
            {{workItemDescription}}

            ## Your task
            Produce the source-code changes that implement this workstream per
            the detailed requirements above. Use real implementation — types,
            methods, tests — not placeholders. Follow the language and
            conventions specified in the requirements; if none are stated,
            match the repository's primary language.

            ## How to deliver
            Use your filesystem tools to write files inside your working
            directory. Use relative paths only; do not use absolute paths and
            do not write outside the working directory. You may create
            subdirectories within the working directory if your implementation
            spans multiple files. Reply `DONE` when finished.

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

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no files for workstream {workstream.Id}. Workspace: {workspace}");
        }
        // Re-key relative paths under the repo prefix the orchestrator commits.
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
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
        var workspace = this.PrepareWorkspace(spawn.Id, $"impl-{Slug(workstream.Id)}");
        var repoPrefix = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{workstream.Id}";
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(workspace)) return dict;

        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            // Skip siblings/ directory (cross-doc context copies)
            if (file.Contains(Path.DirectorySeparatorChar + "siblings" + Path.DirectorySeparatorChar)) continue;
            var rel = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            var content = await File.ReadAllTextAsync(file, ct);
            if (!string.IsNullOrWhiteSpace(content))
            {
                dict[$"{repoPrefix}/{rel}"] = content;
            }
        }

        this.logger?.LogInformation(
            "Read {Count} cached files from workspace for workstream {WsId}.",
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
        return TimeSpan.FromSeconds(baseSeconds * multiplier);
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
            var iterPrompt = iter == 1
                ? generatorPrompt
                : $"{generatorPrompt}\n\n---\n\nEvaluator feedback from iteration {iter - 1}:\n{evalNotes}";

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

        var result = await loop.RunAsync(new DeliverableLoopRequest(
            WorkItemId: workItemId,
            CrewId: crew.Id,
            GeneratorAgentId: genAgentId,
            EvaluatorAgentId: evalAgentId ?? genAgentId,
            Generator: GeneratorDelegate,
            Evaluator: EvaluatorDelegate,
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            EvaluatorCrewId: evalCrewId,
            WorkItemPoints: storyPoints), ct);

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
}
