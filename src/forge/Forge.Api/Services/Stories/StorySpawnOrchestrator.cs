// -----------------------------------------------------------------------
// <copyright file="StorySpawnOrchestrator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 28 — orchestration brain. The endpoints
/// in <c>StoriesEndpoints</c> mutate spawn state; this class watches for
/// state transitions and drives the corresponding crew work:
///
/// <list type="number">
/// <item><c>plan</c> entered → architect crew writes 4 docs, opens plan PR.</item>
/// <item>Plan PR merged + caller advances to <c>decompose</c> → architect produces decomposition (which seeds workstream rows).</item>
/// <item><c>implement</c> entered → engineer crew picks up each ready (no-deps-or-deps-merged) workstream, writes its slice, opens a PR.</item>
/// <item>Workstream PR opened → review crew posts comments and routes them back to the engineer; engineer pushes a fix; review approves and merges.</item>
/// <item>Plan PR merged → QA crew (parallel) writes feature files, opens QA PR.</item>
/// <item>QA PR merged → engineer crew writes step definitions per workstream, opens step-def PRs (one per workstream).</item>
/// <item>All workstream PRs merged + QA + step-def PRs merged + e2e green → spawn flips to <c>done</c>; ADO work item transitions to <c>Closed</c>.</item>
/// </list>
///
/// Each method is idempotent: re-running on the same row is a no-op when
/// the side effect has already happened. This is what makes the orchestrator
/// safe to call from the dependency scheduler tick AND from the endpoint
/// after a phase advance.
/// </summary>
public sealed class StorySpawnOrchestrator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider services;
    private readonly IStoryArtifactStrategy strategy;
    private readonly IStoryGitOps git;
    private readonly IStoryProviderOps provider;
    private readonly AgentAllocationLoader allocation;
    private readonly ILogger<StorySpawnOrchestrator>? logger;

    public StorySpawnOrchestrator(
        IServiceProvider services,
        IStoryArtifactStrategy strategy,
        IStoryGitOps git,
        IStoryProviderOps provider,
        AgentAllocationLoader allocation,
        ILogger<StorySpawnOrchestrator>? logger = null)
    {
        this.services = services;
        this.strategy = strategy;
        this.git = git;
        this.provider = provider;
        this.allocation = allocation;
        this.logger = logger;
    }

    /// <summary>
    /// Drive the spawn forward as far as the gate logic allows. Idempotent.
    /// Called: after a phase advance from the API, after a PR merges (via
    /// <c>SpawnDependencyScheduler</c>), and on server boot for resumption.
    /// </summary>
    public Task DriveAsync(string storyId, CancellationToken ct = default)
        => this.DriveAsync(storyId, forceResume: false, ct);

    /// <summary>
    /// Drive variant that propagates a one-shot <paramref name="forceResume"/>
    /// signal to the architect strategy. Used by the spawn-retry endpoint
    /// after a non-pass verdict so the workspace-cache short-circuit in
    /// <see cref="CrewStoryArtifactStrategy.ProducePlanFileAsync"/> is
    /// bypassed and the gen↔eval loop re-iterates instead of replaying the
    /// stalled artifact unchanged.
    /// </summary>
    public async Task DriveAsync(string storyId, bool forceResume, CancellationToken ct = default)
    {
        // Run a single step per call. Each phase method (DrivePlanAsync,
        // DriveDecomposeAsync, ...) is internally idempotent and exits
        // after its first applicable sub-step (open PR → merge → advance
        // phase, etc.). Subsequent sub-steps land on the next call:
        //   - SpawnDependencyScheduler ticks every 15 s in production,
        //   - PATCH /spawn/phase advances the operator-driven gates,
        //   - POST /spawn/decompose/approve unblocks `implement`,
        //   - tests use TickAsync to advance the lifecycle deterministically.
        // Removing the previous in-line tight loop also lets phase-level
        // assertions in the test suite observe each intermediate state
        // (e.g. spawn at "plan" with PR opened but not yet merged).
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return;
        await this.RunOneStepAsync(db, hub, spawn, forceResume, ct);
    }

    /// <summary>
    /// Single advance attempt. Returns true when a side effect was applied
    /// (so the caller can loop). Reads the spawn fresh inside its own scope.
    /// </summary>
    private async Task<bool> RunOneStepAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub, StorySpawnRow spawn, bool forceResume, CancellationToken ct)
    {
        // The spawn may originate from either an upstream WorkItem (ADO Feature/Epic)
        // or a locally-authored ProductStory. When no WorkItem matches the spawn id,
        // **persist** a fresh WorkItemRow so plan-doc / workstream child rows
        // (Layer B inserts use ParentId=spawn.StoryId) have a real parent to
        // resolve to. Without persistence the parent FK dangles and queries
        // like "all work items for this story" miss the children.
        var story = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == spawn.StoryId, ct);
        if (story is null)
        {
            story = new WorkItemRow
            {
                Id = spawn.StoryId,
                Title = spawn.StoryTitle,
                DescriptionMd = spawn.StoryDescription ?? string.Empty,
                RepoId = spawn.RepoId,
                Provider = "local",
                Track = "dev",
                Type = "feature",
                State = "active",
                Points = spawn.StoryPoints,
                CreatedAt = spawn.CreatedAt,
                UpdatedAt = spawn.UpdatedAt,
            };
            db.WorkItems.Add(story);
            await db.SaveChangesAsync(ct);
        }

        var repo = spawn.RepoId is null
            ? null
            : await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);

        // Phase-specific drivers. Review/QA also re-run the implement pass
        // so blocked-then-unblocked downstream workstreams get picked up
        // when their dependency PR merges (28.5.7 / 28.11.6).
        // Top-level catch ensures any unhandled phase failure is surfaced
        // as LastError rather than crashing the scheduler tick.
        var advanced = false;
        try
        {
            switch (spawn.Phase)
            {
                case StorySpawnPhases.Plan:
                    advanced |= await this.DrivePlanAsync(db, hub, spawn, story, repo, forceResume, ct);
                    break;
                case StorySpawnPhases.Decompose:
                    advanced |= await this.DriveDecomposeAsync(db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Implement:
                    advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Review:
                    advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Qa:
                    advanced |= await this.DriveImplementAsync(db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveReviewAsync(db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveQaAsync(db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Done:
                    advanced |= await this.DriveDoneAsync(db, hub, spawn, story, repo, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogError(ex,
                "Orchestrator phase '{Phase}' failed for spawn {SpawnId} ({StoryId}).",
                spawn.Phase, spawn.Id, spawn.StoryId);
            await PersistLastErrorAsync(db, spawn.Id,
                $"{spawn.Phase} failed: {ex.Message}", hub, ct);
        }
        return advanced;
    }

    /// <summary>
    /// Phase 45 — return the spawn's chosen base branch (defaults to
    /// <c>"main"</c>). Every PR target + every branch-creation base in this
    /// orchestrator routes through this helper so the operator-chosen
    /// branch flows everywhere consistently.
    /// </summary>
    private static string BaseBranchOf(StorySpawnRow spawn) =>
        string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;

    /// <summary>
    /// Phase 45 — make sure the spawn's base branch exists, branched from
    /// <c>main</c>. No-op when the base is <c>main</c> itself or when the
    /// branch is already present in the repo. Called at the very start of
    /// every drive so a freshly-spawned story creates the operator's branch
    /// before the plan branch is forked off it.
    /// </summary>
    private async Task EnsureSpawnBaseBranchAsync(
        StorySpawnRow spawn, RepoRow? repo, CancellationToken ct)
    {
        var b = BaseBranchOf(spawn);
        if (string.Equals(b, "main", StringComparison.Ordinal)) return;
        await this.git.EnsureBranchAsync(repo, b, "main", ct);
    }

    // ── plan ─────────────────────────────────────────────────────────────────
    private async Task<bool> DrivePlanAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, bool forceResume, CancellationToken ct)
    {
        var baseBranch = BaseBranchOf(spawn);
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);

        // Step 1 — PR doesn't exist yet: produce artifacts, commit, open PR.
        // Also re-enters when forceResume=true and PR exists: re-generate
        // stalled/iterate docs, commit new content, push to existing PR.
        // Also re-enters when some plan docs have no deliverables (PENDING)
        // — they need their gen-eval loops run even if the PR was already
        // opened for a sibling doc that passed earlier.
        var hasPendingDocs = false;
        if (!string.IsNullOrEmpty(spawn.PlanPrId) && !forceResume)
        {
            var planDocIds = PlanDocFileNames.Select(f => $"plan-doc:{spawn.Id}:{f}").ToList();
            var docIdsWithDeliverables = await db.Deliverables.AsNoTracking()
                .Where(d => planDocIds.Contains(d.WorkItemId))
                .Select(d => d.WorkItemId)
                .Distinct()
                .ToListAsync(ct);
            hasPendingDocs = docIdsWithDeliverables.Count < planDocIds.Count;
        }
        if (string.IsNullOrEmpty(spawn.PlanPrId) || forceResume || hasPendingDocs)
        {
            var planBranch = spawn.PlanBranch ?? $"story/{GitSafe(spawn.StoryId)}/plan";
            try
            {
                await this.git.EnsureBranchAsync(repo, planBranch, baseBranch, ct);

                await EnsurePlanDocWorkItemsAsync(db, spawn, story, ct);

                var artifacts = await this.strategy.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);
                await this.git.CommitFilesAsync(repo, planBranch, artifacts,
                    forceResume
                        ? $"plan: resume iteration for story {GitSafe(spawn.StoryId)}"
                        : $"plan: artifacts for story {GitSafe(spawn.StoryId)}", ct);

                // Open PR only if not yet opened
                if (string.IsNullOrEmpty(spawn.PlanPrId))
                {
                    var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                        Branch: planBranch,
                        BaseBranch: baseBranch,
                        Title: $"[plan] {story.Title}",
                        Body: $"Planning artifacts for story `{GitSafe(spawn.StoryId)}` produced by the architect crew.",
                        Repo: repo,
                        WorkItemId: story.Id,
                        Workstream: null,
                        Epic: spawn.StoryId), ct);

                    var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                    spawnRow.PlanBranch = planBranch;
                    spawnRow.PlanPrId = prId;
                    spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
                    await ClearLastErrorAsync(db, spawnRow, ct);
                    await db.SaveChangesAsync(ct);

                    if (string.IsNullOrEmpty(spawnRow.QaBranch))
                    {
                        spawnRow.QaBranch = $"story/{GitSafe(spawn.StoryId)}/qa";
                        await db.SaveChangesAsync(ct);
                        await this.git.EnsureBranchAsync(repo, spawnRow.QaBranch, baseBranch, ct);
                    }

                    await EmitChange(hub, "plan-pr-opened", spawnRow, ct);
                }
                else
                {
                    // PR already exists — just clear the error and notify
                    var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                    spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
                    await ClearLastErrorAsync(db, spawnRow, ct);
                    await db.SaveChangesAsync(ct);
                    await EmitChange(hub, "plan-resumed", spawnRow, ct);
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogError(ex,
                    "Plan artifact production failed for spawn {SpawnId} ({StoryId}).",
                    spawn.Id, spawn.StoryId);
                await PersistLastErrorAsync(db, spawn.Id, $"plan artifacts failed: {ex.Message}", hub, ct);
                return false;
            }
        }

        // Step 2 — PR exists. Auto-approve + merge if it's still open.
        // Per the project's "no human gates" rule the plan PR ships to
        // main without a separate review crew pass; the architect crew is
        // the authority on planning artifacts.
        var planPr = await db.Prs.FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId, ct);
        if (planPr is null) return false;

        if (planPr.Col != "merged")
        {
            var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
            var planBranch = spawn.PlanBranch ?? $"story/{GitSafe(spawn.StoryId)}/plan";
            try
            {
                await this.ApproveAndMergeAsync(spawn.PlanPrId, planBranch, reviewer, repo, baseBranch, ct);
                var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                await ClearLastErrorAsync(db, spawnRow, ct);
                await db.SaveChangesAsync(ct);
                await EmitChange(hub, "plan-pr-merged", spawn, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Common causes: merge conflicts vs main, branch protection,
                // failing required checks, PR merged externally (405).
                // Surface to the operator instead of silently deferring — the
                // spawn was visibly stalled for ~1hr without any UI signal.
                this.logger?.LogWarning(ex,
                    "Plan PR {PrId} auto-merge failed; surfacing to operator and deferring to PrSyncJob.",
                    spawn.PlanPrId);
                await PersistLastErrorAsync(db, spawn.Id, $"plan PR merge failed: {ex.Message}", hub, ct);
                return false;
            }
        }

        // Step 3 — PR merged. Advance the spawn to 'decompose' so the next
        // tick runs DriveDecomposeAsync. We bypass the gate-check on
        // PATCH /spawn/phase because the orchestrator IS the agent driver
        // for this transition.
        var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        sRow.Phase = StorySpawnPhases.Decompose;
        sRow.UpdatedAt = DateTimeOffset.UtcNow;
        await ClearLastErrorAsync(db, sRow, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "phase-advanced", sRow, ct);
        return true;
    }

    /// <summary>
    /// Persist a Phase 37 LastError entry on the spawn so the workstream UI
    /// shows the operator why the orchestrator halted. Also broadcasts a
    /// SignalR `spawn-error` event so the open browser tab updates without
    /// waiting for the next /api/state poll.
    /// </summary>
    private static async Task PersistLastErrorAsync(
        ForgeDbContext db, string spawnId, string message, IHubContext<ForgeHub>? hub, CancellationToken ct)
    {
        var row = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (row is null) return;
        row.LastError = message;
        row.LastErrorAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "spawn-error", row, ct);
    }

    private static Task ClearLastErrorAsync(ForgeDbContext db, StorySpawnRow row, CancellationToken ct)
    {
        if (row.LastError is null && row.LastErrorAt is null) return Task.CompletedTask;
        row.LastError = null;
        row.LastErrorAt = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 38 (Layer B.2) — best-effort parse of a decomposition's
    /// <c>estimatedEffort</c> string into a points value. Architect output
    /// is freeform ("3 pts", "M", "2-3 days", "small"); the regex picks up
    /// the most common "{n} pts?" pattern, then T-shirt sizes, then falls
    /// back to <paramref name="fallback"/>. Bounds: 1..21 (fibonacci max).
    /// </summary>
    private static int ParseEffortToPoints(string? estimatedEffort, int fallback)
    {
        if (string.IsNullOrWhiteSpace(estimatedEffort))
            return Math.Clamp(fallback, 1, 21);
        var s = estimatedEffort.Trim();

        // "3 pts", "5pt", "8 points" — pick the first integer followed by pt/pts/point/points.
        var m = System.Text.RegularExpressions.Regex.Match(
            s, @"\b(\d{1,2})\s*(?:pt|pts|point|points)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
            return Math.Clamp(n, 1, 21);

        // T-shirt size shorthand the architect often emits.
        var lower = s.ToLowerInvariant();
        if (lower.Contains("xs") || lower.Contains("trivial")) return 1;
        if (lower.Contains("small")) return 2;
        if (lower == "m" || lower.Contains("medium")) return 3;
        if (lower.Contains("large") || lower.Contains("xl")) return 8;
        if (lower.Contains("xxl") || lower.Contains("epic")) return 13;

        // "2-3 days", "1 day", "1 week" — convert to a coarse points map.
        m = System.Text.RegularExpressions.Regex.Match(s, @"\b(\d{1,2})\s*(?:d|day|days)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && d > 0)
            return Math.Clamp(d <= 1 ? 1 : d <= 2 ? 2 : d <= 3 ? 3 : d <= 5 ? 5 : 8, 1, 21);
        m = System.Text.RegularExpressions.Regex.Match(s, @"\b(\d{1,2})\s*(?:w|week|weeks)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var wk) && wk > 0)
            return Math.Clamp(wk <= 1 ? 8 : wk <= 2 ? 13 : 21, 1, 21);

        return Math.Clamp(fallback, 1, 21);
    }

    /// <summary>
    /// Phase 38 (Layer B.1) — well-known plan-doc filenames mirroring the
    /// architect strategy's specs[] array. Kept here so the orchestrator can
    /// pre-create the WorkItemRows without taking a dependency on the
    /// strategy implementation. Adding a fifth doc requires updates in both
    /// places (small list — duplication is cheaper than indirection).
    /// </summary>
    private static readonly string[] PlanDocFileNames = new[]
    {
        "tech-spec.md",
        "architecture.md",
        "implementation-plan.md",
        "e2e-scenarios.md",
    };

    /// <summary>
    /// Phase 38 (Layer B.1) — ensure each of the four plan docs has a real
    /// <see cref="WorkItemRow"/> backing its synthetic <c>plan-doc:*</c>
    /// workItemId. Idempotent: rows already inserted by a prior tick or by
    /// <c>Phase38Migration</c> are left alone. Each row gets points =
    /// max(1, story.points / 4) so a 1-point story still gets a 1-pt doc
    /// (3-iter budget) and a 13-point story gets 3-pt docs (5-iter budget
    /// each). The id MUST match
    /// <c>$"plan-doc:{spawn.Id}:{filename}"</c> — that's the workItemId
    /// every existing deliverable + transcript event is keyed on, and what
    /// /api/stories/{storyId}/spawn/artifacts/{key}/iterations queries.
    /// </summary>
    private static async Task EnsurePlanDocWorkItemsAsync(
        ForgeDbContext db, StorySpawnRow spawn, WorkItemRow story, CancellationToken ct)
    {
        var docPoints = Math.Max(1, spawn.StoryPoints);
        // List<string> rather than string[] — C# 12's implicit T[] → ReadOnlySpan<T>
        // conversion makes the EF Core LINQ translator route Contains() through
        // MemoryExtensions.Contains, which can't be compiled by the parameter
        // extractor (ReadOnlySpan is a ref struct and violates TRet constraint).
        var docIds = PlanDocFileNames.Select(f => $"plan-doc:{spawn.Id}:{f}").ToList();

        var existing = await db.WorkItems.AsNoTracking()
            .Where(w => docIds.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        foreach (var fileName in PlanDocFileNames)
        {
            var id = $"plan-doc:{spawn.Id}:{fileName}";
            if (existingSet.Contains(id)) continue;
            db.WorkItems.Add(new WorkItemRow
            {
                Id = id,
                ParentId = spawn.StoryId,
                FeatureRef = spawn.StoryId,
                RepoId = spawn.RepoId,
                Provider = "local",
                ProviderRef = null,
                Track = "design",
                Type = "step",
                State = "active",
                Title = $"plan: {fileName}",
                DescriptionMd = $"Plan-phase artifact `{fileName}` for story `{story.Title}`.",
                Points = docPoints,
                Priority = "med",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    // ── decompose ────────────────────────────────────────────────────────────
    private async Task<bool> DriveDecomposeAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Step 1 — generate decomposition (skip if already produced).
        if (string.IsNullOrEmpty(spawn.DecompositionJson))
        {
            // Architect crew reads the merged plan and produces the decomposition.
            // We verify the four expected docs exist on main so the architect
            // really has something to read (28.10 artifact-existence check).
            if (!await this.PlanArtifactsLandedAsync(repo, spawn, ct))
            {
                this.logger?.LogWarning("Plan artifacts missing on main for {StoryId}; deferring decomposition.", spawn.StoryId);
                return false;
            }

            var ws = await this.strategy.ProduceDecompositionAsync(spawn, story, ct);
            if (ws.Count == 0) return false;

            await this.PersistDecompositionAsync(db, spawn, ws, ct);
            await EmitChange(hub, "decomposed", spawn, ct);
            return true;
        }

        // Step 2 — auto-approve + advance to 'implement'. Per the
        // "no human gates" rule, the architect's decomposition lands
        // without an explicit operator click.
        if (!spawn.DecompositionApproved)
        {
            var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            sRow.DecompositionApproved = true;
            sRow.Phase = StorySpawnPhases.Implement;
            sRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "decomposition-approved", sRow, ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Original persist path extracted so the auto-advance branch above
    /// doesn't re-do the workstream + dep insertion every tick.
    /// </summary>
    private async Task PersistDecompositionAsync(
        ForgeDbContext db, StorySpawnRow spawn,
        IReadOnlyList<DecompositionWorkstream> ws, CancellationToken ct)
    {
        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnRow.DecompositionJson = JsonSerializer.Serialize(ws, JsonOpts);
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;

        // Persist workstreams + dep edges (mirrors the manual API path).
        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync(ct) ?? -1;
        var titleToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in ws)
        {
            // Phase 42 — when the entry came from implementation-plan.md the
            // anchor is the stable id basis so the reconciler joins back to
            // the same row on later passes. Fallback (LLM-only path with no
            // anchor): keep the original title-slug derivation.
            var idSlug = !string.IsNullOrEmpty(w.PlanAnchor)
                ? w.PlanAnchor!.Replace('/', '-').Replace('@', '-')
                : SlugFor(w.Title);
            var wsId = $"ws-{GitSafe(spawn.StoryId)}-{idSlug}".ToLowerInvariant();
            titleToId[w.Title] = wsId;
            // Idempotent — leave any pre-existing row alone.
            if (await db.Workstreams.AnyAsync(x => x.Id == wsId, ct)) continue;
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                Sort = ++maxSort,
                RepoId = spawn.RepoId,
                Name = w.Title,
                Branch = $"ws/{GitSafe(spawn.StoryId)}/{idSlug}",
                // Phase 45 — workstreams inherit the spawn's chosen base
                // branch so every coding-task PR targets the right branch.
                Base = BaseBranchOf(spawn),
                Epic = spawn.StoryId,
                Status = "planning",
                WorktreeJson = "[\"(not yet created)\"]",
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Summary = w.Description,
                BarStart = 0, BarEnd = 5, Planned = true,
                StorySpawnId = spawn.Id,
                PlanAnchor = w.PlanAnchor,
                PlanContentHash = w.PlanContentHash,
            });

            // Phase 38 (Layer B.2) — back the workstream with a real
            // WorkItemRow keyed on the same id. Engineer crew + DeliverableLoop
            // read wi.Points for the per-task iteration budget. Idempotent —
            // skip if a row already exists.
            if (!await db.WorkItems.AnyAsync(x => x.Id == wsId, ct))
            {
                db.WorkItems.Add(new WorkItemRow
                {
                    Id = wsId,
                    ParentId = spawn.StoryId,
                    FeatureRef = spawn.StoryId,
                    RepoId = spawn.RepoId,
                    Provider = "local",
                    ProviderRef = null,
                    Track = "dev",
                    Type = "task",
                    State = "active",
                    Title = w.Title,
                    DescriptionMd = w.Description ?? string.Empty,
                    Points = Math.Max(1, spawn.StoryPoints),
                    Priority = "med",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    PlanAnchor = w.PlanAnchor,
                    PlanContentHash = w.PlanContentHash,
                });
            }
        }
        foreach (var w in ws)
        {
            var fromId = titleToId[w.Title];
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                if (!titleToId.TryGetValue(dep, out var depId)) continue;
                if (await db.WorkstreamDeps.AnyAsync(d => d.WorkstreamId == fromId && d.DependsOnId == depId, ct))
                    continue;
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = fromId,
                    DependsOnId = depId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ── implement ────────────────────────────────────────────────────────────
    private async Task<bool> DriveImplementAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);
        var baseBranch = BaseBranchOf(spawn);

        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return false;

        var deps = await db.WorkstreamDeps.ToListAsync(ct);
        var changed = false;
        foreach (var w in workstreams)
        {
            // Already merged into review queue — skip.
            // "cancelled" is the operator-explicit terminal state; the tick
            // must NOT re-claim a cancelled workstream or the cancel button
            // becomes a no-op (observed 2026-05-04: cancel killed the inner
            // invocation but the next 15-second tick fired a fresh iter).
            // The Restart endpoint clears deliverables and flips status back
            // to "active" to revive the loop.
            if (w.Status is "review" or "merging" or "complete" or "cancelled") continue;

            // Block until every dep is merged.
            var blocking = deps
                .Where(d => d.WorkstreamId == w.Id)
                .Select(d => workstreams.FirstOrDefault(ww => ww.Id == d.DependsOnId))
                .Where(ww => ww is not null)
                .Where(ww => ww!.Status is not ("merging" or "complete"))
                .ToList();
            if (blocking.Count > 0) continue;

            // Engineer crew claim — assign BOTH the generator and the
            // evaluator agent ids so the workstream UI shows the actual
            // pair that DeliverableLoop will run (CrewStoryArtifactStrategy
            // mirrors this resolution internally). Falls back to the
            // legacy single-agent allocation row when the crew table has
            // no engineer crew configured.
            var assignedAgents = await ResolveImplPairAsync(db, this.allocation, ct);
            w.AgentsJson = JsonSerializer.Serialize(assignedAgents);
            w.Status = "active";
            await db.SaveChangesAsync(ct);

            // Branch + commit + PR. Branch is rooted at the spawn's chosen
            // base branch so the diff against the PR target is the workstream's
            // changes only.
            // If the workstream row carries a stale Base (e.g. from a
            // pre-Phase-45 row, or before the operator changed the spawn
            // base mid-flight), bring it forward here so the persisted row
            // stays consistent with what we actually opened the PR against.
            if (!string.Equals(w.Base, baseBranch, StringComparison.Ordinal))
            {
                w.Base = baseBranch;
            }
            await this.git.EnsureBranchAsync(repo, w.Branch, baseBranch, ct);

            // Check if the gen/eval loop already passed for this workstream.
            // If so, skip re-running the loop and use the cached workspace
            // files. Without this check, DriveImplementAsync re-enters the
            // full loop from scratch on every orchestrator tick because the
            // DeliverableLoop resume logic skips "pass" verdicts.
            var lastDel = (await db.Deliverables.AsNoTracking()
                    .Where(d => d.WorkItemId == w.Id)
                    .ToListAsync(ct))
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();
            IReadOnlyDictionary<string, string> files;
            if (lastDel is not null && string.Equals(lastDel.Verdict, "pass", StringComparison.OrdinalIgnoreCase))
            {
                // Already passed — read files from the cached workspace
                // instead of re-running the loop.
                this.logger?.LogInformation(
                    "Workstream {WsId} already passed (iter {Iter}, score {Score}). Skipping gen/eval, using cached workspace.",
                    w.Id, lastDel.Iter, lastDel.Score);
                files = await this.strategy.ReadCachedWorkstreamFilesAsync(spawn, w, ct);
            }
            else
            {
                files = await this.strategy.ProduceWorkstreamChangesAsync(spawn, w, ct);
            }

            if (files.Count > 0)
            {
                await this.git.CommitFilesAsync(repo, w.Branch, files,
                    $"impl({w.Id}): {w.Name}", ct);
            }

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: w.Branch,
                BaseBranch: baseBranch,
                Title: $"[impl] {w.Name}",
                Body: $"Implementation of `{w.Id}` for story `{GitSafe(spawn.StoryId)}`.",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: w.Id,
                Epic: spawn.StoryId), ct);

            var prsList = ParseStringList(w.PrsJson);
            if (!prsList.Contains(prId)) prsList.Add(prId);
            w.PrsJson = JsonSerializer.Serialize(prsList);
            w.Status = "review";
            await db.SaveChangesAsync(ct);

            await EmitChange(hub, "ws-pr-opened", spawn, ct);
            changed = true;
        }
        return changed;
    }

    // ── review ───────────────────────────────────────────────────────────────
    private async Task<bool> DriveReviewAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        var baseBranch = BaseBranchOf(spawn);
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);
        var changed = false;
        foreach (var w in workstreams)
        {
            if (w.Status != "review") continue;
            var prs = ParseStringList(w.PrsJson);
            if (prs.Count == 0) continue;
            var prId = prs[0];

            // Round 0 → post comments. Engineer fixes immediately. Round 1 → approve & merge.
            // We track rounds via comment count: 0 comments yet = round 0.
            var commentRows = await db.PrComments.Where(c => c.PrId == prId).ToListAsync(ct);
            if (commentRows.Count == 0)
            {
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                var comments = await this.strategy.ProduceReviewCommentsAsync(spawn, w, prId, round: 0, ct);
                if (comments.Count == 0)
                {
                    // No findings — straight to approve and merge.
                    await this.ApproveAndMergeAsync(prId, w.Branch, reviewer, repo, baseBranch, ct);
                    w.Status = "merging";
                    await db.SaveChangesAsync(ct);
                    changed = true;
                    continue;
                }

                foreach (var c in comments)
                {
                    await this.provider.PostPrCommentAsync(prId, c.FilePath, c.Line, c.Body, reviewer, ct);
                }

                // Engineer immediately addresses each comment.
                var engineerId = ParseStringList(w.AgentsJson).FirstOrDefault() ?? "eng-default";
                var openComments = await db.PrComments.Where(c => c.PrId == prId && c.State == "active").ToListAsync(ct);
                foreach (var oc in openComments)
                {
                    var fix = await this.strategy.ProduceReviewFixAsync(spawn, w,
                        new ReviewCommentContext(oc.FilePath ?? string.Empty, oc.Line ?? 0, oc.Body, engineerId), ct);
                    await this.git.CommitFilesAsync(repo, w.Branch,
                        new Dictionary<string, string>(StringComparer.Ordinal) { [fix.FilePath] = fix.NewContent },
                        $"fix({w.Id}): address review comment", ct);
                    await this.provider.ResolvePrCommentAsync(oc.Id, engineerId, ct);
                }
                changed = true;
                continue;
            }

            // Round 1+: any open comments? If not, approve and merge.
            var stillOpen = commentRows.Count(c => c.State == "active");
            if (stillOpen == 0)
            {
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                await this.ApproveAndMergeAsync(prId, w.Branch, reviewer, repo, baseBranch, ct);
                w.Status = "merging";
                await db.SaveChangesAsync(ct);
                changed = true;
            }
        }
        return changed;
    }

    // ── qa ───────────────────────────────────────────────────────────────────
    private async Task<bool> DriveQaAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);
        var baseBranch = BaseBranchOf(spawn);

        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);

        // Step 1 — produce the QA feature files & open the QA PR (idempotent).
        if (string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qaBranch = spawn.QaBranch ?? $"story/{GitSafe(spawn.StoryId)}/qa";
            await this.git.EnsureBranchAsync(repo, qaBranch, baseBranch, ct);
            var files = await this.strategy.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);
            await this.git.CommitFilesAsync(repo, qaBranch, files,
                $"qa: feature files for story {GitSafe(spawn.StoryId)}", ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: qaBranch,
                BaseBranch: baseBranch,
                Title: $"[qa] {story.Title}",
                Body: $"QA feature files for story `{GitSafe(spawn.StoryId)}`.",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: null,
                Epic: spawn.StoryId), ct);

            var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            spawnRow.QaBranch = qaBranch;
            spawnRow.QaPrId = prId;
            spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Auto-merge the QA PR (review of QA artifacts is implicit in the deterministic flow).
            var qaReviewer = this.allocation.ForTeam("QA").FirstOrDefault()?.Id ?? "qa-default";
            await this.ApproveAndMergeAsync(prId, qaBranch, qaReviewer, repo, baseBranch, ct);
            await EmitChange(hub, "qa-pr-merged", spawnRow, ct);
            return true;
        }

        // Step 2 — after QA PR merged, write step definitions per workstream
        // and open per-workstream step-def PRs (separate from impl PR).
        var qaPr = await db.Prs.FirstOrDefaultAsync(p => p.Id == spawn.QaPrId, ct);
        if (qaPr is null || qaPr.Col != "merged") return false;

        var changed = false;
        foreach (var w in workstreams)
        {
            var stepBranch = $"ws/{GitSafe(spawn.StoryId)}/{w.Id.Split('-').Last()}-steps";
            // Have we already created a step PR for this workstream?
            var existing = await db.Prs
                .Where(p => p.Branch == stepBranch && p.Workstream == w.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null) continue;

            await this.git.EnsureBranchAsync(repo, stepBranch, baseBranch, ct);
            var files = await this.strategy.ProduceStepDefinitionsAsync(spawn, w, ct);
            await this.git.CommitFilesAsync(repo, stepBranch, files,
                $"qa-steps({w.Id}): step definitions", ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: stepBranch,
                BaseBranch: baseBranch,
                Title: $"[qa-steps] {w.Name}",
                Body: $"Step definitions for `{w.Id}` (story `{GitSafe(spawn.StoryId)}`).",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: w.Id,
                Epic: spawn.StoryId), ct);

            // Approve + merge.
            var qaId = this.allocation.ForTeam("QA").FirstOrDefault()?.Id ?? "qa-default";
            await this.ApproveAndMergeAsync(prId, stepBranch, qaId, repo, baseBranch, ct);

            // Stash on workstream PrsJson so artifacts endpoint surfaces it.
            var prsList = ParseStringList(w.PrsJson);
            if (!prsList.Contains(prId)) prsList.Add(prId);
            w.PrsJson = JsonSerializer.Serialize(prsList);
            w.Status = "complete";
            await db.SaveChangesAsync(ct);
            changed = true;
        }

        // Once every workstream is complete, advance to done.
        if (workstreams.All(ww => ww.Status == "complete"))
        {
            var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            spawnRow.Phase = StorySpawnPhases.Done;
            spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "done", spawnRow, ct);
            changed = true;
        }
        return changed;
    }

    // ── done ─────────────────────────────────────────────────────────────────
    private async Task<bool> DriveDoneAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Transition the upstream ADO work item to Closed (idempotent).
        // ProductStory-sourced spawns have no upstream provider record — we
        // mark the local ProductStoryRow as 'done' instead.
        if (story.State is "closed" or "resolved") return false;
        var hasUpstream = await db.WorkItems.AnyAsync(w => w.Id == spawn.StoryId, ct);
        if (hasUpstream)
        {
            await this.provider.TransitionWorkItemAsync(story.Id, "closed", ct);
        }
        else
        {
            var ps = await db.ProductStories.FirstOrDefaultAsync(s => s.Id == spawn.StoryId, ct);
            if (ps is not null)
            {
                ps.Status = "done";
                await db.SaveChangesAsync(ct);
            }
        }
        await EmitChange(hub, "story-closed", spawn, ct);
        return true;
    }

    /// <summary>
    /// Best-effort approve + merge sequence used by every phase that ships a
    /// PR. The approve call is swallowed because a single-operator Forge is
    /// always the PR author, and GitHub returns 422 on self-approval; that's
    /// not a reason to abort the merge. The merge + git-merge are NOT caught
    /// here — the caller decides whether to defer (catch externally) or let
    /// the scheduler surface the failure.
    /// <para>
    /// Phase 45 — <paramref name="baseBranch"/> is the spawn's chosen target
    /// branch (defaults to <c>"main"</c>). Each caller passes its own
    /// resolved value so the local <c>git merge</c> matches the upstream PR
    /// merge target.
    /// </para>
    /// </summary>
    private async Task ApproveAndMergeAsync(
        string prId, string branch, string reviewerAgentId,
        RepoRow? repo, string baseBranch, CancellationToken ct)
    {
        try
        {
            await this.provider.ApprovePrAsync(prId, reviewerAgentId, ct);
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(
                "PR {PrId} approve skipped ({Reason}); proceeding to merge.",
                prId, ex.Message);
        }

        await this.provider.MergePrAsync(prId, ct);
        await this.git.MergeAsync(repo, branch, baseBranch, ct);
    }

    /// <summary>
    /// Resolves the [generator, evaluator] agent ids for the engineer crew.
    /// Mirrors what <c>CrewStoryArtifactStrategy</c> does internally so the
    /// UI's <c>WorkstreamRow.AgentsJson</c> matches the pair the loop
    /// actually invokes. Falls back to the legacy single-agent allocation
    /// when no engineer crew is configured.
    /// </summary>
    private static async Task<string[]> ResolveImplPairAsync(
        ForgeDbContext db, AgentAllocationLoader allocation, CancellationToken ct)
    {
        const string prefix = "crew-eng";
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct);
        if (crew is null)
        {
            var fallback = allocation.ForTeam("Engineer").FirstOrDefault()?.Id ?? "eng-default";
            return new[] { fallback };
        }

        var gen = crew.DefaultGeneratorAgentId ?? crew.Lead;
        var eval = crew.DefaultEvaluatorAgentId;
        if (string.IsNullOrEmpty(eval) && !string.IsNullOrEmpty(crew.PairedWithCrewId))
        {
            var paired = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct);
            eval = paired?.Lead;
        }
        if (string.IsNullOrEmpty(gen)) gen = allocation.ForTeam("Engineer").FirstOrDefault()?.Id ?? "eng-default";
        return string.IsNullOrEmpty(eval) ? new[] { gen! } : new[] { gen!, eval! };
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private async Task<bool> PlanArtifactsLandedAsync(RepoRow? repo, StorySpawnRow spawn, CancellationToken ct)
    {
        // Phase 45 — verify the four plan artifacts on the spawn's chosen
        // base branch (defaults to "main"). The plan PR merges into the
        // spawn's base branch, not necessarily main, so this is the branch
        // downstream phases will read from.
        var baseBranch = BaseBranchOf(spawn);
        var paths = new[]
        {
            $"docs/stories/{GitSafe(spawn.StoryId)}/tech-spec.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/architecture.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/implementation-plan.md",
            $"docs/stories/{GitSafe(spawn.StoryId)}/e2e-scenarios.md",
        };
        foreach (var p in paths)
        {
            if (!await this.git.FileExistsAsync(repo, baseBranch, p, ct)) return false;
        }
        return true;
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static string GitSafe(string storyId) => StoryPaths.Safe(storyId);

    private static string SlugFor(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "ws" : s;
    }

    private static async Task EmitChange(IHubContext<ForgeHub>? hub, string kind, StorySpawnRow spawn, CancellationToken ct)
    {
        if (hub is null) return;
        await hub.Clients.All.SendAsync("story-spawn.changed", new
        {
            kind,
            id = spawn.Id,
            storyId = spawn.StoryId,
            phase = spawn.Phase,
        }, ct);
    }
}
