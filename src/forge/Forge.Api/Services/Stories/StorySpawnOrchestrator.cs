// -----------------------------------------------------------------------
// <copyright file="StorySpawnOrchestrator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Phase 51 Fix 5 — per-spawn drive lock. Two concurrent
    /// <see cref="DriveAsync(string, bool, CancellationToken)"/> calls
    /// race on the unprotected <c>EnsureBranchFreshAsync</c> force-push
    /// and ADO rejects the second with
    /// <c>TF401028: The reference has already been updated by another
    /// client</c>. Concurrent callers happen in production:
    /// <list type="bullet">
    /// <item>POST /spawn's background <c>Task.Run</c> (request returned 201).</item>
    /// <item><c>SpawnDependencyScheduler</c> 15 s tick.</item>
    /// <item>PATCH /spawn/phase / /spawn/decompose/approve operator actions.</item>
    /// </list>
    /// Keyed by deterministic <c>spawn.Id = spawn-{safeStoryId}</c> (not
    /// <c>storyId</c> — the spawn-id matches the gate's claim semantics
    /// and is what every other branch/path slug uses). Acquired
    /// <summary>
    /// Per-spawn drive gate. Wraps a semaphore with an acquisition timestamp
    /// so the scheduler can detect stuck ticks (e.g. an agent invocation
    /// that exceeded its timeout but never released the semaphore). When a
    /// gate has been held longer than <see cref="DriveStuckMinutes"/>, the
    /// scheduler replaces it with a fresh instance and proceeds — the old
    /// (stuck) drive's eventual <c>Release()</c> goes to the discarded
    /// semaphore and is harmless.
    /// </summary>
    private sealed class DriveGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset AcquiredAt { get; set; }
    }

    private const int DriveStuckMinutes = 15;

    private static readonly ConcurrentDictionary<string, DriveGate> driveLocks =
        new(StringComparer.Ordinal);

    private readonly IServiceProvider services;
    private readonly IStoryArtifactStrategy strategy;
    private readonly IStoryGitOps git;
    private readonly IStoryProviderOps provider;
    private readonly AgentAllocationLoader allocation;
    private readonly Forge.Api.Realtime.TaskChannel? taskChannel;
    private readonly ILogger<StorySpawnOrchestrator>? logger;

    public StorySpawnOrchestrator(
        IServiceProvider services,
        IStoryArtifactStrategy strategy,
        IStoryGitOps git,
        IStoryProviderOps provider,
        AgentAllocationLoader allocation,
        ILogger<StorySpawnOrchestrator>? logger = null,
        Forge.Api.Realtime.TaskChannel? taskChannel = null)
    {
        this.services = services;
        this.strategy = strategy;
        this.git = git;
        this.provider = provider;
        this.allocation = allocation;
        this.logger = logger;
        this.taskChannel = taskChannel;
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

        // Phase 51 Fix 5 — per-spawn drive lock. spawn.Id is deterministic
        // from storyId (`spawn-{safeStoryId}`) so we can derive it without
        // a DB round-trip and avoid spending DI scope creation when a
        // concurrent drive is already in flight.
        var spawnId = $"spawn-{StoryPaths.Safe(storyId)}";
        var gate = driveLocks.GetOrAdd(spawnId, _ => new DriveGate());
        if (!await gate.Semaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Check if the in-flight drive is stuck (agent timeout, hung
            // I/O, etc.). If held longer than DriveStuckMinutes, replace
            // the gate so this tick can proceed. The old drive's eventual
            // Release() goes to the discarded semaphore — harmless.
            var elapsed = DateTimeOffset.UtcNow - gate.AcquiredAt;
            if (elapsed.TotalMinutes < DriveStuckMinutes)
            {
                this.logger?.LogInformation(
                    "DriveAsync: drive for spawn {SpawnId} already in flight ({ElapsedMin:F1}m); skipping.",
                    spawnId, elapsed.TotalMinutes);
                return;
            }

            this.logger?.LogWarning(
                "DriveAsync: drive for spawn {SpawnId} stuck for {ElapsedMin:F1}m (>{ThresholdMin}m); "
                + "forcing re-entry with fresh gate.",
                spawnId, elapsed.TotalMinutes, DriveStuckMinutes);
            var fresh = new DriveGate();
            driveLocks[spawnId] = fresh;
            gate = fresh;
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }

        gate.AcquiredAt = DateTimeOffset.UtcNow;
        try
        {
            using var scope = this.services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
            var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
            if (spawn is null) return;
            await this.RunOneStepAsync(db, hub, spawn, forceResume, ct);
        }
        finally
        {
            gate.Semaphore.Release();
        }
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
                $"{spawn.Phase} failed: {FlattenMessage(ex)}", hub, ct);
        }
        return advanced;
    }

    /// <summary>
    /// Phase 45 follow-up — flatten an exception chain into a single string
    /// so EF Core's "An error occurred while saving the entity changes. See
    /// the inner exception for details." actually shows the inner SqliteException
    /// to the operator. Without this the LastError banner is useless for
    /// any DB failure.
    /// </summary>
    private static string FlattenMessage(Exception ex)
    {
        if (ex.InnerException is null) return ex.Message;
        var sb = new System.Text.StringBuilder(ex.Message);
        var inner = ex.InnerException;
        while (inner is not null)
        {
            sb.Append(" → ");
            sb.Append(inner.Message);
            inner = inner.InnerException;
        }
        return sb.ToString();
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
    /// Phase 46 — return whether the spawn has auto-merge enabled. Honoured
    /// for any base, including <c>"main"</c>; the operator is responsible
    /// for arming the flag deliberately.
    /// </summary>
    private static bool AutoMergeOf(StorySpawnRow spawn) => spawn.AutoMerge;

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
        // Phase 53 — sequential plan-doc path. Detection is strictly by
        // column presence: sequential-mode spawns are initialised with
        // PlanDocPrsJson = "[]" at spawn-create time, legacy rows
        // (created before Phase 53 shipped) carry null. No row ever flips
        // between paths mid-life, so the discriminator is unambiguous.
        if (PlanDocStateHelpers.IsSequentialMode(spawn))
        {
            return await this.DrivePlanSequentialAsync(db, hub, spawn, story, repo, forceResume, ct);
        }
        return await this.DrivePlanLegacyAsync(db, hub, spawn, story, repo, forceResume, ct);
    }

    /// <summary>
    /// Phase 53 — sequential drive. Plan docs run one at a time in canonical
    /// order (architecture → tech-spec → implementation-plan → e2e-scenarios).
    /// Each doc gets its own branch (<c>story/{id}/plan-{key}</c>), its own
    /// worktree (<c>.worktree/plan-{key}</c>), and its own PR. Doc N+1's
    /// gen↔eval loop only starts after doc N's PR is merged AND its latest
    /// deliverable verdict is <c>pass</c>.
    /// </summary>
    private async Task<bool> DrivePlanSequentialAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, bool forceResume, CancellationToken ct)
    {
        var baseBranch = BaseBranchOf(spawn);
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);

        // Same plan-phase invariant as the legacy path: no workstream may
        // be active while Phase=Plan. Cancels stale picks from a regressed
        // implement→plan transition.
        await this.EnforcePlanPhaseInvariantAsync(db, spawn, ct);

        // Ensure backing WorkItemRows for each plan doc exist (deliverables /
        // transcripts / queue rows all key on plan-doc:{spawn.Id}:{file}).
        await EnsurePlanDocWorkItemsAsync(db, spawn, story, ct);

        var states = PlanDocStateHelpers.GetPlanDocStates(spawn).ToList();
        var docOrder = PlanDocStateHelpers.PlanDocOrder;
        for (var i = 0; i < docOrder.Count; i++)
        {
            var docKey = docOrder[i];
            var entry = states.FirstOrDefault(e =>
                string.Equals(e.Key, docKey, StringComparison.OrdinalIgnoreCase));
            var prevMerged = i == 0 || await IsDocCompleteAsync(db, spawn, docOrder[i - 1], ct);

            if (entry is null || entry.Branch is null)
            {
                // Doc N has no branch yet. Wait for doc N-1 to be complete
                // (PR merged AND deliverable verdict=pass).
                if (!prevMerged)
                {
                    // Surface a soft error so the operator sees why the chain isn't moving.
                    if (i > 0)
                    {
                        await PersistLastErrorAsync(db, spawn.Id,
                            $"Plan chain waiting on '{docOrder[i - 1]}' to complete (PR merged + verdict=pass) before starting '{docKey}'.",
                            hub, ct);
                    }
                    return false;
                }

                // Crash-recovery anchor: persist {branch, prId=null} BEFORE
                // any git work so a crash mid-iter resumes idempotently.
                var newBranch = PlanDocStateHelpers.CanonicalDocBranch(spawn.StoryId, docKey);
                states = ReplaceOrAppend(states, new PlanDocPrEntry(docKey, newBranch, PrId: null));
                var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                PlanDocStateHelpers.SetPlanDocStates(spawnRow, states);
                spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
                await ClearLastErrorAsync(db, spawnRow, ct);
                await db.SaveChangesAsync(ct);
                entry = new PlanDocPrEntry(docKey, newBranch, PrId: null);
            }

            if (entry.PrId is null)
            {
                // Branch is recorded but PR not yet opened. Either first
                // run for this doc, or resume after a crash between
                // commit and PR open. Both paths re-enter
                // ProduceSinglePlanArtifactAsync — the strategy's
                // workspace-cache short-circuit + pass-verdict guard
                // keep the work idempotent.
                try
                {
                    return await this.RunPlanDocAsync(db, hub, spawn, story, repo, baseBranch,
                        docKey, entry.Branch!, forceResume, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogError(ex,
                        "Plan doc '{Doc}' production failed for spawn {SpawnId} ({StoryId}).",
                        docKey, spawn.Id, spawn.StoryId);
                    await PersistLastErrorAsync(db, spawn.Id,
                        $"plan doc '{docKey}' failed: {FlattenMessage(ex)}", hub, ct);
                    return false;
                }
            }

            // PR exists — auto-approve + merge if not merged yet.
            var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId, ct);
            if (pr is null)
            {
                // PR row vanished (manual DB edit / external cleanup). Clear
                // the prId so the next tick re-opens. Surface so the operator
                // sees the recovery action.
                await PersistLastErrorAsync(db, spawn.Id,
                    $"plan doc '{docKey}' PR row {entry.PrId} not found; clearing for re-open.", hub, ct);
                states = ReplaceOrAppend(states, new PlanDocPrEntry(docKey, entry.Branch, PrId: null));
                var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                PlanDocStateHelpers.SetPlanDocStates(sRow, states);
                sRow.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return false;
            }

            if (!string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
            {
                // Phase 53 mirrors the "no human gate on plan PRs" rule —
                // architect crew is the authority; auto-approve + merge.
                // Also gate on verdict=pass: an externally-merged PR with
                // a stalled verdict still holds the chain. The merge happens
                // here on the same tick whenever both gates open.
                if (!await IsDocVerdictPassAsync(db, spawn, docKey, ct))
                {
                    await PersistLastErrorAsync(db, spawn.Id,
                        $"plan doc '{docKey}' verdict not 'pass'; PR merge blocked until gen-eval loop reaches pass.",
                        hub, ct);
                    return false;
                }
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                try
                {
                    await this.ApproveAndMergeAsync(entry.PrId!, entry.Branch!, reviewer, repo, baseBranch, ct);
                    var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                    await ClearLastErrorAsync(db, sRow, ct);
                    await db.SaveChangesAsync(ct);
                    await EmitChange(hub, "plan-doc-pr-merged", sRow, ct);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "Plan doc '{Doc}' PR {PrId} auto-merge failed; surfacing to operator.",
                        docKey, entry.PrId);
                    await PersistLastErrorAsync(db, spawn.Id,
                        $"plan doc '{docKey}' PR merge failed: {FlattenMessage(ex)}", hub, ct);
                    return false;
                }
            }

            // PR merged. Free the worktree disk for this doc (forensic-only
            // now that the content is on base). Best-effort: failures here
            // log + continue.
            if (repo is not null && !string.IsNullOrEmpty(entry.Branch))
            {
                await this.git.RemoveWorktreeAsync(repo, entry.Branch!, ct);
            }
            // Continue to the next doc on the same tick — quick walk
            // through already-completed docs is cheap.
        }

        // All four docs merged. Re-verify the all-passed gate (catches a
        // deliverable that got hand-edited away from pass between merges).
        var gate = await ArePlanDocsAllPassedAsync(db, spawn, ct);
        if (!gate.AllPassed)
        {
            await PersistLastErrorAsync(db, spawn.Id,
                $"Plan docs not all passed; phase advance blocked: {string.Join(", ", gate.NonPassedKeys)}",
                hub, ct);
            return false;
        }

        var spawnAdvance = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnAdvance.Phase = StorySpawnPhases.Decompose;
        spawnAdvance.UpdatedAt = DateTimeOffset.UtcNow;
        await ClearLastErrorAsync(db, spawnAdvance, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "phase-advanced", spawnAdvance, ct);
        return true;
    }

    /// <summary>
    /// Phase 53 — one drive of a single plan doc: ensure branch + worktree
    /// at the chosen base, run the gen↔eval loop via the strategy, commit
    /// the result, merge upstream base in, and open (or recover) the per-doc
    /// PR. Persists state writes between each step so a crash leaves an
    /// unambiguous recovery anchor in <c>PlanDocPrsJson</c>.
    /// </summary>
    private async Task<bool> RunPlanDocAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, string baseBranch,
        string docKey, string docBranch, bool forceResume, CancellationToken ct)
    {
        // First-drive of THIS doc on a fresh spawn: orphan-heal the branch.
        // The hasAnyPlanDeliverable check mirrors the legacy DrivePlanAsync
        // (line 392-402) but scoped to this doc only.
        var docWorkItemId = $"plan-doc:{spawn.Id}:{PlanDocStateHelpers.PlanDocFileName[docKey]}";
        var hasAnyDeliverable = await db.Deliverables
            .AnyAsync(d => d.WorkItemId == docWorkItemId, ct);
        var isFirstDrive = !hasAnyDeliverable;
        if (isFirstDrive)
        {
            await this.git.EnsureBranchFreshAsync(repo, docBranch, baseBranch, ct);
        }
        else
        {
            await this.git.EnsureBranchAsync(repo, docBranch, baseBranch, ct);
        }

        // Worktree at the doc's canonical slug. Base is spawn.BaseBranch
        // which by this point includes all upstream-merged plan docs.
        var worktreePath = await this.git.EnsureWorktreeAsync(
            repo, docBranch, baseBranch, ct,
            worktreeSlug: PlanDocStateHelpers.CanonicalDocWorktreeSlug(docKey));

        // Scrub already-tracked .forge/ from the index before the agent runs.
        await this.git.ScrubForgeScratchFromIndexAsync(worktreePath, ct);

        // Produce the doc content via the strategy. ProduceSinglePlanArtifactAsync
        // writes the file into the worktree as a side-effect (the agent's
        // file-write tool) AND returns the content string.
        var (repoPath, content) = await this.strategy.ProduceSinglePlanArtifactAsync(
            spawn, story, docKey, worktreePath, docBranch, repo, forceResume, ct);

        // Commit. CommitFilesAsync is no-op when the file content hasn't
        // changed since the per-iter commit (the strategy commits per iter
        // via CommitPathInWorktreeAsync). This final commit is a belt-and-
        // suspenders write that catches "agent edited file mid-iter but
        // per-iter commit was skipped".
        await this.git.CommitFilesAsync(repo, docBranch,
            PlanDocStateHelpers.CanonicalDocWorktreeSlug(docKey),
            new Dictionary<string, string>(StringComparer.Ordinal) { [repoPath] = content },
            forceResume
                ? $"plan-{docKey}: resume iteration for story {GitSafe(spawn.StoryId)}"
                : $"plan-{docKey}: artifact for story {GitSafe(spawn.StoryId)}",
            ct);

        // Merge upstream base in (picks up any sibling docs merged while
        // this one was iterating, plus any concurrent changes from other
        // streams of work).
        await this.git.MergeUpstreamIntoBranchAsync(repo, docBranch, baseBranch, ct: ct);

        // Idempotent PR open: if a PR already exists for this branch (e.g.
        // crash between OpenPrAsync and SaveChangesAsync on a prior tick),
        // reuse it. Otherwise open a fresh one.
        var existing = await db.Prs.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Branch == docBranch
                && p.Col != "merged" && p.Col != "closed", ct);
        string prId;
        if (existing is not null)
        {
            prId = existing.Id;
        }
        else
        {
            prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: docBranch,
                BaseBranch: baseBranch,
                Title: $"[plan-{docKey}] {story.Title}",
                Body: $"Planning artifact `{PlanDocStateHelpers.PlanDocFileName[docKey]}` for story `{GitSafe(spawn.StoryId)}` produced by the architect crew.",
                Repo: repo,
                WorkItemId: docWorkItemId,
                Workstream: null,
                Epic: spawn.StoryId,
                AutoMerge: AutoMergeOf(spawn)), ct);
        }

        // Persist the prId. From now on the next tick takes the "PR exists"
        // branch above and either merges or short-circuits.
        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        var states = PlanDocStateHelpers.GetPlanDocStates(spawnRow).ToList();
        states = ReplaceOrAppend(states, new PlanDocPrEntry(docKey, docBranch, prId));
        PlanDocStateHelpers.SetPlanDocStates(spawnRow, states);
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;

        // Phase 53 — once any plan doc PR is opened, ensure the QA branch
        // also exists (the legacy DrivePlanAsync did this on first plan PR
        // open; QA is a separate downstream branch keyed off the spawn).
        if (string.IsNullOrEmpty(spawnRow.QaBranch))
        {
            spawnRow.QaBranch = $"story/{GitSafe(spawn.StoryId)}/qa";
            await this.git.EnsureBranchFreshAsync(repo, spawnRow.QaBranch, baseBranch, ct);
        }

        await ClearLastErrorAsync(db, spawnRow, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "plan-doc-pr-opened", spawnRow, ct);
        return true;
    }

    /// <summary>
    /// Phase 53 — true when the latest deliverable for <paramref name="docKey"/>
    /// has verdict=pass. Sibling to <see cref="ArePlanDocsAllPassedAsync"/>
    /// but scoped to one doc; used to gate the per-doc PR-merge step in the
    /// sequential drive.
    /// </summary>
    private static async Task<bool> IsDocVerdictPassAsync(
        ForgeDbContext db, StorySpawnRow spawn, string docKey, CancellationToken ct)
    {
        if (!PlanDocStateHelpers.PlanDocFileName.TryGetValue(docKey, out var fileName)) return false;
        var docId = $"plan-doc:{spawn.Id}:{fileName}";
        // Materialize then order client-side: SQLite EF Core can't translate
        // ORDER BY on DateTimeOffset (the same workaround
        // ArePlanDocsAllPassedAsync uses for the multi-doc gate).
        var rows = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == docId)
            .ToListAsync(ct);
        var latest = rows
            .OrderByDescending(d => d.Iter)
            .ThenByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        return latest is not null
            && string.Equals(latest.Verdict, "pass", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase 53 — true when <paramref name="docKey"/> is COMPLETE: its
    /// recorded PR is merged AND its latest deliverable verdict is pass.
    /// Used by the sequential drive to gate the next doc's start.
    /// </summary>
    private static async Task<bool> IsDocCompleteAsync(
        ForgeDbContext db, StorySpawnRow spawn, string docKey, CancellationToken ct)
    {
        var entry = PlanDocStateHelpers.GetEntry(spawn, docKey);
        if (entry.PrId is null) return false;
        var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId, ct);
        if (pr is null || !string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase)) return false;
        return await IsDocVerdictPassAsync(db, spawn, docKey, ct);
    }

    /// <summary>
    /// Phase 53 — replace the entry matching <c>entry.Key</c> in
    /// <paramref name="states"/> with <paramref name="replacement"/>, or
    /// append when no entry exists for the key. Preserves the input
    /// ordering; canonical ordering is re-applied by
    /// <see cref="PlanDocStateHelpers.SetPlanDocStates"/> on serialize.
    /// </summary>
    private static List<PlanDocPrEntry> ReplaceOrAppend(
        List<PlanDocPrEntry> states, PlanDocPrEntry replacement)
    {
        var idx = states.FindIndex(e =>
            string.Equals(e.Key, replacement.Key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            states[idx] = replacement;
        }
        else
        {
            states.Add(replacement);
        }
        return states;
    }

    // ── plan (legacy) ────────────────────────────────────────────────────────
    /// <summary>
    /// Legacy single-PR plan drive. Retained for spawns created before
    /// Phase 53 shipped (rows with <see cref="StorySpawnRow.PlanDocPrsJson"/>
    /// null). All new spawns route through
    /// <see cref="DrivePlanSequentialAsync"/>.
    /// </summary>
    private async Task<bool> DrivePlanLegacyAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, bool forceResume, CancellationToken ct)
    {
        var baseBranch = BaseBranchOf(spawn);
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);

        // Phase 48 follow-up — invariant guard. While spawn.Phase=plan, no
        // workstream of this spawn may carry Status="active". The "active"
        // label is set exclusively by DriveImplementAsync's claim sweep
        // (line ~978); a workstream ending up active in plan phase means
        // a prior phase=implement excursion ran the claim sweep, then
        // phase regressed back to Plan WITHOUT the regression's
        // CancelInFlightImplTasksAsync running (e.g. the spawn was created
        // before that helper existed, or the regression path took a code
        // path that didn't include the cleanup). The orchestrator only
        // calls DriveImplementAsync — and therefore the regression cleanup —
        // when phase ∈ {implement, review, qa}, so once a spawn drops back
        // to plan, the stale active rows would persist forever.
        //
        // Operator-reported on failover-cluster:XRAFT 2026-05-09 — 16
        // workstreams visible as "active" while all 4 plan-docs were still
        // iterating. The /api/stories/{id}/spawn payload echoed those
        // active labels into the swarm view, making it look like impl
        // work was happening when in reality nothing was claimed (no open
        // agent_claims, no live transcript events).
        //
        // We cancel any open queue rows + best-effort runner invocations
        // for these workstreams so a stranded picked-up task can't keep
        // streaming events post-regression. Blocked / review / merging /
        // complete / cancelled / historic rows are preserved — they
        // represent legitimate persistent state across phase moves.
        await this.EnforcePlanPhaseInvariantAsync(db, spawn, ct);

        // Early exit: if the plan PR is already merged, skip artifact
        // production and advance to decompose immediately. This prevents
        // a long-running retry (ProducePlanArtifactsAsync) from blocking
        // phase progression when the PR merged via auto-merge or externally.
        // Phase 48 — gated. An externally-merged PR does NOT bypass the
        // all-passed gate; if any doc is still iterating / stalled, hold
        // at Plan and surface LastError so the operator sees the blockage.
        if (!string.IsNullOrEmpty(spawn.PlanPrId))
        {
            var mergeCheck = await db.Prs.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId, ct);
            if (mergeCheck is not null
                && string.Equals(mergeCheck.Col, "merged", StringComparison.OrdinalIgnoreCase))
            {
                var (earlyAllPassed, earlyNonPassed) = await ArePlanDocsAllPassedAsync(db, spawn, ct);
                if (!earlyAllPassed)
                {
                    await PersistLastErrorAsync(db, spawn.Id,
                        $"Plan PR merged but docs not all passed; phase advance blocked: {string.Join(", ", earlyNonPassed)}",
                        hub, ct);
                    // Fall through to Step 1 below — we want
                    // ProducePlanArtifactsAsync to run for the non-passed
                    // docs even though the PR is already merged.
                }
                else
                {
                    var earlyRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                    earlyRow.Phase = StorySpawnPhases.Decompose;
                    earlyRow.UpdatedAt = DateTimeOffset.UtcNow;
                    await ClearLastErrorAsync(db, earlyRow, ct);
                    await db.SaveChangesAsync(ct);
                    await EmitChange(hub, "phase-advanced", earlyRow, ct);

                    // Phase 49 follow-up — all plan docs passed AND the PR
                    // is merged; the plan worktree on disk is now stale
                    // (the docs are live on main, per-iter snapshots inside
                    // the worktree are forensic-only). Free the disk.
                    // Best-effort: failures here log + continue; we've
                    // already saved the phase advance.
                    if (repo is not null && !string.IsNullOrEmpty(earlyRow.PlanBranch))
                    {
                        await this.git.RemoveWorktreeAsync(repo, earlyRow.PlanBranch!, ct);
                    }

                    return true;
                }
            }
        }

        // Step 1 — PR doesn't exist yet: produce artifacts, commit, open PR.
        // Also re-enters when forceResume=true and PR exists: re-generate
        // stalled/iterate docs, commit new content, push to existing PR.
        // Phase 48 — also re-enters whenever ANY plan-doc's latest deliverable
        // verdict isn't 'pass'. Replaces the older "any doc lacks deliverables"
        // check with the stricter "all four have a passing terminal deliverable"
        // gate; combined with the Phase 45 pass-verdict guard inside
        // ProducePlanFileAsync, only the non-passed docs run the gen-eval
        // loop and passed docs short-circuit to cached worktree content.
        var (planAllPassed, planNonPassedKeys) = await ArePlanDocsAllPassedAsync(db, spawn, ct);
        var notAllPassed = !planAllPassed;
        if (string.IsNullOrEmpty(spawn.PlanPrId) || forceResume || notAllPassed)
        {
            var planBranch = spawn.PlanBranch ?? $"story/{GitSafe(spawn.StoryId)}/plan";
            try
            {
                // Phase 50 — first-drive detection. When a fresh spawn is
                // being driven for the first time (no plan PR yet AND no
                // plan-doc deliverables yet), force-reset the plan branch
                // to base so an orphan tip left over on origin by a prior
                // deleted spawn (whose `git push origin --delete` failed
                // silently — observed 2026-05-11 local:HIGH-MEM-CONSUMPTION
                // re-spawn) can't leak old plan-doc commits into the new
                // architect's worktree. Subsequent drives use the regular
                // EnsureBranchAsync so the orchestrator's own commits
                // aren't blown away.
                var hasAnyPlanDeliverable = await db.Deliverables
                    .AnyAsync(d => d.WorkItemId.StartsWith($"plan-doc:{spawn.Id}:"), ct);
                var isFirstDrive = string.IsNullOrEmpty(spawn.PlanPrId) && !hasAnyPlanDeliverable;
                if (isFirstDrive)
                {
                    await this.git.EnsureBranchFreshAsync(repo, planBranch, baseBranch, ct);
                }
                else
                {
                    await this.git.EnsureBranchAsync(repo, planBranch, baseBranch, ct);
                }

                await EnsurePlanDocWorkItemsAsync(db, spawn, story, ct);

                var artifacts = await this.strategy.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);
                await this.git.CommitFilesAsync(repo, planBranch,
                    PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug(),
                    artifacts,
                    forceResume
                        ? $"plan: resume iteration for story {GitSafe(spawn.StoryId)}"
                        : $"plan: artifacts for story {GitSafe(spawn.StoryId)}", ct);

                // Open PR only if not yet opened
                if (string.IsNullOrEmpty(spawn.PlanPrId))
                {
                    // Phase 46 — merge latest from main into the plan branch
                    // before creating the PR. Always enforced.
                    await this.git.MergeUpstreamIntoBranchAsync(repo, planBranch, "main", ct: ct);

                    var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                        Branch: planBranch,
                        BaseBranch: baseBranch,
                        Title: $"[plan] {story.Title}",
                        Body: $"Planning artifacts for story `{GitSafe(spawn.StoryId)}` produced by the architect crew.",
                        Repo: repo,
                        WorkItemId: story.Id,
                        Workstream: null,
                        Epic: spawn.StoryId,
                        AutoMerge: AutoMergeOf(spawn)), ct);

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
                        // Phase 50 — first-time QA branch creation: same
                        // orphan-heal logic as the plan branch above. The
                        // QaBranch field is only written once per spawn
                        // lifetime so this guard is exact.
                        await this.git.EnsureBranchFreshAsync(repo, spawnRow.QaBranch, baseBranch, ct);
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
                await PersistLastErrorAsync(db, spawn.Id, $"plan artifacts failed: {FlattenMessage(ex)}", hub, ct);
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
                await PersistLastErrorAsync(db, spawn.Id, $"plan PR merge failed: {FlattenMessage(ex)}", hub, ct);
                return false;
            }
        }

        // Step 3 — PR merged. Advance the spawn to 'decompose' so the next
        // tick runs DriveDecomposeAsync. We bypass the gate-check on
        // PATCH /spawn/phase because the orchestrator IS the agent driver
        // for this transition.
        // Phase 48 — re-check the all-passed gate before advancing. The PR
        // can be merged externally (operator clicked Merge on ADO/GitHub
        // before all docs hit pass — Bug A on the failover-cluster:XRAFT
        // spawn) so the PR-merged signal alone isn't sufficient. This gate
        // closes the impl-starts-while-docs-still-iterating regression.
        var (advanceAllPassed, advanceNonPassedKeys) = await ArePlanDocsAllPassedAsync(db, spawn, ct);
        if (!advanceAllPassed)
        {
            await PersistLastErrorAsync(db, spawn.Id,
                $"Plan docs not all passed; phase advance blocked: {string.Join(", ", advanceNonPassedKeys)}",
                hub, ct);
            return false;
        }

        var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        sRow.Phase = StorySpawnPhases.Decompose;
        sRow.UpdatedAt = DateTimeOffset.UtcNow;
        await ClearLastErrorAsync(db, sRow, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "phase-advanced", sRow, ct);

        // Phase 49 follow-up — plan docs all passed AND the PR is merged;
        // the plan worktree on disk is now stale. Free the disk. Matches
        // the same cleanup at the early-return fast path above.
        if (repo is not null && !string.IsNullOrEmpty(sRow.PlanBranch))
        {
            await this.git.RemoveWorktreeAsync(repo, sRow.PlanBranch!, ct);
        }

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
        "architecture.md",
        "tech-spec.md",
        "implementation-plan.md",
        "e2e-scenarios.md",
    };

    /// <summary>
    /// Spec Phase 48 — gate helper. Returns whether every plan-doc has a
    /// terminal <c>verdict='pass'</c> on its latest deliverable. ACCEPT-overridden
    /// docs (Phase 43) flip their latest deliverable to <c>pass</c>, so they
    /// count as passed here.
    ///
    /// <para>Called from four orchestrator entry points to enforce the
    /// "impl never starts before all plan docs complete" invariant:
    /// <list type="bullet">
    /// <item><c>DrivePlanAsync</c> Step 1 — re-trigger artifact production when any doc isn't passed.</item>
    /// <item><c>DrivePlanAsync</c> Step 3 — refuse phase advance to <c>Decompose</c> when any doc isn't passed.</item>
    /// <item><c>DriveDecomposeAsync</c> entry — regress phase to <c>Plan</c> when any doc isn't passed (recovers from spawns whose phase advanced before this fix shipped).</item>
    /// <item><c>DriveImplementAsync</c> entry — regress phase to <c>Plan</c>; halts NEW workstream claims (in-flight rows continue to completion per Q48.3).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static async Task<(bool AllPassed, IReadOnlyList<string> NonPassedKeys)>
        ArePlanDocsAllPassedAsync(ForgeDbContext db, StorySpawnRow spawn, CancellationToken ct)
    {
        var docIds = PlanDocFileNames.Select(f => $"plan-doc:{spawn.Id}:{f}").ToList();
        // Materialize the full deliverable history for these docs in one
        // query, then pick the latest per doc client-side. SQLite EF can't
        // GROUP BY + ORDER BY DateTimeOffset without translation drift.
        var rows = await db.Deliverables.AsNoTracking()
            .Where(d => docIds.Contains(d.WorkItemId))
            .ToListAsync(ct);

        var nonPassed = new List<string>();
        foreach (var fileName in PlanDocFileNames)
        {
            var docId = $"plan-doc:{spawn.Id}:{fileName}";
            var latest = rows
                .Where(d => d.WorkItemId == docId)
                .OrderByDescending(d => d.Iter)
                .ThenByDescending(d => d.CreatedAt)
                .FirstOrDefault();

            // The artifact "key" used by Resume / ACCEPT / Restart endpoints
            // is the filename without the .md suffix (matches BuildPlanDocSpecs).
            var key = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

            if (latest is null
                || !string.Equals(latest.Verdict, "pass", StringComparison.OrdinalIgnoreCase))
            {
                nonPassed.Add(key);
            }
        }

        return (nonPassed.Count == 0, nonPassed);
    }

    /// <summary>
    /// Phase 38 (Layer B.1) — ensure each of the four plan docs has a real
    /// <see cref="WorkItemRow"/> backing its synthetic <c>plan-doc:*</c>
    /// workItemId. Idempotent: rows already inserted by a prior tick or by
    /// <c>Phase38Migration</c> are left alone. Each row gets points =
    /// max(3, story.points). The 3-point floor was added 2026-05-13 (Phase
    /// 60) after observing architecture.md plan-doc spawns repeatedly
    /// hitting the per-task max-turns cap on 2-point stories (`points *
    /// 30 = 60` turns is exhausted by the architect's grep-verify
    /// protocol + incremental edits + sibling reads). A 3-point floor
    /// gives every plan-doc ≥90 turns and ≥15 min wall-clock
    /// (<see cref="BudgetForPhase"/>'s `plan` prefix floor matches).
    /// Higher-point stories keep their full points (no `/4` division here;
    /// that's the historical <see cref="Phase38Migration"/> formula).
    /// The id MUST match
    /// <c>$"plan-doc:{spawn.Id}:{filename}"</c> — that's the workItemId
    /// every existing deliverable + transcript event is keyed on, and what
    /// /api/stories/{storyId}/spawn/artifacts/{key}/iterations queries.
    /// </summary>
    internal static async Task EnsurePlanDocWorkItemsAsync(
        ForgeDbContext db, StorySpawnRow spawn, WorkItemRow story, CancellationToken ct)
    {
        var docPoints = Math.Max(3, spawn.StoryPoints);
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
    /// <summary>
    /// Spec Phase 48 follow-up — when the orchestrator regresses from
    /// Implement / Decompose back to Plan because plan docs aren't all
    /// passed, halt every in-flight workstream agent + queue row for the
    /// spawn. Without this, impl-phase iterations keep burning tokens
    /// and clobbering the workstream branch even though the plan they
    /// were based on is now invalidated.
    ///
    /// <para>Best-effort: failures on individual cancellations log but
    /// don't surface as a regression failure (the phase regression
    /// itself is the operator-visible signal). Workstreams flip to
    /// <c>'planning'</c> so the impl scheduler will re-claim them with
    /// fresh agent pairs when the spawn re-advances to Implement.</para>
    /// </summary>
    private async Task CancelInFlightImplTasksAsync(
        ForgeDbContext db, StorySpawnRow spawn, CancellationToken ct)
    {
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return;

        var wsIds = workstreams.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

        // Find every open queue row tied to a workstream of this spawn.
        // Open states are listed in TaskQueueStates.OpenStates.
        var openQueueRows = await db.TaskQueue
            .Where(r => wsIds.Contains(r.WorkItemId)
                     && r.State != TaskQueueStates.Completed
                     && r.State != TaskQueueStates.Cancelled)
            .ToListAsync(ct);

        foreach (var q in openQueueRows)
        {
            q.State = TaskQueueStates.Cancelled;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
            q.LastError = "Phase regressed to Plan — impl tasks cancelled.";
        }

        // Flip the workstream rows back to 'planning' so the impl
        // scheduler will re-claim them once the spawn re-advances. Skip
        // historic / cancelled / complete rows (terminal states are
        // preserved per Phase 42).
        foreach (var ws in workstreams)
        {
            if (ws.Status is "complete" or "cancelled" or "paused" or "merging" or WorkstreamStatuses.Historic)
            {
                continue;
            }
            ws.Status = "planning";
            ws.LastActive = (DateTimeOffset.UtcNow - new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
                .TotalDays;
        }

        // Cancel each agent invocation. CancelByTaskAsync is per-work-item;
        // looping is fine — production workstream counts are bounded.
        try
        {
            using var scope = this.services.CreateScope();
            var runner = scope.ServiceProvider.GetService<Forge.Api.Agents.IAgentRunner>();
            if (runner is not null)
            {
                foreach (var wsId in wsIds)
                {
                    try { await runner.CancelByTaskAsync(wsId, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger?.LogDebug(ex,
                            "CancelInFlightImplTasksAsync: runner.CancelByTaskAsync({WsId}) failed; queue row already cancelled.",
                            wsId);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "CancelInFlightImplTasksAsync: agent-runner resolution failed; queue rows cancelled but agent invocations may keep streaming until they self-detect cancellation.");
        }
    }

    /// <summary>
    /// Phase 48 follow-up — invariant enforcement called at the start of
    /// <see cref="DrivePlanAsync"/>. Plan phase MUST NOT have any workstream
    /// in <c>active</c> status — that label is set exclusively by the
    /// impl claim sweep, so its presence means a prior phase=implement
    /// excursion's regression cleanup didn't run. Flip those rows back
    /// to <c>planning</c> and cancel any open queue rows + runner
    /// invocations for them so a stranded picked-up task can't keep
    /// streaming events. Other non-terminal statuses (blocked / review /
    /// merging) are preserved — they represent legitimate persistent
    /// state across phase moves and shouldn't be coerced just because
    /// the spawn is currently in plan.
    /// </summary>
    private async Task EnforcePlanPhaseInvariantAsync(
        ForgeDbContext db, StorySpawnRow spawn, CancellationToken ct)
    {
        var stranded = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id && w.Status == "active")
            .ToListAsync(ct);
        if (stranded.Count == 0) return;

        var strandedIds = stranded.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

        var openQueueRows = await db.TaskQueue
            .Where(r => strandedIds.Contains(r.WorkItemId)
                     && r.State != TaskQueueStates.Completed
                     && r.State != TaskQueueStates.Cancelled)
            .ToListAsync(ct);
        foreach (var q in openQueueRows)
        {
            q.State = TaskQueueStates.Cancelled;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
            q.LastError = "Spawn is in plan phase — impl tasks blocked until plan-docs all pass.";
        }

        foreach (var w in stranded)
        {
            w.Status = "planning";
            w.LastActive = (DateTimeOffset.UtcNow - new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
                .TotalDays;
        }

        await db.SaveChangesAsync(ct);

        this.logger?.LogInformation(
            "EnforcePlanPhaseInvariant: spawn {SpawnId} ({StoryId}) — flipped {Count} stranded 'active' workstream(s) back to 'planning' (queue rows cancelled: {QueueCount}).",
            spawn.Id, spawn.StoryId, stranded.Count, openQueueRows.Count);

        // Best-effort cancel of any in-flight agent invocations for these
        // workstream ids. CancelByTaskAsync is per-work-item; looping is
        // fine — the stranded set is bounded by a single spawn's workstream
        // count. Mirrors CancelInFlightImplTasksAsync's catch-and-log
        // pattern so a runner failure doesn't break the tick.
        try
        {
            using var scope = this.services.CreateScope();
            var runner = scope.ServiceProvider.GetService<Forge.Api.Agents.IAgentRunner>();
            if (runner is not null)
            {
                foreach (var wsId in strandedIds)
                {
                    try { await runner.CancelByTaskAsync(wsId, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger?.LogDebug(ex,
                            "EnforcePlanPhaseInvariant: runner.CancelByTaskAsync({WsId}) failed; queue row already cancelled.",
                            wsId);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "EnforcePlanPhaseInvariant: agent-runner resolution failed; queue rows cancelled but agent invocations may keep streaming until they self-detect cancellation.");
        }
    }

    private async Task<bool> DriveDecomposeAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Phase 48 — recover from spawns whose phase advanced under the
        // pre-fix Bug A. If any plan-doc isn't passed, regress phase to
        // Plan and let DrivePlanAsync re-run the gen-eval loops (the
        // strategy's pass-verdict guard short-circuits the already-passed
        // docs; only non-passed ones run).
        // Phase 48 follow-up — consolidated single SaveChangesAsync for the
        // regression. The earlier two-save shape (Phase + SaveChanges,
        // then PersistLastErrorAsync's own save) raised the chance of a
        // "database is locked" race against a concurrent writer on
        // SQLite's single-writer journal. One write keeps the
        // transaction window tight.
        var (decomposeAllPassed, decomposeNonPassed) = await ArePlanDocsAllPassedAsync(db, spawn, ct);
        if (!decomposeAllPassed)
        {
            // Phase 48 follow-up — cancel any in-flight impl tasks BEFORE
            // saving the phase regression so the cancel writes batch in
            // the same transaction window. Reduces SQLite lock contention.
            await CancelInFlightImplTasksAsync(db, spawn, ct);

            var sBack = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            sBack.Phase = StorySpawnPhases.Plan;
            sBack.LastError = $"Phase regressed to Plan: docs not all passed: {string.Join(", ", decomposeNonPassed)}";
            sBack.LastErrorAt = DateTimeOffset.UtcNow;
            sBack.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "spawn-error", sBack, ct);
            await EmitChange(hub, "phase-regressed", sBack, ct);
            return true;
        }

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

        // Step 2 — backfill the WorkstreamDeps FK table from each row's
        // DepsJson before we hand the spawn to DriveImplementAsync. The
        // PlanReconciler writes deps as resolved stage TITLES into DepsJson
        // during plan phase but doesn't insert the FK rows; the FK rows are
        // what DriveImplementAsync's claim sweep reads to compute "every dep
        // merged?". Without this backfill the sweep sees an empty FK table,
        // treats every stage as ready, and claims all 21 stages at once on
        // the first impl tick (observed 2026-05-09 on failover-cluster:XRAFT).
        await SyncWorkstreamDepsFromJsonAsync(db, spawn, ct);

        // Step 3 — auto-approve + advance to 'implement'. Per the
        // "no human gates" rule, the architect's decomposition lands
        // without an explicit operator click. The earlier `if (!Approved)`
        // guard misfired on already-approved spawns: it skipped the phase
        // flip, leaving the spawn parked at decompose forever (observed
        // 2026-05-09 on failover-cluster:XRAFT — decomposition was approved
        // the moment it landed, but Phase=decompose persisted). Make the
        // advance idempotent — any time we reach here in decompose phase,
        // Step 1 has already produced the decomposition, so the conditions
        // for the advance are met.
        var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        var changed = false;
        if (!sRow.DecompositionApproved) { sRow.DecompositionApproved = true; changed = true; }
        if (sRow.Phase == StorySpawnPhases.Decompose)
        {
            sRow.Phase = StorySpawnPhases.Implement;
            changed = true;
        }
        if (changed)
        {
            sRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "decomposition-approved", sRow, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Phase 49 — backfill <see cref="WorkstreamDepRow"/> rows from each
    /// workstream's <c>DepsJson</c> (which carries resolved stage TITLES).
    /// <see cref="PlanReconciler"/> writes <c>DepsJson</c> during plan
    /// phase but never touches the FK table; <see cref="PersistDecompositionAsync"/>
    /// only writes FK rows when a workstream is freshly created — so when
    /// the rows already exist (PlanReconciler-seeded), the FK table stays
    /// empty. This helper closes the gap: walks each workstream of the
    /// spawn, parses <c>DepsJson</c>, resolves each title to a workstream
    /// id via the same-spawn name→id map, and inserts any missing
    /// <see cref="WorkstreamDepRow"/>. Idempotent — existing rows and
    /// titles that don't resolve to a sibling are skipped.
    /// </summary>
    private static async Task SyncWorkstreamDepsFromJsonAsync(
        ForgeDbContext db, StorySpawnRow spawn, CancellationToken ct)
    {
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return;

        var nameToId = workstreams
            .Where(w => w.Status != "cancelled" && w.Status != "paused" && w.Status != WorkstreamStatuses.Historic)
            .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var wsIds = workstreams.Select(w => w.Id).ToList();
        var existing = await db.WorkstreamDeps
            .Where(d => wsIds.Contains(d.WorkstreamId))
            .ToListAsync(ct);
        var existingSet = new HashSet<(string From, string To)>(
            existing.Select(e => (e.WorkstreamId, e.DependsOnId)));

        var added = 0;
        foreach (var w in workstreams)
        {
            if (string.IsNullOrEmpty(w.DepsJson)) continue;
            List<string>? titles;
            try { titles = JsonSerializer.Deserialize<List<string>>(w.DepsJson); }
            catch { continue; }
            if (titles is null) continue;
            foreach (var t in titles)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!nameToId.TryGetValue(t.Trim(), out var depId)) continue;
                if (string.Equals(depId, w.Id, StringComparison.Ordinal)) continue;
                if (existingSet.Contains((w.Id, depId))) continue;
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = w.Id,
                    DependsOnId = depId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                existingSet.Add((w.Id, depId));
                added++;
            }
        }
        if (added > 0) await db.SaveChangesAsync(ct);
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

            // Phase 46 — persist deps + phase-anchor so the runtime
            // scheduler reads dependency edges directly from the row
            // (no plan re-parse) and the swarm DAG view groups by phase.
            var depsJson = w.Deps is { Count: > 0 }
                ? JsonSerializer.Serialize(w.Deps, JsonOpts)
                : null;

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
                DepsJson = depsJson,
                PhaseAnchor = w.PhaseAnchor,
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
        // Phase 48 — gate the impl claim sweep on plan-doc completion.
        // The user's regression on failover-cluster:XRAFT showed 24
        // workstreams active while all 4 plan-docs were still iterating;
        // this is the explicit "impl never starts before docs complete"
        // requirement. Q48.3: in-flight workstreams (state=picked-up)
        // continue running — only NEW claims are blocked here.
        // Phase 48 follow-up — single SaveChangesAsync (matches DriveDecomposeAsync).
        var (implAllPassed, implNonPassed) = await ArePlanDocsAllPassedAsync(db, spawn, ct);
        if (!implAllPassed)
        {
            // Phase 48 follow-up — operator request 2026-05-09: stop all
            // in-flight impl tasks when phase regresses. Without this the
            // 24 active workstreams keep burning tokens against a plan
            // that's been invalidated.
            await CancelInFlightImplTasksAsync(db, spawn, ct);

            var sBack = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
            sBack.Phase = StorySpawnPhases.Plan;
            sBack.LastError = $"Phase regressed to Plan: docs not all passed: {string.Join(", ", implNonPassed)}";
            sBack.LastErrorAt = DateTimeOffset.UtcNow;
            sBack.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await EmitChange(hub, "spawn-error", sBack, ct);
            await EmitChange(hub, "phase-regressed", sBack, ct);
            return true;
        }

        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);
        var baseBranch = BaseBranchOf(spawn);

        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return false;

        // Phase 49 — read deps from BOTH the FK table and each workstream's
        // DepsJson titles (resolved via same-spawn name→id, excluding
        // cancelled/historic so a tombstone can't shadow a live row).
        // Mirrors the GetSpawn fallback so the runtime claim sweep agrees
        // with the UI even if SyncWorkstreamDepsFromJsonAsync hasn't
        // backfilled the FK rows yet (e.g. an older spawn that never
        // visited decompose).
        var fkDeps = await db.WorkstreamDeps
            .Where(d => workstreams.Select(w => w.Id).Contains(d.WorkstreamId))
            .ToListAsync(ct);
        var nameToId = workstreams
            .Where(w => w.Status != "cancelled" && w.Status != "paused" && w.Status != WorkstreamStatuses.Historic)
            .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var depsByWs = fkDeps
            .GroupBy(d => d.WorkstreamId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(d => d.DependsOnId), StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var w in workstreams)
        {
            if (string.IsNullOrEmpty(w.DepsJson)) continue;
            List<string>? titles;
            try { titles = JsonSerializer.Deserialize<List<string>>(w.DepsJson); }
            catch { continue; }
            if (titles is null) continue;
            if (!depsByWs.TryGetValue(w.Id, out var resolved))
            {
                resolved = new HashSet<string>(StringComparer.Ordinal);
                depsByWs[w.Id] = resolved;
            }
            foreach (var t in titles)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!nameToId.TryGetValue(t.Trim(), out var depId)) continue;
                if (string.Equals(depId, w.Id, StringComparison.Ordinal)) continue;
                resolved.Add(depId);
            }
        }

        // ── Phase 1: sequential claim — fast, DB-safe ─────────────────────
        // Mark every ready workstream as `active` with EMPTY AgentsJson;
        // ensure its branch on origin. Agent assignment happens LATER
        // in Phase 2, right before the agent invocation starts — see
        // the pick-lock block inside the per-task Task.Run below.
        // Phase 49 follow-up — was assigning the (gen, eval) pair here
        // at claim time; that made the assignment go stale by the time
        // the runner actually started (minutes later) and caused every
        // workstream to end up with the same default pair when the
        // resolver couldn't beat the legacy fallback. Late assignment
        // takes a fresh busy-set snapshot inside the parallel task.
        var readyList = new List<(WorkstreamRow ws, string baseBranch, int wsPoints)>();
        foreach (var w in workstreams)
        {
            // Already merged into review queue — skip.
            // "cancelled" is the operator-explicit terminal state; the tick
            // must NOT re-claim a cancelled workstream or the cancel button
            // becomes a no-op (observed 2026-05-04: cancel killed the inner
            // invocation but the next 15-second tick fired a fresh iter).
            // The Restart endpoint clears deliverables and flips status back
            // to "active" to revive the loop.
            //
            // Phase 49 follow-up — "blocked" was REMOVED from this skip
            // list. The historical reason for keeping it (back-compat
            // with rows persisted by older builds where "blocked" meant
            // failure-triage) is no longer worth the cost: failure-triage
            // now writes "stalled" + StallReason, and back-compat rows
            // are migrated to "planning" by the one-off SQL cleanup
            // (admin endpoint /api/admin/workstreams/unblock-stale).
            // Net effect: a workstream sitting at "blocked" with all
            // its deps now complete will re-enter the claim sweep on
            // the next tick instead of waiting for a manual Restart.
            // Observed 2026-05-10 on failover-cluster:XRAFT stages
            // 2.1/2.2/2.3: they ran speculatively before deps were
            // enforced, got marked "blocked" by an older code path, and
            // sat indefinitely after 1.1/1.2/1.3 completed because the
            // skip list excluded them. "stalled" stays in the skip
            // list because that IS the operator-attention state.
            if (w.Status is "review" or "merging" or "complete" or "cancelled" or "paused" or WorkstreamStatuses.Stalled) continue;

            // Block until every dep is merged. Reads from the merged
            // depsByWs map (FK table + DepsJson title fallback) so a
            // stale FK table can't make a downstream stage look ready.
            //
            // Phase 49 follow-up — only "complete" satisfies a dep.
            // The prior check accepted "merging" too on the rationale
            // that the upstream PR was already merged on the provider
            // side at that point. But that left a race window: the
            // downstream worktree could fetch origin/{baseBranch}
            // BEFORE the upstream merge propagated locally, claim
            // against a stale base missing the upstream contribution,
            // and produce code that conflicts at merge time
            // (observed 2026-05-10 on failover-cluster:XRAFT stages
            // 2.2/2.3 starting while 2.1 was still in "merging"
            // limbo). Requiring "complete" guarantees the workstream
            // lifecycle has fully settled (PR.Col="merged",
            // ws.Status="complete") and at least one PrSyncJob fetch
            // has run against the post-merge tip before the
            // downstream worktree is created. ~15-30s slower pipeline,
            // zero stale-base races.
            if (depsByWs.TryGetValue(w.Id, out var depIds) && depIds.Count > 0)
            {
                // Phase 63 — "historic" and "cancelled" deps are treated
                // as satisfied. Historic workstreams belong to a superseded
                // decomposition whose code has already been merged or made
                // irrelevant by the new plan. Cancelled workstreams were
                // explicitly terminated by the operator. Without this,
                // FK dep edges from new workstreams to old (historic)
                // workstream IDs permanently block the entire DAG (observed
                // 2026-05-14 on qq:MICROSOFT-TEAMS-MESS: 16 workstreams
                // stuck behind 3 historic deps from the old
                // messaging-abstractions-and-data-model phase).
                var blocking = depIds
                    .Select(id => workstreams.FirstOrDefault(ww => ww.Id == id))
                    .Where(ww => ww is not null)
                    .Where(ww => ww!.Status is not "complete"
                        and not WorkstreamStatuses.Historic
                        and not "cancelled")
                    .ToList();
                if (blocking.Count > 0) continue;
            }

            // 2026-05-07 — in-flight + failure-backoff guard. The 15s tick
            // would otherwise spawn a fresh agent invocation every cycle on a
            // workstream whose previous spawn was still running (or had just
            // crashed with the Copilot SDK CLI subprocess dying mid-stream).
            // Observed on WI-HIGH-MEM-CONSUMPTION iter 38: 25 spawns over
            // ~80 minutes, each starting and dying without producing a
            // deliverable row. The guard reads the WS's impl-spawn transcript
            // events and returns SkipInFlight (budget+slack window) or
            // SkipBackoff (consecutive-failures window) when appropriate.
            // See ImplSpawnGuard for the full state machine.
            //
            // 2026-05-12 (Phase 52 follow-up) — the guard now ALSO detects
            // "successful no-op" loops where the orchestrator keeps
            // re-spawning successfully but no new deliverable rows appear.
            // Observed on XRAFT stage 3.1: 3,180 spawn events over 7+ hours
            // with iter stuck at 3/50 because the previous fix path
            // (success=true always allowed) had no signal that work wasn't
            // progressing. On hitting NoProgressReassignThreshold the guard
            // sets ShouldTriggerReassign=true; we park the work item at
            // pending-reassign so Phase 52's SloppyReassignmentReconciler
            // rotates to a fresh pair on the next tick.
            var wsPointsForGuard = await LookupWorkstreamPointsAsync(db, w.Id, spawn.StoryPoints, ct);
            // Phase 64 — supply the in-process active-invocation check so the
            // guard's "overran budget+slack → Allow recovery spawn" branch
            // refuses to fire when the original SDK invocation is still alive.
            // Resolved off the root provider; the dispatcher is registered
            // as a singleton so this never creates a scope conflict.
            var agentRunnerForGuard = this.services.GetService<Forge.Api.Agents.IAgentRunner>();
            Func<string, bool>? isActive = agentRunnerForGuard is null
                ? null
                : agentRunnerForGuard.IsTaskActive;
            var guardDecision = await ImplSpawnGuard.CheckAsync(db, w.Id, wsPointsForGuard, DateTimeOffset.UtcNow, ct, isActive);
            if (guardDecision.Outcome != ImplSpawnGuard.Decision.Allow)
            {
                this.logger?.LogDebug(
                    "Workstream {WsId} skipped this tick: {Outcome} — {Reason}",
                    w.Id, guardDecision.Outcome, guardDecision.Reason);

                // Phase 52 follow-up — Level 3 escalation. After
                // NoProgressReassignThreshold consecutive successful spawns
                // produced no deliverable, route the workstream's work item
                // through Phase 52 reassignment so a fresh pair takes over
                // with a clean slate. Idempotent: if the work item is
                // already pending-reassign or terminally failed we no-op.
                if (guardDecision.ShouldTriggerReassign)
                {
                    await this.TryRouteToSloppyReassignAsync(db, w, guardDecision.ConsecutiveNoProgress, ct);
                }
                else if (guardDecision.Outcome == ImplSpawnGuard.Decision.SkipNoProgressBackoff
                    && guardDecision.ConsecutiveNoProgress == SyncConstants.NoProgressBackoffThreshold)
                {
                    // First tick on which the backoff kicks in — emit a
                    // single visibility event so the operator can see the
                    // stuck state in the conversation tab without scrolling
                    // through hundreds of raw spawn-started/finished pairs.
                    await this.EmitSpawnEventAsync(w.Id, ImplSpawnGuard.SpawnNoProgressKind, new
                    {
                        wsId = w.Id,
                        spawnId = spawn.Id,
                        consecutiveNoProgress = guardDecision.ConsecutiveNoProgress,
                        backoffThreshold = SyncConstants.NoProgressBackoffThreshold,
                        reassignThreshold = SyncConstants.NoProgressReassignThreshold,
                        reason = guardDecision.Reason,
                    }, ct);
                }
                continue;
            }

            // Engineer crew claim — assign agents LATE (Phase 49 follow-up).
            // The legacy code populated AgentsJson here at claim time,
            // but the operator observed all workstreams ending up with the
            // same pair because (a) the resolver returned crew defaults
            // when it ran (now fixed via PairDiversityResolver), and (b)
            // the busy-set snapshot was taken minutes before the agent
            // actually ran, so by the time the runner started the pair
            // could have shifted. The fix: leave AgentsJson empty at
            // claim time; pick the pair INSIDE the parallel task right
            // before strategy.ProduceWorkstreamChangesAsync runs, with a
            // fresh busy-set read inside a shared pick-lock so concurrent
            // tasks see each other's picks.
            if (w.Status != "active")
            {
                w.AgentsJson = "[]";
                w.Status = "active";
            }

            // If the workstream row carries a stale Base, bring it forward.
            if (!string.Equals(w.Base, baseBranch, StringComparison.Ordinal))
            {
                w.Base = baseBranch;
            }
            await db.SaveChangesAsync(ct);
            await this.git.EnsureBranchAsync(repo, w.Branch, baseBranch, ct);

            readyList.Add((w, baseBranch, wsPointsForGuard));
        }
        if (readyList.Count == 0) return false;

        // ── Phase 2: parallel execution — gen↔eval + commit + PR ──────────
        // Each workstream runs in its own Task so multiple agents work
        // concurrently. Each task uses a fresh DbContext scope for its final
        // status update (the original `db` context is not thread-safe).
        // Bounded to 4 concurrent workstreams to avoid overloading agents.
        var changedFlag = 0;
        var semaphore = new SemaphoreSlim(4);
        // Phase 49 follow-up — agent assignment lock. Serialises the
        // "load busy set → pick pair → save AgentsJson" critical section
        // across parallel tasks so two concurrent workstreams don't read
        // the same busy snapshot and pick the same agents. The
        // invocation itself runs OUTSIDE the lock so the lock window
        // is short (DB read + in-memory pick + DB write ≈ <50 ms).
        var pickLock = new SemaphoreSlim(1, 1);
        var tasks = readyList.Select(item => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            // Emit the spawn-started marker BEFORE any orchestrator work so
            // the in-flight guard sees this attempt even if the failure
            // happens during worktree setup / target discovery (i.e. before
            // DeliverableLoop emits task.iteration.started).
            await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnStartedKind, new
            {
                wsId = item.ws.Id,
                spawnId = spawn.Id,
                wsPoints = item.wsPoints,
                startedAt = DateTimeOffset.UtcNow,
            }, ct);

            try
            {
                var (w, bb, _) = item;

                // Phase 49 follow-up — LATE agent assignment, serialised
                // across the parallel tasks via pickLock. Each task takes
                // a fresh busy-set snapshot inside the lock, picks a
                // pair, saves AgentsJson, releases. The invocation runs
                // outside the lock so picking serialisation doesn't cap
                // throughput. The freshly-assigned pair is written into
                // `w.AgentsJson` so ProduceWorkstreamChangesAsync (which
                // reads workstream.AgentsJson) picks up the assignment
                // without a re-fetch.
                await pickLock.WaitAsync(ct);
                try
                {
                    using var pickScope = this.services.CreateScope();
                    var pickDb = pickScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                    var freshBusy = await Forge.Api.Services.BusyAgentsHelper.LoadBusyAgentsAsync(pickDb, ct);

                    // Phase 65 — seed the busy set with evaluator agents
                    // already assigned to sibling workstreams in this spawn.
                    // Without this, PickEvaluator gives DefaultEvaluatorAgentId
                    // (BH) unconditional first-dibs on every tick, because
                    // workstreams clear deps one-per-tick and freshBusy only
                    // tracks currently-in-flight invocations. The sibling
                    // evaluators are no longer in-flight but should still be
                    // excluded so the picker rotates through the roster.
                    var siblingAgentsJson = await pickDb.Workstreams
                        .AsNoTracking()
                        .Where(ws => ws.StorySpawnId == spawn.Id
                            && ws.Id != w.Id
                            && ws.AgentsJson != null)
                        .Select(ws => ws.AgentsJson)
                        .ToListAsync(ct);
                    foreach (var sj in siblingAgentsJson)
                    {
                        var sibPair = ParseStringList(sj);
                        if (sibPair.Count >= 2 && !string.IsNullOrEmpty(sibPair[1]))
                        {
                            freshBusy.Add(sibPair[1]);
                        }
                    }

                    var assignedPair = await ResolveImplPairAsync(
                        pickDb, this.allocation, freshBusy, w.Sort, ct);
                    var assignedJson = JsonSerializer.Serialize(assignedPair);
                    w.AgentsJson = assignedJson;
                    var pickRow = await pickDb.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
                    if (pickRow is not null)
                    {
                        pickRow.AgentsJson = assignedJson;
                        await pickDb.SaveChangesAsync(ct);
                    }
                    this.logger?.LogInformation(
                        "Late agent assignment for {WsId}: pair=[{Pair}] (busySet size at pick={BusyCount})",
                        w.Id, string.Join(", ", assignedPair), freshBusy.Count);
                }
                finally { pickLock.Release(); }

                await this.DriveOneWorkstreamImplAsync(spawn, w, repo, bb, hub, ct);
                Interlocked.Exchange(ref changedFlag, 1);
                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = true,
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Process shutdown — let it propagate naturally. We still
                // emit a spawn-finished event so the in-flight guard releases
                // on the next process boot; "success=false" applies the
                // failure backoff which is the right behavior for an aborted
                // run we can't reason about.
                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = "OperationCanceledException",
                    message = "process shutdown",
                }, CancellationToken.None);
            }
            catch (NoProgressException npe)
            {
                // Phase 47 — the engineer agent produced zero file changes
                // for N consecutive iterations. Mark the workstream stalled
                // immediately (don't apply failure backoff; that would just
                // re-spawn after the window). Operator's Restart endpoint
                // clears deliverables + flips status back to "active" to
                // unblock. Phase 49 — uses the "stalled" status (not
                // "blocked") so the UI can distinguish failure-triage
                // from dependency-waiting. Stores the reason on the
                // workstream row for the tooltip.
                this.logger?.LogWarning(
                    "Workstream {WsId} no-progress after {Iters} consecutive zero-change iters (last iter {LastIter}). "
                    + "Marking stalled. Operator must Restart to retry.",
                    item.ws.Id, npe.ConsecutiveIterations, npe.LastIteration);

                using var noProgScope = this.services.CreateScope();
                var noProgDb = noProgScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var noProgRow = await noProgDb.Workstreams.FirstOrDefaultAsync(
                    ws => ws.Id == item.ws.Id, CancellationToken.None);
                if (noProgRow is not null && noProgRow.Status is not "cancelled" and not "complete")
                {
                    noProgRow.Status = WorkstreamStatuses.Stalled;
                    noProgRow.StallReason =
                        $"No progress: agent produced zero file changes in {npe.ConsecutiveIterations} consecutive iterations "
                        + $"(through iter {npe.LastIteration}). Restart to retry, or escalate to lead.";
                    await noProgDb.SaveChangesAsync(CancellationToken.None);
                    await EmitChange(hub, "ws-stalled", spawn, CancellationToken.None);
                }

                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = nameof(NoProgressException),
                    message = npe.Message,
                    consecutiveNoChange = npe.ConsecutiveIterations,
                    lastIter = npe.LastIteration,
                }, CancellationToken.None);
            }
            catch (BuildFailedException bfe)
            {
                // Phase 47e — the build command rejected the engineer's diff.
                // Two trip points produce this: (a) per-iter, three
                // consecutive broken builds, or (b) post-pass, the iter
                // the evaluator scored at pass still doesn't compile.
                // Both route to Status="stalled" (Phase 49 split — see
                // WorkstreamStatuses.Stalled doc) — re-spawning would
                // just re-burn budget on the same broken code. Operator
                // must Restart (which wipes the worktree + deliverables)
                // to give the agent a clean slate.
                this.logger?.LogWarning(
                    "Workstream {WsId} gate failed: stage={Stage} command={Command} trippedByConsecutive={Trip} "
                    + "consecutiveBroken={Broken} lastIter={LastIter} exit={Exit} errors={Errors}. Marking stalled.",
                    item.ws.Id, bfe.Stage ?? "(legacy)", bfe.Command ?? "(legacy)",
                    bfe.TrippedByConsecutiveIters, bfe.ConsecutiveBrokenIters,
                    bfe.LastIteration, bfe.ExitCode, bfe.ErrorCount);

                using var buildScope = this.services.CreateScope();
                var buildDb = buildScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var buildRow = await buildDb.Workstreams.FirstOrDefaultAsync(
                    ws => ws.Id == item.ws.Id, CancellationToken.None);
                if (buildRow is not null && buildRow.Status is not "cancelled" and not "complete")
                {
                    // Phase 56 -- enrich StallReason with the failing
                    // gate stage + command so the operator can see at a
                    // glance whether they're looking at a `cargo build`
                    // failure, a `cargo fmt --check` failure, or a
                    // `cargo test` regression without having to dig
                    // into the transcript. Stage is null on legacy
                    // build-only paths -- fall back to today's text.
                    var stageLabel = bfe.Stage switch
                    {
                        "quality" => "quality (fmt/clippy)",
                        "test" => "test",
                        "build" => "build",
                        _ => "build",
                    };
                    var commandSuffix = string.IsNullOrWhiteSpace(bfe.Command)
                        ? string.Empty
                        : $" running `{bfe.Command}`";
                    buildRow.Status = WorkstreamStatuses.Stalled;
                    buildRow.StallReason = bfe.TrippedByConsecutiveIters
                        ? $"Gate failed ({stageLabel}){commandSuffix}: {bfe.ConsecutiveBrokenIters} consecutive "
                            + $"failed iterations (through iter {bfe.LastIteration}, exit {bfe.ExitCode}, "
                            + $"{bfe.ErrorCount} error(s)). Operator triage required."
                        : $"Gate ({stageLabel}) rejected pass{commandSuffix} after recovery attempts exhausted "
                            + $"(through iter {bfe.LastIteration}, exit {bfe.ExitCode}, "
                            + $"{bfe.ErrorCount} error(s)). Evaluator scored at pass repeatedly but the gate "
                            + $"kept rejecting the diff. Operator triage required.";
                    await buildDb.SaveChangesAsync(CancellationToken.None);
                    await EmitChange(hub, "ws-stalled", spawn, CancellationToken.None);
                }

                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = nameof(BuildFailedException),
                    message = bfe.Message,
                    exitCode = bfe.ExitCode,
                    errorCount = bfe.ErrorCount,
                    warningCount = bfe.WarningCount,
                    errorSummary = bfe.ErrorSummary,
                    trippedByConsecutiveIters = bfe.TrippedByConsecutiveIters,
                    consecutiveBrokenIters = bfe.ConsecutiveBrokenIters,
                    lastIter = bfe.LastIteration,
                    // Phase 56 -- surface stage + command in the transcript event
                    // so the Conversation/Build tab can render which gate broke.
                    gateStage = bfe.Stage,
                    gateCommand = bfe.Command,
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // 2026-05-07 — escalated from LogWarning to LogError + emit
                // a spawn-finished transcript event with full exception
                // context. Previously the only diagnostic was the type +
                // message in stdout; the live UI's Conversation tab had no
                // breadcrumb explaining why iter X stalled. Now operators
                // can see the failure inline alongside generator/evaluator
                // turns and the in-flight guard correctly applies the
                // failure backoff.
                this.logger?.LogError(ex,
                    "Workstream {WsId} impl failed; will retry after backoff. exception={ExceptionDetail}",
                    item.ws.Id, ex.ToString());
                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = ex.GetType().FullName,
                    message = ex.Message,
                    stackPreview = TrimStack(ex.StackTrace, 1024),
                    innerType = ex.InnerException?.GetType().FullName,
                    innerMessage = ex.InnerException?.Message,
                }, CancellationToken.None);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        return changedFlag != 0;
    }

    /// <summary>
    /// Resolve the points for a workstream's WI, falling back to the story
    /// points when the row is missing. Used by the in-flight guard to size
    /// the budget+slack window. Per-doc points (Layer B.3) live on the
    /// WorkItemRow keyed on the workstream id.
    /// </summary>
    private static async Task<int> LookupWorkstreamPointsAsync(
        ForgeDbContext db, string workstreamId, int storyPoints, CancellationToken ct)
    {
        var p = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workstreamId)
            .Select(w => (int?)w.Points)
            .FirstOrDefaultAsync(ct);
        return Math.Max(1, p ?? storyPoints);
    }

    /// <summary>
    /// Best-effort emit of an impl-spawn transcript event. Swallows all
    /// errors so an emit failure can never abort the orchestrator tick —
    /// the guard's correctness is preserved as long as MOST events land,
    /// and the worst case (a missed emit) just causes one extra recovery
    /// spawn after the guard window elapses.
    /// </summary>
    private async Task EmitSpawnEventAsync(string wsId, string kind, object payload, CancellationToken ct)
    {
        if (this.taskChannel is null) return;
        try
        {
            await this.taskChannel.EmitAsync(wsId, kind, payload, ct: ct);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex,
                "Failed to emit {Kind} for {WsId}; in-flight guard may double-spawn next tick.",
                kind, wsId);
        }
    }

    /// <summary>
    /// Phase 52 follow-up — route a workstream that's been stuck in a
    /// successful no-op respawn loop through the
    /// <c>SloppyReassignmentReconciler</c>. Idempotent: a workstream
    /// already in <c>pending-reassign</c>, <c>failed</c>, or terminal-
    /// failed state is left alone.
    ///
    /// <para>Steps:</para>
    /// <list type="number">
    ///   <item>Load the work item row (workstream id == work item id for
    ///   spawn-flow stage rows; the column is the canonical link).</item>
    ///   <item>If already <c>pending-reassign</c> / <c>failed</c> / no
    ///   row → no-op.</item>
    ///   <item>Append the workstream's current pair (from
    ///   <c>AgentsJson</c>) to <c>PriorPairsJson</c> so the resolver
    ///   excludes them on the next claim.</item>
    ///   <item>Set <c>ExecutionStatus=pending-reassign</c> and emit a
    ///   <c>task.sloppy-parked</c> event so the conversation tab shows
    ///   the routing.</item>
    /// </list>
    /// </summary>
    private async Task TryRouteToSloppyReassignAsync(
        Forge.Api.Persistence.ForgeDbContext db,
        Forge.Api.Persistence.WorkstreamRow w,
        int consecutiveNoProgress,
        CancellationToken ct)
    {
        var wiRow = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == w.Id, ct);
        if (wiRow is null) return;

        // Idempotent guard — don't double-park or override a terminal state.
        if (wiRow.ExecutionStatus == Forge.Api.Persistence.ExecutionStatuses.PendingReassign
            || wiRow.ExecutionStatus == Forge.Api.Persistence.ExecutionStatuses.Failed)
        {
            return;
        }

        // Append the workstream's currently-assigned pair to PriorPairsJson.
        // AgentsJson is a JSON array of agent ids; we use the first two as
        // (generator, evaluator) per Phase 47's pair-assignment convention.
        string[]? currentPair = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(w.AgentsJson) ? "[]" : w.AgentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var arr = doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(2)
                    .ToArray();
                if (arr.Length == 2) currentPair = arr;
            }
        }
        catch (JsonException) { /* malformed — skip pair capture */ }

        if (currentPair is not null)
        {
            var priorPairs = new List<string[]>();
            if (!string.IsNullOrWhiteSpace(wiRow.PriorPairsJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string[]>>(wiRow.PriorPairsJson);
                    if (parsed is not null) priorPairs = parsed;
                }
                catch (JsonException) { /* malformed — start fresh */ }
            }
            var alreadyLast = priorPairs.Count > 0
                && priorPairs[priorPairs.Count - 1].Length == 2
                && string.Equals(priorPairs[priorPairs.Count - 1][0], currentPair[0], StringComparison.Ordinal)
                && string.Equals(priorPairs[priorPairs.Count - 1][1], currentPair[1], StringComparison.Ordinal);
            if (!alreadyLast) priorPairs.Add(currentPair);
            wiRow.PriorPairsJson = JsonSerializer.Serialize(priorPairs);
        }

        wiRow.ExecutionStatus = Forge.Api.Persistence.ExecutionStatuses.PendingReassign;
        wiRow.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        this.logger?.LogWarning(
            "Workstream {WsId} routed to Phase 52 reassignment after {N} consecutive no-progress successful spawns",
            w.Id, consecutiveNoProgress);

        await this.EmitSpawnEventAsync(w.Id, "task.sloppy-parked", new
        {
            wsId = w.Id,
            consecutiveNoProgress,
            reason = $"orchestrator re-spawn loop without new deliverables; routing to SloppyReassignmentReconciler ({consecutiveNoProgress} consecutive no-progress successes)",
            priorPair = currentPair,
        }, ct);
    }

    private static string TrimStack(string? stack, int maxChars)
    {
        if (string.IsNullOrEmpty(stack)) return string.Empty;
        return stack.Length <= maxChars ? stack : stack[..maxChars];
    }

    /// <summary>
    /// Execute the gen↔eval loop, commit, and open PR for a single workstream.
    /// Designed to run in parallel across workstreams — uses its own
    /// <see cref="ForgeDbContext"/> scope for DB writes and re-checks
    /// workstream status before finalizing (guards against cancel/restart
    /// that may have fired mid-loop).
    /// </summary>
    private async Task DriveOneWorkstreamImplAsync(
        StorySpawnRow spawn, WorkstreamRow w, RepoRow? repo,
        string baseBranch, IHubContext<ForgeHub>? hub, CancellationToken ct)
    {
        // Check if the gen/eval loop already passed for this workstream.
        // Use a fresh scope so we don't race the original DbContext.
        using var scope = this.services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var lastDel = (await db2.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == w.Id)
                .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        IReadOnlyDictionary<string, string> files;
        if (lastDel is not null && string.Equals(lastDel.Verdict, "pass", StringComparison.OrdinalIgnoreCase))
        {
            this.logger?.LogInformation(
                "Workstream {WsId} already passed (iter {Iter}, score {Score}). Skipping gen/eval, using cached workspace.",
                w.Id, lastDel.Iter, lastDel.Score);
            files = await this.strategy.ReadCachedWorkstreamFilesAsync(spawn, w, ct);
        }
        else if (lastDel is not null && lastDel.Verdict is "stalled" or "stalled-budget" or "reject" or "sloppy")
        {
            // Phase 47 — non-pass terminal verdicts route to a triage state
            // instead of re-spawning the gen↔eval loop.
            //
            // Phase 51 — `stalled-budget` is the one verdict in this set
            // that is NOT a dead end: the operator can raise the crew's
            // MaxTaskBudgetUsd and the row should resume automatically.
            // Re-evaluate against the CURRENT cap right here. If
            // cumulative cost is now under cap, the cap was raised since
            // the deliverable was written — fall through to the else
            // branch (produces a fresh iteration) instead of re-stalling.
            // This is the orchestrator-side mirror of
            // BudgetCooldownReconciler.TryResumeAsync; both ends must
            // agree so the reconciler doesn't clear the stall and the
            // orchestrator immediately re-set it on the next tick.
            //
            // Phase 52 — `sloppy` (score plateau, generator token cap, or
            // iter cap) is auto-recovered by SloppyReassignmentReconciler:
            // it picks a fresh pair (clean slate) up to MaxPairAttempts,
            // then terminates. The orchestrator MUST NOT mark the
            // workstream stalled on a sloppy verdict — that would race
            // the reconciler. Only when PairAttemptCount has hit the cap
            // (terminal failure) do we set the stall + reason; the
            // reconciler does this transition itself via
            // MarkTerminalFailedAsync, so we just no-op here to avoid
            // double-stalling.
            if (string.Equals(lastDel.Verdict, "sloppy", StringComparison.Ordinal))
            {
                var wiSloppy = await db2.WorkItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == w.Id, ct);
                var atCap = wiSloppy is not null
                    && wiSloppy.PairAttemptCount >= ScoringConstants.MaxPairAttempts;
                this.logger?.LogInformation(
                    "DriveOneWorkstreamImpl: ws={WsId} lastVerdict=sloppy iter={Iter} "
                    + "pairAttempt={Pair}/{Max} atCap={AtCap}",
                    w.Id, lastDel.Iter,
                    wiSloppy?.PairAttemptCount ?? 0, ScoringConstants.MaxPairAttempts, atCap);
                if (!atCap)
                {
                    // Phase 52 hand-off: park the work item at
                    // pending-reassign so SloppyReassignmentReconciler
                    // picks it up on its next 15 s tick and rotates to a
                    // fresh pair. Without this, ExecutionStatus stays at
                    // "queued" (set by the queue worker when it enqueued
                    // the row) and the reconciler's scan
                    // (WHERE ExecutionStatus = "pending-reassign") never
                    // finds it — observed 2026-05-13 on the XRAFT stage
                    // 3.1 workstream looping after restart. Idempotent:
                    // TryRouteToSloppyReassignAsync no-ops when already
                    // in pending-reassign / failed state.
                    await this.TryRouteToSloppyReassignAsync(db2, w, consecutiveNoProgress: 0, ct);
                    return;
                }
                // At cap. The reconciler's MarkTerminalFailedAsync stamps
                // workstream stall + reason. Mirror that here as
                // defense-in-depth in case the reconciler hasn't run yet.
                var sloppyRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
                if (sloppyRow is not null && sloppyRow.Status is not "cancelled" and not "complete")
                {
                    sloppyRow.Status = WorkstreamStatuses.Stalled;
                    sloppyRow.StallReason =
                        "Sloppy-3-attempts: " + ScoringConstants.SloppyTerminalFailureReason;
                    await db2.SaveChangesAsync(ct);
                    await EmitChange(hub, "ws-stalled", spawn, ct);
                }
                return;
            }

            if (string.Equals(lastDel.Verdict, "stalled-budget", StringComparison.Ordinal))
            {
                var crew = await db2.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == lastDel.CrewId, ct);
                // Windowed cost — sum agent_usage from
                // WorkItemRow.BudgetWindowStartUtc forward so a
                // cooldown-elapsed reset (BudgetCooldownReconciler) is
                // honored here too. Both decision points must agree on
                // "what is the current attempt's cost" or the
                // orchestrator re-stalls a row the reconciler just
                // freshened.
                var wiForWindow = await db2.WorkItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == w.Id, ct);
                var windowStart = wiForWindow?.BudgetWindowStartUtc;
                var usageRows = await db2.AgentUsage.AsNoTracking()
                    .Where(u => u.TaskId == w.Id)
                    .ToListAsync(ct);
                var cumulativeCost = windowStart is DateTimeOffset ws
                    ? usageRows.Where(u => u.StartedAt >= ws).Sum(u => u.CostUsd)
                    : usageRows.Sum(u => u.CostUsd);
                var currentCap = crew?.MaxTaskBudgetUsd;
                var capRaised = currentCap is decimal cap && cumulativeCost < cap;
                this.logger?.LogInformation(
                    "DriveOneWorkstreamImpl: ws={WsId} lastVerdict=stalled-budget iter={Iter} "
                    + "cost={Cost} currentCap={Cap} crew={Crew} capRaised={CapRaised}",
                    w.Id, lastDel.Iter,
                    cumulativeCost.ToString("0.00"),
                    currentCap?.ToString("0.00") ?? "(null)",
                    lastDel.CrewId ?? "(null)",
                    capRaised);
                if (capRaised)
                {
                    // Cap was raised since iter N's deliverable was written.
                    // Clear any stale stalled status from a prior tick and
                    // fall through to produce a fresh iteration.
                    var liveRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
                    if (liveRow is not null && liveRow.Status == WorkstreamStatuses.Stalled)
                    {
                        liveRow.Status = "active";
                        liveRow.StallReason = null;
                        liveRow.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        await db2.SaveChangesAsync(ct);
                        await EmitChange(hub, "ws-unstalled-budget-raise", spawn, ct);
                        this.logger?.LogInformation(
                            "Cleared budget-stall on {WsId}; new cap ${Cap} > cost ${Cost}. Driving fresh iteration.",
                            w.Id, currentCap?.ToString("0.00"), cumulativeCost.ToString("0.00"));
                    }
                    files = await this.strategy.ProduceWorkstreamChangesAsync(spawn, w, ct);
                    goto AfterProduce;
                }
                // Cap not raised — fall through to the normal stall path.
            }

            this.logger?.LogWarning(
                "Workstream {WsId} terminal verdict {Verdict} (iter {Iter}, score {Score}) "
                + "failureKind={FailureKind} failureReason={FailureReason}. "
                + "Marking stalled; operator must Restart (or raise cap for stalled-budget) to retry.",
                w.Id, lastDel.Verdict, lastDel.Iter, lastDel.Score,
                lastDel.FailureKind ?? "(none)", lastDel.FailureReason ?? "(none)");

            var blockedRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (blockedRow is not null && blockedRow.Status is not "cancelled" and not "complete")
            {
                blockedRow.Status = WorkstreamStatuses.Stalled;
                blockedRow.StallReason = ComposeStallReason(lastDel);
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }
            return;
        }
        else
        {
            files = await this.strategy.ProduceWorkstreamChangesAsync(spawn, w, ct);
        }

    AfterProduce:
        // Re-check status: operator may have cancelled mid-loop.
        var freshRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
        if (freshRow is null || freshRow.Status is "cancelled" or "paused")
        {
            this.logger?.LogInformation(
                "Workstream {WsId} was cancelled during gen/eval; skipping commit + PR.", w.Id);
            return;
        }

        if (files.Count > 0)
        {
            var wsSlug = await WorkstreamWorktreeSlug.ComputeAsync(w, db2, ct);
            await this.git.CommitFilesAsync(repo, w.Branch, wsSlug, files,
                $"impl({w.Id}): {w.Name}", ct);
        }

        // Phase 46 — merge latest from the spawn's base branch into the
        // workstream branch before creating the PR. Always enforced.
        // Phase 49 follow-up — was hardcoded to "main"; spawns can use
        // a non-main base (e.g. failover-cluster:XRAFT uses
        // feature/xraft) and merging the wrong branch produces
        // spurious "behind" deltas on the PR.
        await this.git.MergeUpstreamIntoBranchAsync(repo, w.Branch, baseBranch, ct: ct);

        // Build rich PR description from the last deliverable, matching the
        // format used by CrewQueueWorker for regular work-item PRs:
        // title, score, iterations, generator/evaluator names, what changed,
        // evaluator review notes.
        var prTitle = $"[impl] {w.Name}";
        var prBody = $"Implementation of `{w.Id}` for story `{GitSafe(spawn.StoryId)}`.";

        // Re-read the latest deliverable after the gen↔eval loop so we
        // capture the pass verdict (the loop may have created new rows).
        var finalDel = (await db2.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == w.Id)
                .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Iter)
            .FirstOrDefault();

        if (finalDel is not null)
        {
            var genSummary = finalDel.GeneratorSummary ?? string.Empty;
            var evalNotes = finalDel.Notes ?? string.Empty;
            var score = finalDel.Score;
            var iter = finalDel.Iter;

            // Extract "## Change Summary" section if present.
            var summaryIdx = genSummary.IndexOf("## Change Summary", StringComparison.OrdinalIgnoreCase);
            if (summaryIdx >= 0)
            {
                genSummary = genSummary[(summaryIdx + "## Change Summary".Length)..].Trim();
                var nextHeading = genSummary.IndexOf("\n## ", StringComparison.Ordinal);
                if (nextHeading > 0) genSummary = genSummary[..nextHeading].Trim();
            }
            if (genSummary.Length > 800) genSummary = genSummary[..800] + "...";

            // Resolve agent display names.
            var genAgentId = finalDel.GeneratorAgentId;
            var evalAgentId = finalDel.EvaluatorAgentId;
            var genAgent = !string.IsNullOrEmpty(genAgentId)
                ? await db2.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == genAgentId, ct) : null;
            var evalAgent = !string.IsNullOrEmpty(evalAgentId)
                ? await db2.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evalAgentId, ct) : null;
            var genName = genAgent?.Name ?? genAgentId ?? "unknown";
            var evalName = evalAgent?.Name ?? evalAgentId;

            // Resolve models from crew config.
            string? genModel = null, evalModel = null;
            var genCrew = !string.IsNullOrEmpty(finalDel.CrewId)
                ? await db2.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == finalDel.CrewId, ct) : null;
            if (genCrew is not null)
            {
                genModel = genAgent?.GeneratorModel ?? genCrew.Model;
                var evalCrew = !string.IsNullOrEmpty(genCrew.PairedWithCrewId)
                    ? await db2.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == genCrew.PairedWithCrewId, ct) : null;
                evalModel = evalAgent?.EvaluatorModel ?? evalCrew?.EvaluatorModel ?? evalCrew?.Model;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## {prTitle}");
            sb.AppendLine();
            sb.AppendLine($"**Story:** {spawn.StoryId}");
            sb.AppendLine($"**Score:** {score} | **Iterations:** {iter}");
            sb.AppendLine();
            sb.Append($"🤖 **Generator:** {genName}");
            if (!string.IsNullOrEmpty(genModel)) sb.Append($" (`{genModel}`)");
            if (!string.IsNullOrEmpty(evalName))
            {
                sb.Append($" | 🤖 **Evaluator:** {evalName}");
                if (!string.IsNullOrEmpty(evalModel)) sb.Append($" (`{evalModel}`)");
            }
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("### What changed");
            sb.AppendLine();
            sb.AppendLine(!string.IsNullOrWhiteSpace(genSummary) ? genSummary : "_No change summary provided._");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(evalName))
            {
                sb.AppendLine("### Evaluator Review");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(evalNotes))
                {
                    var notes = evalNotes.Length > 800 ? evalNotes[..800] + "..." : evalNotes;
                    sb.AppendLine(notes);
                }
                else
                {
                    sb.AppendLine("_No review comment provided._");
                }
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("*Auto-generated by Forge.*");

            prBody = sb.ToString();
            if (prBody.Length > 3900)
                prBody = prBody[..3900] + "\n\n…_(truncated — full details in evaluator notes)_";
        }

        var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
            Branch: w.Branch,
            BaseBranch: baseBranch,
            Title: prTitle,
            Body: prBody,
            Repo: repo,
            WorkItemId: spawn.StoryId,
            Workstream: w.Id,
            Epic: spawn.StoryId,
            AutoMerge: AutoMergeOf(spawn)), ct);

        var prsList = ParseStringList(freshRow.PrsJson);
        if (!prsList.Contains(prId)) prsList.Add(prId);
        freshRow.PrsJson = JsonSerializer.Serialize(prsList);
        freshRow.Status = "review";
        await db2.SaveChangesAsync(ct);

        await EmitChange(hub, "ws-pr-opened", spawn, ct);
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

        // Two-pass approach: process merge-ready workstreams FIRST
        // (no active comments → approve & merge), then run review-fix
        // loops (agent invocations that block for minutes). Without
        // this split, a workstream stuck in review-fix starves every
        // merge-ready sibling behind it in the sequential foreach.
        // Observed 2026-05-15 on qq:TELEGRAM-MESSENGER-S — ws 2.3
        // (SCR 95, PR ready to merge) sat for 8h+ because ws 2.4's
        // review-fix loop blocked the foreach on every tick.
        var fixNeeded = new List<(WorkstreamRow ws, string prId, List<PrCommentRow> activeComments)>();

        foreach (var w in workstreams)
        {
            // 2026-05-13 — include "complete" so late-arriving review comments
            // on already-merged PRs still get addressed by the fix loop. Without
            // this, any comment posted after the upstream merge would be
            // stranded (PrSyncJob imports it with State="active" but the
            // workstream is out of DriveReviewAsync's scan window).
            if (w.Status != "review" && w.Status != "merging" && w.Status != "complete") continue;

            var prs = ParseStringList(w.PrsJson);
            if (prs.Count == 0) continue;
            var prId = prs[0];

            // 2026-05-13 -- Phase 57b -- stamp the PR's Review verdict as
            // "pending" the first time DriveReviewAsync sees an unreviewed
            // workstream PR. AutoMergeWatcher (15 s tick) used to race the
            // orchestrator's slower reviewer dispatch and merge PRs whose
            // Review verdict was still null -- leaving the reviewer agent's
            // later-posted comments stranded on a closed PR. AutoMergeWatcher
            // already skips PRs with verdict "pending" / "changes"; flipping
            // the verdict to "pending" the moment we enter the review-scan
            // window closes the race even without the workstream-PR check
            // in AutoMergeWatcher (which is the primary fix).
            //
            // Only stamp for in-flight ws.Status: "review" / "merging".
            // "complete" workstreams already merged via a non-orchestrator
            // path (human merge upstream, AutoMerge before this fix landed)
            // should NOT have their historical verdict overwritten --
            // late-arriving comments are still handled by the fix loop
            // below regardless of verdict.
            if (w.Status is "review" or "merging")
            {
                var prRowForVerdict = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
                if (prRowForVerdict is not null
                    && string.IsNullOrEmpty(prRowForVerdict.ReviewJson))
                {
                    prRowForVerdict.ReviewJson = JsonSerializer.Serialize(new { verdict = "pending" });
                    prRowForVerdict.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
                    await db.SaveChangesAsync(ct);
                }
            }

            // Load the comment set up front. The active set drives the
            // universal guard below; the full set decides whether Step 1
            // needs to run a reviewer pass.
            var allComments = await db.PrComments.Where(c => c.PrId == prId).ToListAsync(ct);
            var activeComments = allComments.Where(c => c.State == "active").ToList();

            // ── Universal active-comments guard (2026-05-13) ──────────────
            // NO workstream advances to "complete" while any reviewer
            // comment on its PR is still in state="active". This blocks
            // three previously-leaky paths in one place:
            //   1. review→merging via Step 3 ApproveAndMergeAsync
            //      (preserved by Step 3's existing flow — we never reach
            //      it when activeComments.Count > 0).
            //   2. review→complete via the external-merge fast-path
            //      (previously bypassed the comment check — observed
            //      smartpcr/qq#3, 5 active comments stranded on merge).
            //   3. merging→complete promotion (same blind spot when a
            //      reviewer posted comments while the PR row was
            //      transitioning).
            // The fix loop also runs on already-"complete" workstreams so
            // late-arriving comments after the upstream merge still get a
            // reply + resolution on the thread. ws.Status is intentionally
            // not regressed back to "review" — the UI chip would flicker
            // and the merge-history would lie.
            if (activeComments.Count > 0)
            {
                // Phase 59 — gate the fix loop on PR/worktree liveness.
                if (await this.TrySkipReviewFixOnStalePrAsync(db, spawn, w, prId, activeComments, repo, ct))
                {
                    changed = true;
                    continue;
                }

                // Defer the agent-driven review-fix to pass 2 so
                // merge-ready workstreams (no active comments) are
                // processed first without blocking on agent invocations.
                fixNeeded.Add((w, prId, activeComments));
                continue;
            }

            // No active comments — handle status-specific transitions.

            // "complete" workstreams with no active comments → nothing to do.
            if (w.Status == "complete") continue;

            // Promote "merging" workstreams to "complete" once the PR row
            // reaches the terminal merged cache state. Active-comments
            // guard above already ensures we are not closing on top of
            // unresolved comments.
            if (w.Status == "merging")
            {
                var prRowMerging = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prId, ct);
                if (prRowMerging is not null && string.Equals(prRowMerging.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    w.Status = "complete";
                    await db.SaveChangesAsync(ct);
                    changed = true;
                    this.logger?.LogInformation(
                        "Workstream {WsId} PR {PrId} merged — advanced to complete.",
                        w.Id, prId);
                    await EmitChange(hub, "ws-complete", spawn, ct);

                    // Phase 49 follow-up — workstream PR is merged into
                    // main; the per-stage worktree on disk is now stale
                    // (its commits are live on main). Free the disk.
                    // Best-effort: failures log + continue.
                    if (repo is not null && !string.IsNullOrEmpty(w.Branch))
                    {
                        await this.git.RemoveWorktreeAsync(repo, w.Branch, ct);
                    }
                }
                continue;
            }

            // w.Status == "review" — original Phase 49 flow follows.

            // External-merge fast-path: if the PR row reached the terminal
            // "merged" cache state (PrSyncJob maps upstream "completed" /
            // "abandoned" to Col=="merged" with IsActive=false), advance
            // straight to "complete". Active-comments guard above already
            // ensures we are not stranding reviewer comments — if there
            // were any, the fix loop has already claimed this tick.
            var prRowAtReview = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (prRowAtReview is not null && string.Equals(prRowAtReview.Col, "merged", StringComparison.OrdinalIgnoreCase))
            {
                w.Status = "complete";
                await db.SaveChangesAsync(ct);
                changed = true;
                this.logger?.LogInformation(
                    "Workstream {WsId} PR {PrId} reached terminal merged state — advanced from review to complete.",
                    w.Id, prId);
                await EmitChange(hub, "ws-complete", spawn, ct);

                // Phase 49 follow-up — mirror the merging→complete cleanup
                // for the external-merge fast path. Worktree is stale once
                // upstream is merged.
                if (repo is not null && !string.IsNullOrEmpty(w.Branch))
                {
                    await this.git.RemoveWorktreeAsync(repo, w.Branch, ct);
                }
                continue;
            }

            // Step 1 — first visit: no comments yet → run the reviewer to
            // post the initial round. ProduceReviewCommentsAsync emits its
            // transcript events under workstream.Id (Phase 49), so the
            // Review subtab in the Conversation pane lights up here.
            if (allComments.Count == 0)
            {
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                var comments = await this.strategy.ProduceReviewCommentsAsync(spawn, w, prId, round: 0, ct);
                if (comments.Count == 0)
                {
                    // No findings — straight to approve and merge. Phase 49
                    // follow-up — the merge can throw (GitHub 405 dirty when
                    // a sibling PR merged first, 405 unstable on required-
                    // check red, network blip, etc.). Catch per-workstream
                    // so a single bad merge stalls only THIS row instead of
                    // halting the entire spawn via TickOneSpawnAsync's
                    // outer LastError catch (observed 2026-05-11 on
                    // failover-cluster:XRAFT — PR #7 merge-conflict took
                    // down all 21 workstreams).
                    try
                    {
                        await this.ApproveAndMergeAsync(prId, w.Branch, reviewer, repo, baseBranch, ct);
                        w.Status = "merging";
                        await db.SaveChangesAsync(ct);
                        changed = true;
                        continue;
                    }
                    catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
                    {
                        await this.StallWorkstreamForMergeFailureAsync(db, hub, spawn, w, prId, mergeEx, ct);
                        changed = true;
                        continue;
                    }
                }

                foreach (var c in comments)
                {
                    await this.provider.PostPrCommentAsync(prId, c.FilePath, c.Line, c.Body, reviewer, ct);
                }

                // Re-load active comments and run the fix loop on this tick.
                allComments = await db.PrComments.Where(c => c.PrId == prId).ToListAsync(ct);
                activeComments = allComments.Where(c => c.State == "active").ToList();
                if (activeComments.Count > 0)
                {
                    await this.RunReviewFixLoopAsync(spawn, w, prId, activeComments, repo, ct);
                    changed = true;
                    continue;
                }
            }

            // Step 3 — no active comments → approve and merge. Phase 49
            // follow-up — same per-workstream catch as Step 1 so a
            // GitHub 405 (merge conflicts / required-checks red / blocked
            // by branch protection) on this PR doesn't bubble up to
            // TickOneSpawnAsync and set spawn.LastError, which the UI
            // surfaces as "ORCHESTRATOR HALTED" and freezes the
            // remaining workstreams. Marking only THIS row stalled keeps
            // the spawn's other workstreams progressing.
            var approveReviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
            try
            {
                await this.ApproveAndMergeAsync(prId, w.Branch, approveReviewer, repo, baseBranch, ct);
                w.Status = "merging";
                await db.SaveChangesAsync(ct);
                changed = true;
            }
            catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
            {
                await this.StallWorkstreamForMergeFailureAsync(db, hub, spawn, w, prId, mergeEx, ct);
                changed = true;
            }
        }

        // ── Pass 2: review-fix agent invocations (deferred) ──────────────
        // Runs AFTER all merge-ready workstreams have been processed so
        // merge candidates aren't starved by long-running agent calls.
        foreach (var (fw, fwPrId, fwActiveComments) in fixNeeded)
        {
            await this.RunReviewFixLoopAsync(spawn, fw, fwPrId, fwActiveComments, repo, ct);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Phase 59 — gate <see cref="RunReviewFixLoopAsync"/> against PRs that
    /// can no longer be fixed: either the PR row is terminal
    /// (<c>pr.Col in {merged, closed}</c>) or the workstream's per-stage
    /// worktree has been wiped from disk (Phase 49 follow-up runs
    /// <see cref="IStoryGitOps.RemoveWorktreeAsync"/> after the upstream
    /// merge). In either case <see cref="IStoryGitOps.CommitFilesAsync"/>
    /// would throw — the existing per-comment catch swallows the throw
    /// and <see cref="IStoryProviderOps.ResolvePrCommentAsync"/> is never
    /// reached, so the comment stays <c>active</c> and the next tick
    /// repeats the same failing engineer invocation, burning ~$0.10 per
    /// E9 call indefinitely (observed smartpcr/qq PRs #2 + #3 with 17
    /// comments stranded across both).
    ///
    /// <para>When the gate trips we post a system reply on every active
    /// comment and resolve it, then emit a single
    /// <c>task.review-fix-skipped</c> transcript event explaining why.
    /// Returns <c>true</c> when the caller should skip the engineer pass
    /// for this PR.</para>
    /// </summary>
    private async Task<bool> TrySkipReviewFixOnStalePrAsync(
        ForgeDbContext db, StorySpawnRow spawn, WorkstreamRow w, string prId,
        IReadOnlyList<PrCommentRow> activeComments,
        RepoRow? repo, CancellationToken ct)
    {
        if (activeComments.Count == 0) return false;

        var prRow = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prId, ct);
        var prTerminal = prRow is not null
            && (string.Equals(prRow.Col, "merged", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prRow.Col, "closed", StringComparison.OrdinalIgnoreCase));

        // Empty branch → no possible worktree to commit into, treat as
        // missing. WorktreeExistsAsync returns false fast on null repo /
        // missing CloneRoot, true on probe failure (fail-safe).
        var worktreeExists = !string.IsNullOrEmpty(w.Branch)
            && await this.git.WorktreeExistsAsync(repo, w.Branch, ct);
        var worktreeMissing = !worktreeExists;

        if (!prTerminal && !worktreeMissing) return false;

        var col = (prRow?.Col ?? "unknown").ToLowerInvariant();
        string reason;
        string replyBody;
        if (prTerminal && worktreeMissing)
        {
            reason = $"pr-{col}-and-worktree-gone";
            replyBody =
                $"System: this PR is already {col} and the workstream's worktree has been "
                + "cleaned up post-merge. No further fix is possible — auto-resolving this comment.";
        }
        else if (prTerminal)
        {
            reason = $"pr-{col}";
            replyBody =
                $"System: this PR is already {col}. No further fix is possible — "
                + "auto-resolving this comment.";
        }
        else
        {
            reason = "worktree-missing";
            replyBody =
                "System: the workstream's worktree is no longer available on disk. "
                + "No further fix is possible — auto-resolving this comment.";
        }

        const string SystemResolver = "system:post-merge-cleanup";
        var resolved = 0;
        foreach (var oc in activeComments)
        {
            try
            {
                await this.provider.ReplyPrCommentAsync(oc.Id, replyBody, SystemResolver, ct);
                await this.provider.ResolvePrCommentAsync(oc.Id, SystemResolver, ct);
                resolved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "TrySkipReviewFixOnStalePrAsync: failed to auto-resolve comment {CommentId} "
                    + "on PR {PrId} for ws {WsId}; next tick will retry.",
                    oc.Id, prId, w.Id);
            }
        }

        this.logger?.LogInformation(
            "TrySkipReviewFixOnStalePrAsync: skipped review-fix loop for ws {WsId} PR {PrId} "
            + "(reason={Reason}, prCol={Col}, worktreeMissing={WtMissing}); auto-resolved "
            + "{Resolved}/{Total} active comments.",
            w.Id, prId, reason, col, worktreeMissing, resolved, activeComments.Count);

        await this.EmitSpawnEventAsync(w.Id, "task.review-fix-skipped", new
        {
            wsId = w.Id,
            spawnId = spawn.Id,
            prId,
            reason,
            prCol = col,
            worktreeMissing,
            commentCount = activeComments.Count,
            resolvedCount = resolved,
        }, ct);
        return true;
    }

    /// <summary>
    /// 2026-05-13 — extracted from <see cref="DriveReviewAsync"/>. Runs one
    /// pass of <see cref="IStoryArtifactStrategy.ProduceReviewFixAsync"/>
    /// per active comment on a PR, with per-comment try/catch so a transient
    /// failure on comment #2 doesn't strand comments #3..N. The engineer
    /// agent returns ONE of two shapes per comment (Phase 49):
    /// <list type="bullet">
    /// <item>Code change (FilePath + NewContent non-null) → commit the
    ///   file AND post a thread reply summarising the change.</item>
    /// <item>Reply only (FilePath / NewContent null) → no commit; post the
    ///   reply explaining why no change is needed.</item>
    /// </list>
    /// ReplyBody is ALWAYS present so every reviewer comment gets an
    /// explicit response on the PR thread. <see cref="IStoryProviderOps.ResolvePrCommentAsync"/>
    /// updates the DB row state to "resolved"; the next tick re-queries and
    /// the loop exits when activeComments is empty. The helper is shared by
    /// the universal active-comments guard (handles "review" / "merging" /
    /// "complete" status) and Step 1's post-load freshly-posted-rows path.
    /// </summary>
    private async Task RunReviewFixLoopAsync(
        StorySpawnRow spawn, WorkstreamRow w, string prId,
        IReadOnlyList<PrCommentRow> activeComments,
        RepoRow? repo, CancellationToken ct)
    {
        // Phase 59 — defense in depth. DriveReviewAsync already gates
        // terminal-PR / missing-worktree cases, but any future caller
        // (admin endpoint, tests, direct workflow code) that reaches us
        // bypassing that gate gets the same protection: re-probe and
        // short-circuit with system-resolved comments. Uses a fresh
        // scope so we don't leak the orchestrator's db across the call
        // boundary.
        using (var scope = this.services.CreateScope())
        {
            var dbDefense = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            if (await this.TrySkipReviewFixOnStalePrAsync(dbDefense, spawn, w, prId, activeComments, repo, ct))
            {
                return;
            }
        }

        var engineerId = ParseStringList(w.AgentsJson).FirstOrDefault() ?? "eng-default";
        foreach (var oc in activeComments)
        {
            try
            {
                var fix = await this.strategy.ProduceReviewFixAsync(spawn, w,
                    new ReviewCommentContext(oc.FilePath ?? string.Empty, oc.Line ?? 0, oc.Body, engineerId), ct);

                // Path A — commit the code change.
                if (!fix.IsReplyOnly)
                {
                    using var slugScope = this.services.CreateScope();
                    var slugDb = slugScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                    var wsSlug = await WorkstreamWorktreeSlug.ComputeAsync(w, slugDb, ct);
                    await this.git.CommitFilesAsync(repo, w.Branch, wsSlug,
                        new Dictionary<string, string>(StringComparer.Ordinal) { [fix.FilePath!] = fix.NewContent! },
                        $"fix({w.Id}): address review comment", ct);
                }
                // Path A & B — always post the reply so the reviewer
                // (and operator) sees an explicit response.
                await this.provider.ReplyPrCommentAsync(oc.Id, fix.ReplyBody, engineerId, ct);
                // Mark the original reviewer comment resolved so it
                // drops out of the active set on the next tick.
                await this.provider.ResolvePrCommentAsync(oc.Id, engineerId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "DriveReviewAsync: failed to fix comment {CommentId} on PR {PrId} for ws {WsId}; "
                    + "will retry next tick. surviving comments will still be processed in this loop.",
                    oc.Id, prId, w.Id);
                // Don't break — continue with the next comment so a
                // failure on one doesn't strand the rest.
            }
        }
    }

    /// <summary>
    /// Phase 49 follow-up — route an exception from
    /// <see cref="ApproveAndMergeAsync"/> to a workstream-scoped
    /// <c>stalled</c> transition with a precise
    /// <see cref="WorkstreamRow.StallReason"/>, instead of letting it
    /// escape <see cref="DriveReviewAsync"/> and trip the outer
    /// <see cref="TickOneSpawnAsync"/> catch (which sets
    /// <see cref="StorySpawnRow.LastError"/> and surfaces as
    /// "ORCHESTRATOR HALTED" in the UI, freezing every workstream).
    ///
    /// <para>Triggered by: GitHub 405 <c>Pull Request has merge conflicts</c>
    /// (mergeable_state=dirty when a sibling PR has merged into the base
    /// since this branch was rebased on claim); 405 <c>required status
    /// check failed</c> (mergeable_state=unstable / blocked); transient
    /// 5xx from the provider; <c>RealStoryGitOps.MergeAsync</c> failures
    /// when the post-upstream-merge fast-forward into the local mirror
    /// hits a stale ref. All look identical from this layer's POV —
    /// the operator gets the concrete provider message in
    /// <c>StallReason</c> and uses Restart (after a manual rebase if
    /// needed) to retry.</para>
    /// </summary>
    private async Task StallWorkstreamForMergeFailureAsync(
        ForgeDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkstreamRow w, string prId, Exception ex, CancellationToken ct)
    {
        this.logger?.LogWarning(ex,
            "DriveReviewAsync: ApproveAndMergeAsync failed for ws {WsId} PR {PrId}. "
            + "Marking stalled; spawn continues with remaining workstreams.",
            w.Id, prId);

        // Don't overwrite a terminal/operator-set state. Same guard the
        // BuildFailedException path in DriveImplementAsync uses.
        if (w.Status is not "cancelled" and not "paused" and not "complete")
        {
            w.Status = WorkstreamStatuses.Stalled;
            w.StallReason =
                $"Merge failed for PR {prId}: {FlattenMessage(ex)}. "
                + "Resolve the conflict (rebase the ws branch onto the current base tip "
                + "and push, or fix the failing required-status check), then Restart.";
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                // Don't let a save failure mask the original merge failure.
                this.logger?.LogWarning(saveEx,
                    "DriveReviewAsync: failed to persist stalled status for ws {WsId} after merge failure; "
                    + "next tick will re-enter and retry the transition.", w.Id);
            }
            await EmitChange(hub, "ws-stalled", spawn, ct);
        }

        await this.EmitSpawnEventAsync(w.Id, "task.merge-failed", new
        {
            wsId = w.Id,
            prId,
            spawnId = spawn.Id,
            message = FlattenMessage(ex),
            exceptionType = ex.GetType().Name,
        }, ct);
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
            await this.git.CommitFilesAsync(repo, qaBranch,
                PlanDocStateHelpers.CanonicalQaWorktreeSlug(spawn.StoryId),
                files,
                $"qa: feature files for story {GitSafe(spawn.StoryId)}", ct);

            // Phase 46 — merge latest from main into the QA branch
            // before creating the PR. Always enforced.
            await this.git.MergeUpstreamIntoBranchAsync(repo, qaBranch, "main", ct: ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: qaBranch,
                BaseBranch: baseBranch,
                Title: $"[qa] {story.Title}",
                Body: $"QA feature files for story `{GitSafe(spawn.StoryId)}`.",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: null,
                Epic: spawn.StoryId,
                AutoMerge: AutoMergeOf(spawn)), ct);

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
            await this.git.CommitFilesAsync(repo, stepBranch,
                PlanDocStateHelpers.CanonicalQaStepsWorktreeSlug(w.Id),
                files,
                $"qa-steps({w.Id}): step definitions", ct);

            // Phase 46 — merge latest from main before creating the PR.
            await this.git.MergeUpstreamIntoBranchAsync(repo, stepBranch, "main", ct: ct);

            var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
                Branch: stepBranch,
                BaseBranch: baseBranch,
                Title: $"[qa-steps] {w.Name}",
                Body: $"Step definitions for `{w.Id}` (story `{GitSafe(spawn.StoryId)}`).",
                Repo: repo,
                WorkItemId: spawn.StoryId,
                Workstream: w.Id,
                Epic: spawn.StoryId,
                AutoMerge: AutoMergeOf(spawn)), ct);

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

            // Phase 49 follow-up — spawn reached Done; QA branch is
            // merged into main and we won't touch its worktree again.
            // Free disk by removing the QA worktree. Engineer worktrees
            // (one per workstream) were already cleaned up at each
            // ws.Status="complete" transition above and in DriveReviewAsync;
            // this catches the QA worktree alone.
            if (repo is not null && !string.IsNullOrEmpty(spawnRow.QaBranch))
            {
                await this.git.RemoveWorktreeAsync(repo, spawnRow.QaBranch!, ct);
            }

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
    /// <summary>
    /// Phase 49 follow-up — pick a (generator, evaluator) pair for an
    /// engineer-workstream claim with three guarantees:
    /// <list type="number">
    /// <item><b>Diversity</b> — pulls the engineer crew's roster, runs
    ///   <see cref="PairDiversityResolver.PickGenerator"/> /
    ///   <see cref="PairDiversityResolver.PickEvaluator"/> against the
    ///   live busy set so the picked agents are not already on another
    ///   in-flight invocation.</item>
    /// <item><b>Per-tick uniqueness</b> — mutates <paramref name="busyAgents"/>
    ///   after picking each agent so the very next claim in the same
    ///   tick sees them as busy and skips them. Without this, multiple
    ///   workstreams claimed in one tick all picked the crew's lead and
    ///   default evaluator (the bug operator hit on
    ///   <c>1.2 → 1.3 → 1.4</c> sequential claims).</item>
    /// <item><b>Round-robin spread</b> — seeds the picker with
    ///   <c>TickCount + workstream.Sort</c> so adjacent claims rotate
    ///   through the roster even when they fall in the same millisecond
    ///   tick.</item>
    /// </list>
    /// Falls back to the legacy <c>(crew.DefaultGenerator,
    /// crew.DefaultEvaluator)</c> chain when the crew has no
    /// <c>crew_members</c> rows or every member is busy — the row still
    /// claims and the ledger contention sorts itself out as runs finish.
    /// </summary>
    internal static async Task<string[]> ResolveImplPairAsync(
        ForgeDbContext db,
        AgentAllocationLoader allocation,
        HashSet<string> busyAgents,
        int seedOffset,
        CancellationToken ct)
    {
        const string prefix = "crew-eng";
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct);
        if (crew is null)
        {
            var fallback = allocation.ForTeam("Engineer").FirstOrDefault()?.Id ?? "eng-default";
            return new[] { fallback };
        }

        var evaluatorCrew = !string.IsNullOrEmpty(crew.PairedWithCrewId)
            ? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct)
            : null;

        // Generator roster — engineer crew members. The schedule-gate
        // (on-shift filter) is the orchestrator's responsibility; for
        // story-spawn workstreams we treat all members as eligible
        // because the spawn was already through the operator's
        // explicit approval. Fall back to the lead when no members.
        // Ordered by AgentId for a stable round-robin baseline; the
        // per-claim seed below rotates the picked index.
        var genRoster = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == crew.Id)
            .OrderBy(m => m.AgentId)
            .Select(m => m.AgentId)
            .ToListAsync(ct);

        // Seed: Random.Shared.Next() so concurrent claims each get an
        // independent uniformly-random index — true randomization
        // rather than deterministic round-robin. Combined with the
        // mutated busyAgents set, this means two simultaneous claims
        // pick distinct agents AND repeated claims across ticks rotate
        // through the roster without operator-visible patterns.
        // The seedOffset is mixed in so deterministic test fixtures
        // (which pin Random.Shared via a thread-local seed) still see
        // workstream-position-dependent behaviour.
        var seed = unchecked(Random.Shared.Next() + seedOffset);

        var (genAgent, _) = PairDiversityResolver.PickGenerator(
            genRoster, busyAgents, crew.Lead, seed);

        // No free roster member AND no lead — last-resort fallback.
        if (string.IsNullOrEmpty(genAgent))
        {
            genAgent = crew.DefaultGeneratorAgentId
                ?? crew.Lead
                ?? allocation.ForTeam("Engineer").FirstOrDefault()?.Id
                ?? "eng-default";
        }
        busyAgents.Add(genAgent);

        // Evaluator roster — paired crew's members (or same crew minus
        // the picked generator for a standalone crew). Phase 39 gen != eval
        // is enforced inside PickEvaluator.
        var evalRosterCrewId = evaluatorCrew?.Id ?? crew.Id;
        var evalRoster = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == evalRosterCrewId)
            .OrderBy(m => m.AgentId)
            .Select(m => m.AgentId)
            .ToListAsync(ct);

        // 2026-05-12 bug fix — only the operator-configured preferred
        // evaluator gets first-dibs in PickEvaluator. The previous code
        // ALSO promoted `evaluatorCrew?.Lead` into this slot, which gave
        // the night-crew lead unconditional first-dibs and pinned every
        // engineer claim to the same evaluator (operator-reported: "the
        // evaluator side always assigns the same agent"). The Lead is
        // now reserved as a last-resort fallback ONLY when PickEvaluator
        // returns null because every roster member is busy.
        var preferredEval = crew.DefaultEvaluatorAgentId
            ?? evaluatorCrew?.DefaultEvaluatorAgentId;
        var leadFallback = evaluatorCrew?.Lead;

        var (evalAgent, _) = PairDiversityResolver.PickEvaluator(
            evalRoster, busyAgents, preferredEval, genAgent, seed: unchecked(Random.Shared.Next() + seedOffset));

        // Q47.3 — evaluator diversity is best-effort. If every candidate
        // is busy fall back so the row still claims; ledger contention
        // sorts itself out as in-flight runs finish. Preference order
        // matches the previous behaviour minus the implicit Lead-as-
        // first-dibs promotion: operator-configured default first,
        // crew Lead last.
        if (string.IsNullOrEmpty(evalAgent))
        {
            evalAgent = preferredEval ?? leadFallback;
        }

        if (string.IsNullOrEmpty(evalAgent))
        {
            return new[] { genAgent };
        }

        busyAgents.Add(evalAgent);
        return new[] { genAgent, evalAgent };
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

    /// <summary>
    /// Phase 64 — compose the workstream-level StallReason text from a
    /// terminal deliverable row. Preferred source is the structured
    /// <see cref="DeliverableRow.FailureKind"/> / <see cref="DeliverableRow.FailureReason"/>
    /// columns (populated for new stalled rows since the migration).
    /// Falls back to parsing the legacy <c>"GENERATOR FAILED (kind): reason"</c>
    /// prefix in <see cref="DeliverableRow.Notes"/> so rows written before
    /// the migration still surface a useful reason. Final fallback is the
    /// generic "Evaluator terminal verdict 'X'" text the pre-Phase-64
    /// code wrote unconditionally.
    /// </summary>
    private static string ComposeStallReason(DeliverableRow del)
    {
        var kind = del.FailureKind;
        var reason = del.FailureReason;
        if (string.IsNullOrEmpty(kind))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                del.Notes ?? string.Empty,
                @"^GENERATOR FAILED \(([^)]+)\):\s*(.+?)(?:\r?\n|$)");
            if (m.Success)
            {
                kind = m.Groups[1].Value;
                reason = m.Groups[2].Value;
            }
        }

        if (!string.IsNullOrEmpty(kind))
        {
            var advice = kind switch
            {
                "generator-max-turns" =>
                    "Raise WorkItem.Points (each point adds 30 turns) and Restart.",
                "generator-timeout" =>
                    "Raise WorkItem.Points (each point adds 2 min wall-clock) or split the stage, then Restart.",
                "generator-inactive" =>
                    "Transport silent — check whether a long tool call (e.g. dotnet build) is exceeding "
                    + "Forge:Agents:InactivityTimeout. Raise the option or Restart.",
                "generator-stuck-tool-call" =>
                    "A single tool call ran past Forge:Agents:PerTurnTimeout. Inspect the named tool "
                    + "(likely a hung build/test subprocess) before Restart.",
                "generator-cli-exit" =>
                    "Agent CLI crashed mid-stream. Check the transcript for the exit detail, then Restart.",
                "generator-provider-error" =>
                    "Provider HTTP error after retries exhausted. Wait for upstream recovery and Restart.",
                _ => "Restart to retry, or escalate to lead.",
            };
            var detail = string.IsNullOrEmpty(reason) ? string.Empty : $": {reason}";
            return $"Generator failed ({kind}) at iter {del.Iter}{detail}. {advice}";
        }

        return $"Evaluator terminal verdict '{del.Verdict}' at iter {del.Iter} (score {del.Score}). "
            + "Restart to retry, or escalate to lead.";
    }
}
