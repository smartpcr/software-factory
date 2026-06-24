// -----------------------------------------------------------------------
// <copyright file="StorySpawnOrchestrator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Concurrent;
using System.Globalization;
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

    // 2026-05-29 — lowered 15→5. The implement-phase drive holds this gate for
    // the full impl wall-clock because DriveImplementAsync awaits Task.WhenAll
    // over every in-wave workstream's iterate-to-90 loop. While the gate is held,
    // every subsequent tick is skipped ("already in flight"), so the per-spawn
    // DriveReviewAsync (which runs first each turn, see RunOneStepAsync) only
    // gets a fresh turn when the gate force-reenters here. At 15m, e2e/impl
    // workstreams that PASSED and moved to Status="review" were reviewed only
    // ONE-per-15-min-cycle and queued up behind the held gate (observed
    // 2026-05-29: code-intelligence AST-PARSER go/c-cpp/rust e2e each waited a
    // full 15m cycle for its reviewer). Force-reentry is the existing, safe
    // recovery path (the orphaned drive's impl tasks keep running in the
    // background; its eventual Release lands on the discarded gate, harmless),
    // so reentering at 5m simply lets review drain ~3× faster. Non-implement
    // drives finish in <60s and never reach this threshold, so they're
    // unaffected. (The complete cure is detaching the impl wave so the gate is
    // never held for the budget — deferred: it breaks the test seam that relies
    // on DriveImplementAsync awaiting WhenAll for deterministic single-tick
    // advancement.)
    private const int DriveStuckMinutes = 5;

    private static readonly ConcurrentDictionary<string, DriveGate> driveLocks =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Plan-doc in-flight guard — prevents concurrent gen-eval loops for
    /// the same plan doc. The spawn-level <see cref="DriveGate"/> has a
    /// <see cref="DriveStuckMinutes"/>-minute stuck timeout that
    /// force-releases when a long-running gen-eval loop exceeds the
    /// threshold, letting a second tick re-enter
    /// <see cref="RunPlanDocAsync"/> while the first is still in flight.
    /// Two concurrent loops create duplicate deliverable rows at the
    /// same iter, and a phantom score-0 row after a pass permanently
    /// blocks the plan chain gate.
    /// Observed 2026-05-15 on local:HIGH-MEM-CONSUMPTION architecture.md:
    /// 10-iter loop took 31 min total; at 15 min the stuck detection
    /// fired, a second loop started, and the phantom iter-7 row blocked
    /// tech-spec indefinitely.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> planDocInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 2026-05-26 — per-comment consecutive-timeout counter for
    /// <see cref="RunReviewFixLoopAsync"/>. Keyed by
    /// <c>(workstream id, pr_comment row id)</c>; incremented each time
    /// <see cref="IStoryArtifactStrategy.ProduceReviewFixAsync"/> throws with
    /// a "hard timeout" / "exceeded budget" message. Once it hits
    /// <see cref="MaxReviewFixTimeouts"/> the loop auto-resolves the comment
    /// with a system reply + transcript event so the operator can step in
    /// without paying for additional ~$30 retries (observed 2026-05-26 on
    /// ws stage-1.2-clusterdbstore: 4× back-to-back 8m timeouts before
    /// operator restart).
    /// Resets on a successful fix; cleared on circuit-break to keep memory
    /// bounded. In-memory only — orchestrator restart re-arms a fresh attempt,
    /// which is the intended behaviour (operator restart == fresh attempt).
    /// </summary>
    private readonly ConcurrentDictionary<(string WsId, long CommentId), int> reviewFixTimeoutCounter =
        new();

    /// <summary>
    /// Number of consecutive review-fix budget exhaustions on the SAME
    /// comment before the orchestrator auto-resolves it. Set deliberately
    /// low (3) because each attempt burns the full per-phase wall-clock
    /// budget (~8m for an 8-pt story) with no metered usage row, so the
    /// blast radius scales linearly with the cap.
    /// </summary>
    private const int MaxReviewFixTimeouts = 3;

    /// <summary>
    /// Resolver-id stamped on <see cref="PrCommentRow.ResolvedByUserId"/>
    /// when the circuit breaker auto-resolves a comment. Distinguishes from
    /// <c>"system:post-merge-cleanup"</c> (Phase 59 stale-PR cleanup) so
    /// telemetry and tests can tell the two paths apart.
    /// </summary>
    private const string ReviewFixBudgetResolverId = "system:review-fix-budget";

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
            // Phase 73 — push the product slug onto the AsyncLocal
            // stack BEFORE opening any DI scope so every nested
            // CreateScope() (here, and inside every helper that runs
            // within this try-block, including DrivePlanAsync,
            // DriveImplementAsync, DriveReviewAsync, DriveQaAsync, and
            // their per-WS inner scopes) resolves ProductDbContext
            // against the correct per-product DB file rather than
            // falling through to CreateUnscoped → control DB. The
            // slug is derived from the story-id convention
            // `{slug}:{STORY_KEY}`. AsyncLocal<T> flows through
            // IServiceScopeFactory.CreateScope and through async
            // continuations so a single push at this entry point
            // covers the entire orchestrator turn. When the slug is
            // null (legacy story-id without a colon) the push is a
            // no-op and the unscoped fallback applies as before —
            // identical to pre-Phase-73 behaviour.
            using var slugScope = ProductDbContextFactory.PushAsyncLocalSlug(
                ProductSlugExtractor.FromStoryId(storyId));

            using var scope = this.services.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
            var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
            if (spawn is null) return;
            await this.RunOneStepAsync(control, db, hub, spawn, forceResume, ct);
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub, StorySpawnRow spawn, bool forceResume, CancellationToken ct)
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
                    advanced |= await this.DrivePlanAsync(control, db, hub, spawn, story, repo, forceResume, ct);
                    break;
                case StorySpawnPhases.Decompose:
                    advanced |= await this.DriveDecomposeAsync(control, db, hub, spawn, story, repo, ct);
                    break;
                // 2026-05-28 — review/QA drive runs BEFORE the implement drive.
                // `DriveImplementAsync` ends with `await Task.WhenAll(tasks)`, so
                // it holds the per-spawn DriveGate for the FULL impl wall-clock
                // (every workstream's iterate-to-90 loop, 10–30+ min). The
                // scheduler orphans the call at 60 s but the DriveGate stays held
                // until the 15-min DriveStuckMinutes force-reentry, and the next
                // gate cycle just re-enters the impl drive first again. On a story
                // with many parallel workstreams (e.g. AST-PARSER: 6 tree-sitter
                // parsers + per-stage e2e) the impl wave is never idle, so a
                // review/merge drive sequenced AFTER it was perpetually starved —
                // a workstream that PASSED and moved to Status="review" never got
                // its reviewer dispatched (observed: code-intelligence
                // AST-PARSER stage c-and-cpp ctreesitterparser-e2e sat in review
                // 24 min+ after scoring 92). Running review/QA first bounds review
                // latency to one gate cycle (≤ DriveStuckMinutes) instead of
                // leaving it unbounded behind the impl wave. The implement drive
                // still runs each turn (it just yields the gate window to review
                // first); test determinism is preserved because every orchestrator
                // test ticks DriveAsync in a loop until a terminal phase.
                case StorySpawnPhases.Implement:
                    advanced |= await this.DriveReviewAsync(control, db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveImplementAsync(control, db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Review:
                    advanced |= await this.DriveReviewAsync(control, db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveImplementAsync(control, db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Qa:
                    advanced |= await this.DriveReviewAsync(control, db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveQaAsync(control, db, hub, spawn, story, repo, ct);
                    advanced |= await this.DriveImplementAsync(control, db, hub, spawn, story, repo, ct);
                    break;
                case StorySpawnPhases.Done:
                    advanced |= await this.DriveDoneAsync(control, db, hub, spawn, story, repo, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Phase 78 — recognise the Phase 74 concurrent-dispatch
            // discriminator throw and treat it as a transient tick-overlap
            // event rather than a fatal phase failure. Without this guard,
            // the throw bubbled here and overwrote `spawn.LastError` with
            // a generic "implement failed: Concurrent dispatch refused..."
            // message that masked the actual workstream-level failure
            // (e.g. "Merge failed for PR #38: ... merge conflicts" on
            // `WorkstreamRow.StallReason`). The UI's "ORCHESTRATOR HALTED"
            // banner then surfaced the dispatch race instead of the real
            // merge conflict, confusing the operator.
            //
            // The race itself is a real condition: two scheduler ticks for
            // the same spawn ran simultaneously after the first tick's
            // 60 s RunWithTimeoutAsync window expired and the second tick
            // started 15 s later while the first tick's orphaned dispatch
            // was still in flight. The root-cause fix is the per-spawn
            // semaphore added to `SpawnDependencyScheduler.TickOnceAsync`
            // in the same phase; this guard remains as defence-in-depth
            // (admin endpoints, tests, future schedulers all benefit).
            //
            // Next-tick recovery: when the original dispatch terminates,
            // the dispatcher removes the workItemId from
            // `liveInvocationsByTask`, and the next tick's dispatch
            // succeeds. No operator action required.
            if (ex.Message.Contains(
                    Forge.Api.Agents.AgentRunnerDispatcher.ConcurrentDispatchRefusalSentinel,
                    StringComparison.OrdinalIgnoreCase))
            {
                this.logger?.LogWarning(ex,
                    "Orchestrator phase '{Phase}' for spawn {SpawnId} ({StoryId}) hit a "
                    + "tick-overlap concurrent-dispatch race. NOT persisting as spawn.LastError "
                    + "(would mask any workstream-level StallReason). Next tick will retry "
                    + "once the original dispatch terminates.",
                    spawn.Phase, spawn.Id, spawn.StoryId);
                return advanced;
            }
            this.logger?.LogError(ex,
                "Orchestrator phase '{Phase}' failed for spawn {SpawnId} ({StoryId}).",
                spawn.Phase, spawn.Id, spawn.StoryId);
            await PersistLastErrorAsync(control, db, spawn.Id,
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, bool forceResume, CancellationToken ct)
    {
        // Phase 53 — sequential plan-doc path. Detection is strictly by
        // column presence: sequential-mode spawns are initialised with
        // PlanDocPrsJson = "[]" at spawn-create time, legacy rows
        // (created before Phase 53 shipped) carry null. No row ever flips
        // between paths mid-life, so the discriminator is unambiguous.
        if (PlanDocStateHelpers.IsSequentialMode(spawn))
        {
            return await this.DrivePlanSequentialAsync(control, db, hub, spawn, story, repo, forceResume, ct);
        }
        return await this.DrivePlanLegacyAsync(control, db, hub, spawn, story, repo, forceResume, ct);
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, bool forceResume, CancellationToken ct)
    {
        var baseBranch = BaseBranchOf(spawn);
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);

        // Same plan-phase invariant as the legacy path: no workstream may
        // be active while Phase=Plan. Cancels stale picks from a regressed
        // implement→plan transition.
        await this.EnforcePlanPhaseInvariantAsync(control, db, spawn, ct);

        // Ensure backing WorkItemRows for each plan doc exist (deliverables /
        // transcripts / queue rows all key on plan-doc:{spawn.Id}:{file}).
        await EnsurePlanDocWorkItemsAsync(control, db, spawn, story, ct);

        var states = PlanDocStateHelpers.GetPlanDocStates(spawn).ToList();
        var docOrder = PlanDocStateHelpers.PlanDocOrder;
        for (var i = 0; i < docOrder.Count; i++)
        {
            var docKey = docOrder[i];
            var entry = states.FirstOrDefault(e =>
                string.Equals(e.Key, docKey, StringComparison.OrdinalIgnoreCase));
            var prevMerged = i == 0 || await IsDocCompleteAsync(control, db, spawn, docOrder[i - 1], ct);

            if (entry is null || entry.Branch is null)
            {
                // Doc N has no branch yet. Wait for doc N-1 to be complete
                // (PR merged AND deliverable verdict=pass).
                if (!prevMerged)
                {
                    // Surface a soft error so the operator sees why the chain isn't moving.
                    if (i > 0)
                    {
                        await PersistLastErrorAsync(control, db, spawn.Id,
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
                await ClearLastErrorAsync(control, db, spawnRow, ct);
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
                    return await this.RunPlanDocAsync(control, db, hub, spawn, story, repo, baseBranch,
                        docKey, entry.Branch!, forceResume, ct);
                }
                catch (PlanDocInFlightException ifEx)
                {
                    // 2026-05-20 — another in-flight drive owns this doc
                    // (operator Restart racing the SpawnDependencyScheduler
                    // tick → Phase 74 skipped-concurrent). Not a failure: the
                    // winning run produces the artifact. Return false so the
                    // next tick retries, and deliberately do NOT
                    // PersistLastError — surfacing this transient race as
                    // "ORCHESTRATOR HALTED" is exactly the regression we're
                    // fixing.
                    this.logger?.LogInformation(
                        "Plan doc '{Doc}' skipped (in-flight) for spawn {SpawnId}: {Msg}. Retrying next tick.",
                        docKey, spawn.Id, ifEx.Message);
                    return false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogError(ex,
                        "Plan doc '{Doc}' production failed for spawn {SpawnId} ({StoryId}).",
                        docKey, spawn.Id, spawn.StoryId);
                    await PersistLastErrorAsync(control, db, spawn.Id,
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
                await PersistLastErrorAsync(control, db, spawn.Id,
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
                if (!await IsDocVerdictPassAsync(control, db, spawn, docKey, ct))
                {
                    await PersistLastErrorAsync(control, db, spawn.Id,
                        $"plan doc '{docKey}' verdict not 'pass'; PR merge blocked until gen-eval loop reaches pass.",
                        hub, ct);
                    return false;
                }
                var reviewer = this.allocation.ForTeam("Review").FirstOrDefault()?.Id ?? "rev-default";
                try
                {
                    await this.ApproveAndMergeAsync(control, db, entry.PrId!, entry.Branch!, reviewer, repo, baseBranch, ct);
                    var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                    await ClearLastErrorAsync(control, db, sRow, ct);
                    await db.SaveChangesAsync(ct);
                    await EmitChange(hub, "plan-doc-pr-merged", sRow, ct);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex,
                        "Plan doc '{Doc}' PR {PrId} auto-merge failed; surfacing to operator.",
                        docKey, entry.PrId);
                    await PersistLastErrorAsync(control, db, spawn.Id,
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
        var gate = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
        if (!gate.AllPassed)
        {
            await PersistLastErrorAsync(control, db, spawn.Id,
                $"Plan docs not all passed; phase advance blocked: {string.Join(", ", gate.NonPassedKeys)}",
                hub, ct);
            return false;
        }

        var spawnAdvance = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnAdvance.Phase = StorySpawnPhases.Decompose;
        spawnAdvance.UpdatedAt = DateTimeOffset.UtcNow;
        await ClearLastErrorAsync(control, db, spawnAdvance, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "phase-advanced", spawnAdvance, ct);
        return true;
    }

    /// <summary>
    /// Phase 66 — sentinel prefix for <see cref="StorySpawnRow.LastError"/>
    /// when the spawn is parked awaiting operator answers (not a hard
    /// failure). The UI matches on this prefix to render a blue
    /// "awaiting answers" banner instead of the red orchestrator-halted
    /// banner. Keep in sync with
    /// <c>src/forge/Forge.UI/src/swarm/SpawnErrorBanner.tsx</c>.
    /// </summary>
    internal const string AwaitingAnswersErrorPrefix = "Awaiting operator answers";

    /// <summary>
    /// Phase 53 — one drive of a single plan doc: ensure branch + worktree
    /// at the chosen base, run the gen↔eval loop via the strategy, commit
    /// the result, merge upstream base in, and open (or recover) the per-doc
    /// PR. Persists state writes between each step so a crash leaves an
    /// unambiguous recovery anchor in <c>PlanDocPrsJson</c>.
    /// </summary>
    private async Task<bool> RunPlanDocAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, string baseBranch,
        string docKey, string docBranch, bool forceResume, CancellationToken ct)
    {
        // First-drive of THIS doc on a fresh spawn: orphan-heal the branch.
        // The hasAnyPlanDeliverable check mirrors the legacy DrivePlanAsync
        // (line 392-402) but scoped to this doc only.
        var docWorkItemId = $"plan-doc:{spawn.Id}:{PlanDocStateHelpers.PlanDocFileName[docKey]}";

        // Plan-doc concurrency guard — skip if another tick is already
        // driving this doc's gen-eval loop. The spawn-level DriveGate
        // force-releases after DriveStuckMinutes, which is shorter than
        // a multi-iter plan-doc loop on a high-point story.
        if (!planDocInFlight.TryAdd(docWorkItemId, DateTimeOffset.UtcNow))
        {
            var started = planDocInFlight.GetValueOrDefault(docWorkItemId);
            this.logger?.LogInformation(
                "RunPlanDocAsync: plan-doc {DocKey} ({DocId}) already in-flight since {Started}; skipping.",
                docKey, docWorkItemId, started);
            return false;
        }

        try
        {
            return await this.RunPlanDocInnerAsync(control, db, hub, spawn, story, repo, baseBranch,
                docKey, docBranch, docWorkItemId, forceResume, ct);
        }
        finally
        {
            planDocInFlight.TryRemove(docWorkItemId, out _);
        }
    }

    private async Task<bool> RunPlanDocInnerAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, string baseBranch,
        string docKey, string docBranch, string docWorkItemId, bool forceResume, CancellationToken ct)
    {

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
        // Story-scoped slug so two stories in the same repo don't collide.
        var worktreePath = await this.git.EnsureWorktreeAsync(
            repo, docBranch, baseBranch, ct,
            worktreeSlug: PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, docKey));

        // Scrub already-tracked .forge/ from the index before the agent runs.
        await this.git.ScrubForgeScratchFromIndexAsync(worktreePath, ct);

        // Phase 81/83/85 preflight — when the repo's .gitignore would exclude
        // the plan-doc destination on this branch (e.g. OneBranch '/*' +
        // '!/src/' allowlists routing the default 'docs/stories/...' path
        // into the ignored set), append a narrow allowlist exception as a
        // prep commit on this branch BEFORE any LLM cost is incurred. Falls
        // through to throw GitIgnoredPathException only when the root .gitignore
        // wasn't the blocker (nested .gitignore, unsafe baseDir, etc.).
        var planDocFileName = PlanDocStateHelpers.PlanDocFileName[docKey];
        var planDocBaseDir = StoryPaths.NormalizePlanDocBaseDir(repo?.PlanDocBaseDir);
        var planDocRelPath =
            $"{StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId)}/{planDocFileName}";
        await this.git.EnsurePlanDocPathsCommittableAsync(
            repo, docBranch, new[] { planDocRelPath }, planDocBaseDir, ct);

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
            PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, docKey),
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

        await ClearLastErrorAsync(control, db, spawnRow, ct);
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, string docKey, CancellationToken ct)
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, string docKey, CancellationToken ct)
    {
        var entry = PlanDocStateHelpers.GetEntry(spawn, docKey);
        if (entry.PrId is null) return false;
        var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId, ct);
        if (pr is null || !string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase)) return false;
        return await IsDocVerdictPassAsync(control, db, spawn, docKey, ct);
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
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
        await this.EnforcePlanPhaseInvariantAsync(control, db, spawn, ct);

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
                var (earlyAllPassed, earlyNonPassed) = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
                if (!earlyAllPassed)
                {
                    await PersistLastErrorAsync(control, db, spawn.Id,
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
                    await ClearLastErrorAsync(control, db, earlyRow, ct);
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
        var (planAllPassed, planNonPassedKeys) = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
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

                await EnsurePlanDocWorkItemsAsync(control, db, spawn, story, ct);

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
                    await ClearLastErrorAsync(control, db, spawnRow, ct);
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
                    await ClearLastErrorAsync(control, db, spawnRow, ct);
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
                await PersistLastErrorAsync(control, db, spawn.Id, $"plan artifacts failed: {FlattenMessage(ex)}", hub, ct);
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
                await this.ApproveAndMergeAsync(control, db, spawn.PlanPrId, planBranch, reviewer, repo, baseBranch, ct);
                var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
                await ClearLastErrorAsync(control, db, spawnRow, ct);
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
                await PersistLastErrorAsync(control, db, spawn.Id, $"plan PR merge failed: {FlattenMessage(ex)}", hub, ct);
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
        var (advanceAllPassed, advanceNonPassedKeys) = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
        if (!advanceAllPassed)
        {
            await PersistLastErrorAsync(control, db, spawn.Id,
                $"Plan docs not all passed; phase advance blocked: {string.Join(", ", advanceNonPassedKeys)}",
                hub, ct);
            return false;
        }

        var sRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        sRow.Phase = StorySpawnPhases.Decompose;
        sRow.UpdatedAt = DateTimeOffset.UtcNow;
        await ClearLastErrorAsync(control, db, sRow, ct);
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
        ControlDbContext control, ProductDbContext db, string spawnId, string message, IHubContext<ForgeHub>? hub, CancellationToken ct)
    {
        var row = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (row is null) return;
        row.LastError = message;
        row.LastErrorAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "spawn-error", row, ct);
    }

    private static Task ClearLastErrorAsync(ControlDbContext control, ProductDbContext db, StorySpawnRow row, CancellationToken ct)
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
        ArePlanDocsAllPassedAsync(ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, CancellationToken ct)
    {
        var docIds = PlanDocFileNames.Select(f => $"plan-doc:{spawn.Id}:{f}").ToList();
        // Materialize the full deliverable history for these docs in one
        // query, then pick the latest per doc client-side. SQLite EF can't
        // GROUP BY + ORDER BY DateTimeOffset without translation drift.
        var rows = await db.Deliverables.AsNoTracking()
            .Where(d => docIds.Contains(d.WorkItemId))
            .ToListAsync(ct);

        // Phase 68 — pre-load the per-doc PR rows for the "merged + ever-passed"
        // gate-relaxation branch. A merged PR means the doc actually shipped;
        // a stale post-merge phantom iter (e.g. resume-after-merge) shouldn't
        // re-block phase advance just because it left an iterate/iter-cap row
        // at the top of the deliverable history. Observed 2026-05-15 on
        // code-intelligence:AGENT-MEMORY e2e-scenarios.md: PR #4 merged at
        // iter 1 score=92 (verdict=pass), then a Resume fired post-merge and
        // produced an iter-2 score=0 row that became "latest", blocking
        // Phase 48 indefinitely until ACCEPT was clicked.
        var prIdsByKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (PlanDocStateHelpers.IsSequentialMode(spawn))
        {
            foreach (var fileName in PlanDocFileNames)
            {
                var key = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^3]
                    : fileName;
                var entry = PlanDocStateHelpers.GetEntry(spawn, key);
                prIdsByKey[key] = entry.PrId;
            }
        }
        var prIds = prIdsByKey.Values.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        var mergedPrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (prIds.Count > 0)
        {
            var prs = await db.Prs.AsNoTracking()
                .Where(p => prIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Col })
                .ToListAsync(ct);
            foreach (var p in prs)
            {
                if (string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    mergedPrIds.Add(p.Id);
                }
            }
        }

        var nonPassed = new List<string>();
        foreach (var fileName in PlanDocFileNames)
        {
            var docId = $"plan-doc:{spawn.Id}:{fileName}";
            var docRows = rows.Where(d => d.WorkItemId == docId).ToList();
            var latest = docRows
                .OrderByDescending(d => d.Iter)
                .ThenByDescending(d => d.CreatedAt)
                .FirstOrDefault();

            // The artifact "key" used by Resume / ACCEPT / Restart endpoints
            // is the filename without the .md suffix (matches BuildPlanDocSpecs).
            var key = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

            // (a) Existing path — latest deliverable verdict == pass (covers
            // natural pass + Phase 43 ACCEPT-override which flips latest
            // verdict to pass).
            if (latest is not null
                && string.Equals(latest.Verdict, "pass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // (a2) Ever-passed fallback — when a later phantom row
            // (e.g. a generator-inactive failure on a resume attempt
            // after a pass, or a post-pass gate retry that created a
            // second deliverable at the same iter) left a non-pass
            // verdict as "latest", the doc is still satisfied if ANY
            // prior deliverable scored pass. Without this, a transient
            // failure after a pass permanently blocks the plan chain.
            // Observed 2026-05-15 on local:HIGH-MEM-CONSUMPTION
            // architecture.md: iter 7 scored 91/pass, then a second
            // iter 7 row (generator-inactive, score=0) became latest
            // and blocked tech-spec indefinitely.
            if (docRows.Any(d => string.Equals(d.Verdict, "pass", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // (b) Phase 68 — merged + ever-passed. Any prior pass deliverable
            // for a doc whose PR has merged counts as satisfied. The merged
            // PR is the ship signal; deliverable history is the quality
            // signal; both together = the doc actually went out with a
            // passing iter. A phantom post-merge iter at iterate/sloppy
            // shouldn't undo that.
            if (prIdsByKey.TryGetValue(key, out var prId)
                && !string.IsNullOrEmpty(prId)
                && mergedPrIds.Contains(prId!)
                && docRows.Any(d => string.Equals(d.Verdict, "pass", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            nonPassed.Add(key);
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, WorkItemRow story, CancellationToken ct)
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, CancellationToken ct)
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, CancellationToken ct)
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
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
        var (decomposeAllPassed, decomposeNonPassed) = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
        if (!decomposeAllPassed)
        {
            // Phase 48 follow-up — cancel any in-flight impl tasks BEFORE
            // saving the phase regression so the cancel writes batch in
            // the same transaction window. Reduces SQLite lock contention.
            await CancelInFlightImplTasksAsync(control, db, spawn, ct);

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

            await this.PersistDecompositionAsync(control, db, spawn, ws, ct);
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
        await SyncWorkstreamDepsFromJsonAsync(control, db, spawn, ct);

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
    /// id via the same-spawn name→id map, and rebuilds the FK edge set
    /// AUTHORITATIVELY — adds missing edges AND removes edges the row's
    /// DepsJson no longer names. This is what propagates a DepsJson change
    /// (e.g. the Forge:Stories:GateImplOnPriorStageE2e rewrite of an impl
    /// dep from <c>D</c> → <c>D — E2E</c>) onto the edge table that the DAG
    /// + claim sweep read. Idempotent — a stable DepsJson produces no
    /// writes; titles that don't resolve to a sibling are skipped; a row
    /// with null/unparseable <c>DepsJson</c> is left untouched (a transient
    /// empty value can't wipe real edges).
    /// </summary>
    internal static async Task SyncWorkstreamDepsFromJsonAsync(
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, CancellationToken ct)
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

        // Desired edge set per workstream, resolved from its DepsJson titles.
        // Only workstreams with a parseable DepsJson get an authoritative
        // rebuild; a row with null / unparseable DepsJson is left untouched so
        // a transient empty value can't wipe real edges. An empty list ("[]")
        // is authoritative ("no deps") and DOES drop the row's stale edges.
        var desiredByFrom = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var w in workstreams)
        {
            if (string.IsNullOrEmpty(w.DepsJson)) continue;
            List<string>? titles;
            try { titles = JsonSerializer.Deserialize<List<string>>(w.DepsJson); }
            catch { continue; }
            if (titles is null) continue;

            var desired = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in titles)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!nameToId.TryGetValue(t.Trim(), out var depId)) continue;
                if (string.Equals(depId, w.Id, StringComparison.Ordinal)) continue;
                desired.Add(depId);
            }
            desiredByFrom[w.Id] = desired;
        }

        var changed = false;

        // Authoritative rewrite: drop edges no longer named by the row's
        // DepsJson. Scoped to rows that HAVE a desired set (parseable DepsJson)
        // so rows we didn't recompute keep their edges.
        foreach (var e in existing)
        {
            if (!desiredByFrom.TryGetValue(e.WorkstreamId, out var desired)) continue;
            if (!desired.Contains(e.DependsOnId))
            {
                db.WorkstreamDeps.Remove(e);
                changed = true;
            }
        }

        // Add edges the row now declares but the table is missing.
        foreach (var (fromId, desired) in desiredByFrom)
        {
            foreach (var depId in desired)
            {
                if (existingSet.Contains((fromId, depId))) continue;
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = fromId,
                    DependsOnId = depId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Original persist path extracted so the auto-advance branch above
    /// doesn't re-do the workstream + dep insertion every tick.
    /// </summary>
    private async Task PersistDecompositionAsync(
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn,
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
                // docs/forge/e2e.md Stage 1.2 — carry the decomposer's
                // Category through to the row. Falls back to the WorkstreamRow
                // default ('impl') when the strategy didn't set it (legacy
                // strategy bindings written before Stage 1.2).
                Category = WorkstreamCategories.IsValid(w.Category) ? w.Category : WorkstreamCategories.Impl,
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
                    DescriptionMd = ComposeWorkItemDescriptionMd(w),
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

    /// <summary>
    /// docs/forge/e2e.md Stage 4.3 — compose a decomposed workstream's backing
    /// <c>WorkItemRow.DescriptionMd</c>. For qa-e2e rows the producer
    /// (<c>CrewStoryArtifactStrategy.ReadQaE2eTestScenariosAsync</c>) recovers
    /// the acceptance scenarios from a <c>## Test Scenarios</c> section, so the
    /// scenarios held in <see cref="DecompositionWorkstream.AcceptanceCriteria"/>
    /// must be folded in here. The decomposer's summary <c>Description</c> alone
    /// carries no such section, which would make the producer read zero
    /// scenarios and stall the workstream with "zero acceptance test scenarios".
    /// Heading text mirrors <c>PlanReconciler.QaE2eStageDescription</c> so both
    /// write paths converge on one parseable shape.
    /// </summary>
    internal static string ComposeWorkItemDescriptionMd(DecompositionWorkstream w)
    {
        var summary = w.Description ?? string.Empty;
        if (!string.Equals(w.Category, WorkstreamCategories.QaE2e, StringComparison.Ordinal)
            || w.AcceptanceCriteria is not { Count: > 0 })
        {
            return summary;
        }

        var sb = new System.Text.StringBuilder(summary);
        if (summary.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
        }

        sb.AppendLine("## Test Scenarios (from implementation-plan.md)");
        foreach (var sc in w.AcceptanceCriteria)
        {
            sb.Append("- ").AppendLine(sc);
        }

        return sb.ToString();
    }

    // ── implement ────────────────────────────────────────────────────────────
    private async Task<bool> DriveImplementAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Phase 48 — gate the impl claim sweep on plan-doc completion.
        // The user's regression on failover-cluster:XRAFT showed 24
        // workstreams active while all 4 plan-docs were still iterating;
        // this is the explicit "impl never starts before docs complete"
        // requirement. Q48.3: in-flight workstreams (state=picked-up)
        // continue running — only NEW claims are blocked here.
        // Phase 48 follow-up — single SaveChangesAsync (matches DriveDecomposeAsync).
        var (implAllPassed, implNonPassed) = await ArePlanDocsAllPassedAsync(control, db, spawn, ct);
        if (!implAllPassed)
        {
            // Phase 48 follow-up — operator request 2026-05-09: stop all
            // in-flight impl tasks when phase regresses. Without this the
            // 24 active workstreams keep burning tokens against a plan
            // that's been invalidated.
            await CancelInFlightImplTasksAsync(control, db, spawn, ct);

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

        // Keep the WorkstreamDep FK table in lockstep with each workstream's
        // (gated) DepsJson on every implement tick — not just at decompose.
        // The PlanReconciler re-derives DepsJson with the operator's
        // e2e-gate applied (Forge:Stories:GateImplOnPriorStageE2e); rebuilding
        // the edge table authoritatively here propagates that to a spawn whose
        // edges were persisted before the gate, and keeps any spawn consistent
        // when the plan's deps change mid-flight.
        await SyncWorkstreamDepsFromJsonAsync(control, db, spawn, ct);

        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);
        if (workstreams.Count == 0) return false;
        var workstreamIds = workstreams.Select(w => w.Id).ToList();
        var prIdsByWorkstream = (await db.Prs.AsNoTracking()
                .Where(p => p.Workstream != null && workstreamIds.Contains(p.Workstream))
                .Select(p => new { p.Workstream, p.Id })
                .ToListAsync(ct))
            .GroupBy(p => p.Workstream!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.Id).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        // 2026-05-28 — build a merged-PRs-by-branch map for the
        // already-shipped heal below. Matching by Branch (not Workstream)
        // because PrSyncJob can overwrite a Forge-tagged PR row from the
        // provider, which doesn't carry the local Workstream column —
        // observed code-intelligence:AST-PARSER-FOR-ADDIT csharptree PR
        // #152: Forge opened it with Workstream=wid, sync re-fetched and
        // cleared Workstream to null, so the prIdsByWorkstream lookup
        // missed it. Branch survives the sync round-trip because it's
        // upstream-provided.
        var wsBranches = workstreams
            .Where(w => !string.IsNullOrEmpty(w.Branch))
            .Select(w => w.Branch)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var mergedPrIdsByBranch = (await db.Prs.AsNoTracking()
                .Where(p => p.Branch != null
                         && wsBranches.Contains(p.Branch)
                         && (p.Col == "merged" || p.Col == "Merged"))
                .Select(p => new { p.Branch, p.Id })
                .ToListAsync(ct))
            .GroupBy(p => p.Branch!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.Id).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

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
            // 2026-05-28 — heal already-merged workstreams. When any of a
            // workstream's PRs reaches Col=merged but ws.Status didn't
            // transition to "complete" (sync gap, AutoMergeWatcher race,
            // restart killed the in-process DriveReviewAsync mid-merge,
            // etc.), the outer drive will happily re-enter the workstream
            // and either burn tokens on a redundant gen↔eval or — worse —
            // commit + MergeUpstreamIntoBranchAsync the branch with the
            // now-merged base, producing an empty-diff branch and opening
            // a second PR that the review crew then refuses with "branch
            // has no diff vs {base} (empty PR)" → ws stalled despite the
            // work being shipped. Observed code-intelligence:AST-PARSER-
            // FOR-ADDIT stage 1.2 csharptreesitterparser-implementation
            // 2026-05-28: PR #152 merged, ws.Status drifted back from
            // "review", second drive opened empty PR #153, ws stalled.
            //
            // Heal by detecting a Col=merged PR on this workstream's
            // branch and advancing Status="complete" + WorkItem
            // ExecutionStatus=Completed. Branch match (not Workstream)
            // because PrSyncJob clears the local Workstream column when
            // re-fetching upstream — see mergedPrIdsByBranch build site.
            if (!string.IsNullOrEmpty(w.Branch)
                && mergedPrIdsByBranch.TryGetValue(w.Branch, out var mergedPrIds)
                && mergedPrIds.Count > 0
                && w.Status is not "complete" and not "cancelled" and not "historic")
            {
                var priorStatus = w.Status;
                var priorPrsJson = w.PrsJson;

                // Restore the merged PR id to PrsJson if the empty-PR
                // second-drive overwrote it with just the empty PR's id
                // (idempotent via Contains check).
                var existing = ParseStringList(w.PrsJson);
                var anyAdded = false;
                foreach (var mid in mergedPrIds)
                {
                    if (!existing.Contains(mid))
                    {
                        existing.Insert(0, mid);
                        anyAdded = true;
                    }
                }
                if (anyAdded)
                {
                    w.PrsJson = JsonSerializer.Serialize(existing);
                }

                w.Status = "complete";
                w.StallReason = null;
                w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await TryCloseWorkItemAsync(control, db, w.Id, ct);
                await db.SaveChangesAsync(ct);

                this.logger?.LogWarning(
                    "Healed already-merged workstream {WsId}: priorStatus='{PriorStatus}' "
                    + "priorPrsJson='{PriorPrs}' → Status='complete' with merged PR(s) {MergedPrs}. "
                    + "(Prevents the empty-PR second-drive that would have stalled the row.)",
                    w.Id, priorStatus, priorPrsJson, string.Join(",", mergedPrIds));
                await EmitChange(hub, "ws-complete", spawn, ct);

                // docs/forge/e2e.md Stage 5.2 — done-gate: if this was the
                // last outstanding workstream, advance the spawn directly
                // to done. Mirrors DriveReviewAsync's external-merge fast
                // path at line ~4245.
                if (await this.TryAdvanceToDoneAsync(db, hub, spawn, ct))
                {
                    return true;
                }
                continue;
            }

            if (w.Status is "review" or "merging" or "complete" or "cancelled" or "paused") continue;

            // 2026-05-28 — auto-heal the "pass-stalled-no-PR" pattern.
            // When the gen↔eval loop's evaluator scored a pass but the
            // post-pass commit/PR step failed (Win32Exception starting git
            // in a transiently-missing worktree path, scratch dir scrubbed
            // before ReadAllFiles, .git/ leak into commit dict before the
            // 2026-05-28 ReadAllFiles fix, etc.), the orchestrator stamps
            // Status=stalled. The latest deliverable keeps its verdict=pass
            // row, but the outer claim sweep was skipping stalled rows so
            // the only recovery was an operator Retry click — and rows
            // like code-intelligence:AST-PARSER-FOR-ADDIT stage 1.5-e2e
            // sat for ~11 h with a passing score because the operator
            // hadn't noticed. Eligibility is narrow (latest verdict=pass
            // AND no PR opened yet) so non-recoverable stalls (.gitignore
            // blockers, sloppy-3-attempts, build-gate failures, merge-PR
            // Phase 79c exhaustion) still surface for operator triage.
            if (string.Equals(w.Status, WorkstreamStatuses.Stalled, StringComparison.Ordinal))
            {
                if (!await this.TryAutoHealPassStalledAsync(control, db, w, ct))
                {
                    continue;
                }
                // Fall through — workstream is now active again.
            }

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

            if (await this.TryMoveExistingPrWorkstreamToReviewAsync(db, hub, spawn, w, prIdsByWorkstream, ct))
            {
                continue;
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
            var wsPointsForGuard = await LookupWorkstreamPointsAsync(control, db, w.Id, spawn.StoryPoints, ct);
            // Phase 64 — supply the in-process active-invocation check so the
            // guard's "overran budget+slack → Allow recovery spawn" branch
            // refuses to fire when the original SDK invocation is still alive.
            // Resolved off the root provider; the dispatcher is registered
            // as a singleton so this never creates a scope conflict.
            var agentRunnerForGuard = this.services.GetService<Forge.Api.Agents.IAgentRunner>();
            Func<string, bool>? isActive = agentRunnerForGuard is null
                ? null
                : agentRunnerForGuard.IsTaskActive;
            var guardDecision = await ImplSpawnGuard.CheckAsync(control, db, w.Id, wsPointsForGuard, DateTimeOffset.UtcNow, ct, isActive);
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
                    await this.TryRouteToSloppyReassignAsync(control, db, w, guardDecision.ConsecutiveNoProgress, ct);
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

            // 2026-05-19 — Phase 75: hard-ceiling cleanup. The guard
            // released the workstream for recovery despite
            // isInvocationActive=true because elapsed wall-clock exceeded
            // N× the guard window — the dispatcher's "still alive" claim
            // is unreliable at this point (wedged runner suspected). Two
            // pre-spawn actions are required before the normal
            // spawn-started fires:
            //   1. Cancel the wedged invocation via the dispatcher's
            //      CancelByTaskAsync, which trips the linked CTS so
            //      effectiveCt fires inside RunCopilotSdk and the SDK
            //      SendAsync (or watchdog poll) finally completes; the
            //      WrapEvents finally then cleans up the active map.
            //      Best-effort: if the active map no longer has this
            //      taskId we simply no-op.
            //   2. Emit a synthetic
            //      `task.impl.spawn-finished {success:false,
            //      exceptionType:"HardCeilingTimeout"}` so the guard's
            //      NEXT tick sees a closed lifecycle. Without this
            //      synthetic close, the immediate next tick would treat
            //      THIS tick's spawn-started as another in-flight spawn
            //      (or, worse, two simultaneously-recorded spawn-starteds
            //      would confuse the CountConsecutiveNoProgressSuccesses
            //      logic). The synthetic finish closes the original
            //      orphan lifecycle.
            if (guardDecision.WedgedSpawnHardCeilingExceeded)
            {
                this.logger?.LogWarning(
                    "Workstream {WsId} hard-ceiling exceeded: {Reason} — cancelling wedged invocation + emitting synthetic spawn-finished before recovery spawn",
                    w.Id, guardDecision.Reason);

                if (agentRunnerForGuard is not null)
                {
                    try
                    {
                        var cancelled = await agentRunnerForGuard.CancelByTaskAsync(w.Id, ct);
                        this.logger?.LogInformation(
                            "Workstream {WsId} hard-ceiling cancel: dispatcher cancelled {Count} invocation(s).",
                            w.Id, cancelled);
                    }
                    catch (Exception cancelEx)
                    {
                        this.logger?.LogWarning(cancelEx,
                            "Workstream {WsId} hard-ceiling cancel failed; proceeding with synthetic spawn-finished anyway.",
                            w.Id);
                    }
                }

                await this.EmitSpawnEventAsync(w.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = w.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = "HardCeilingTimeout",
                    message = guardDecision.Reason,
                    hardCeilingMultiplier = SyncConstants.InflightSpawnHardCeilingMultiplier,
                }, ct);
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
        // 2026-05-28 — bumped 4 → 8 to clear stage backlog faster on stories
        // with many parallel impl workstreams (e.g. AST-PARSER-FOR-ADDIT has
        // 6 tree-sitter parsers per language at the same stage and the prior
        // cap=4 left 2 idle behind the semaphore through a full first-wave
        // gen↔eval cycle). crew-eng has 18 members so capacity is fine; the
        // Copilot SDK can handle 8 concurrent invocations per process; the
        // per-agent busy filter still prevents the same agent being pulled
        // into two workstreams at once.
        var changedFlag = 0;
        var semaphore = new SemaphoreSlim(8);
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
                    var pickControl = pickScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var pickDb = pickScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var freshBusy = await Forge.Api.Services.BusyAgentsHelper.LoadBusyAgentsAsync(pickControl, ct);

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

                    // Generator rotation across pair attempts (2026-05-27).
                    // qa-e2e reassignment is re-driven HERE by the orchestrator
                    // (the SloppyReassignmentReconciler's fresh-generator pick
                    // lands on a task_queue row this path ignores), and the
                    // re-pick below previously only excluded live-busy + sibling
                    // agents — never the generators that already failed on THIS
                    // work item. So generator JS2 ran all three pair attempts
                    // (120 iters) on common-infra:HTTP-MEMORY stage 2.1-e2e while
                    // DC/WG/XQ never ran. Seed the busy set with every generator
                    // recorded in PriorPairsJson so ResolveImplPairAsync draws a
                    // never-tried crew-qa member. Scoped to qa-e2e (workstream
                    // id == work item id); the impl path already gets this
                    // exclusion via CrewQueueWorker's PriorPairsJson handling.
                    if (IsQaE2e(w))
                    {
                        var qaWi = await pickDb.WorkItems.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == w.Id, ct);
                        foreach (var priorGen in ExtractPriorGenerators(qaWi?.PriorPairsJson))
                        {
                            freshBusy.Add(priorGen);
                        }
                    }

                    var assignedPair = await ResolveImplPairAsync(
                        pickControl, pickDb, this.allocation, freshBusy, w.Sort,
                        // Phase 88 — qa-e2e workstreams must draw from crew-qa
                        // (test-attackers + memory + ai-test-engineer), not
                        // crew-eng (which includes refusal-prone refactor-
                        // defenders that self-veto "write more tests" tasks).
                        WorkstreamCategories.IsValid(w.Category) ? w.Category! : WorkstreamCategories.Impl,
                        ct);
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
            catch (WorkstreamInFlightException ifEx)
            {
                // Phase 74 follow-up — the per-work-item DeliverableLoop
                // mutex proved another impl run is currently owning this
                // workstream. This is a transient no-op, not a failed
                // engineer attempt and not a successful no-progress spawn.
                // Record a distinct skipped-concurrent finish so the guard
                // suppresses duplicate outer spawns while the winning run
                // continues.
                this.logger?.LogInformation(
                    "Workstream {WsId} impl skipped because another run is in-flight: {Message}",
                    item.ws.Id, ifEx.Message);
                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    skippedConcurrent = true,
                    exceptionType = nameof(WorkstreamInFlightException),
                    message = ifEx.Message,
                }, CancellationToken.None);
            }
            catch (WorkstreamPassGateRefusedException pgEx)
            {
                // Phase 75 follow-up — artifact files without a persisted
                // natural pass (or operator ACCEPT audit) are not shippable.
                // Treat this like a failed spawn for the in-flight guard, but
                // do not mark the workstream stalled here: the next tick can
                // re-enter gen-eval after the invalid row is demoted or after
                // the racing invocation finishes.
                this.logger?.LogWarning(
                    pgEx,
                    "Workstream {WsId} pass gate refused commit/PR; leaving active for retry.",
                    item.ws.Id);
                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    passGateRefused = true,
                    exceptionType = nameof(WorkstreamPassGateRefusedException),
                    message = pgEx.Message,
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
                var noProgDb = noProgScope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
            catch (SloppyVerdictException sve)
            {
                this.logger?.LogWarning(sve,
                    "Workstream {WsId} impl produced sloppy verdict; parking for reassignment. reason={SloppyReason} iterations={Iterations} finalScore={FinalScore}",
                    item.ws.Id, sve.SloppyReason, sve.Iterations, sve.FinalScore);

                var parkedForReassign = false;
                using (var sloppyScope = this.services.CreateScope())
                {
                    var sloppyControl = sloppyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var sloppyDb = sloppyScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var sloppyRow = await sloppyDb.Workstreams.FirstOrDefaultAsync(ws => ws.Id == item.ws.Id, CancellationToken.None);
                    if (sloppyRow is not null)
                    {
                        // Use the routed return value so the spawn-finished event reflects
                        // the ACTUAL outcome (not just the upstream Workstreams existence
                        // check). Without this, parkedForReassign:true was reported even
                        // when TryRouteToSloppyReassignAsync silently no-op'd on its guard,
                        // hiding the stuck-WS bug under a misleading success signal.
                        parkedForReassign = await this.TryRouteToSloppyReassignAsync(
                            sloppyControl,
                            sloppyDb,
                            sloppyRow,
                            consecutiveNoProgress: 0,
                            CancellationToken.None,
                            $"impl sloppy verdict ({sve.SloppyReason ?? "unknown"}); routing to SloppyReassignmentReconciler");
                    }
                }

                await this.EmitSpawnEventAsync(item.ws.Id, ImplSpawnGuard.SpawnFinishedKind, new
                {
                    wsId = item.ws.Id,
                    spawnId = spawn.Id,
                    success = false,
                    exceptionType = nameof(SloppyVerdictException),
                    message = sve.Message,
                    sloppyReason = sve.SloppyReason,
                    iterations = sve.Iterations,
                    finalScore = sve.FinalScore,
                    parkedForReassign,
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
                var buildDb = buildScope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        ControlDbContext control, ProductDbContext db, string workstreamId, int storyPoints, CancellationToken ct)
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
    private async Task<bool> TryRouteToSloppyReassignAsync(
        Forge.Api.Persistence.ControlDbContext control,
        Forge.Api.Persistence.ProductDbContext db,
        Forge.Api.Persistence.WorkstreamRow w,
        int consecutiveNoProgress,
        CancellationToken ct,
        string? reasonOverride = null)
    {
        var wiRow = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == w.Id, ct);
        if (wiRow is null) return false;

        // Idempotent guard — don't double-park already-parked rows, and don't
        // override a terminal-failed row that carries a real FailureReason
        // (set by SloppyReassignmentReconciler, BudgetCooldownReconciler, or
        // operator). HOWEVER we DO allow recovery of the "phantom-Failed"
        // state produced by SweepLiveBadgeReconciliationAsync racing
        // DeliverableLoop.TryMarkWorkItemLiveAsync: that path sets
        // ExecutionStatus=Failed with FailureReason=null and never
        // appends PriorPairsJson. Without this recovery branch, the
        // SloppyVerdictException catch silently no-ops and the WI stays
        // stuck forever (root cause of the 2026-05-28 4-WS stuck-sloppy
        // incident).
        if (wiRow.ExecutionStatus == Forge.Api.Persistence.ExecutionStatuses.PendingReassign)
        {
            return false;
        }
        if (wiRow.ExecutionStatus == Forge.Api.Persistence.ExecutionStatuses.Failed
            && !string.IsNullOrWhiteSpace(wiRow.FailureReason))
        {
            return false;
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
            reason = reasonOverride ?? $"orchestrator re-spawn loop without new deliverables; routing to SloppyReassignmentReconciler ({consecutiveNoProgress} consecutive no-progress successes)",
            priorPair = currentPair,
        }, ct);
        return true;
    }

    private static string TrimStack(string? stack, int maxChars)
    {
        if (string.IsNullOrEmpty(stack)) return string.Empty;
        return stack.Length <= maxChars ? stack : stack[..maxChars];
    }

    /// <summary>
    /// Phase 75 Fix 3 — detects when a workstream PR body embeds a
    /// generator-failure marker that should NEVER reach a real PR
    /// (e.g. literal "GENERATOR FAILED" from a transport-timeout
    /// deliverable, or the "**Score:** 0 |" header from a
    /// no-progress / empty-diff iter). Returns true when the body
    /// should be refused.
    /// </summary>
    /// <param name="prBody">The assembled PR body about to be sent
    /// to the provider.</param>
    internal static bool PrBodyHasFailureMarker(string? prBody)
    {
        if (string.IsNullOrEmpty(prBody)) return false;
        if (prBody.Contains("GENERATOR FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        // Match the `**Score:** N | **Iterations:** M` header (line 2629)
        // when N is 0. Whitespace tolerant; case-insensitive on the
        // surrounding markup. Score >= 1 passes the gate.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                prBody,
                @"\*\*Score:\*\*\s*0\s*\|",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Phase 70 × Phase 75 cross-cut — combines the pass-gate
    /// classification with the PR-body failure-marker check into one
    /// decision the orchestrator can apply at the moment of PR open.
    /// <para>OPERATOR-ACCEPTED iters bypass the body marker check
    /// because the operator has explicitly authorised the PR despite
    /// failure-marker content (typical case: a score=0
    /// generator-timeout iter where the agent's commit IS already on
    /// the branch, but the wall-clock budget pre-empted the evaluator
    /// score so <c>finalDel.Score=0</c> and <c>finalDel.Notes</c>
    /// still contains the original "GENERATOR FAILED" text — both
    /// of which would otherwise trip <see cref="PrBodyHasFailureMarker"/>
    /// even though the underlying work is good).</para>
    /// <para>NaturalPass + CorruptPass + NotPass paths all delegate
    /// to <see cref="PrBodyHasFailureMarker"/> — the body check is
    /// still belt-and-suspenders for any future code path that gets
    /// past the verdict gate without operator intent.</para>
    /// <para>Observed 2026-05-27 on local:UPDATE-HISTORY-IN-CL
    /// stage-1.4-clusterdbpersistenceprovider-base-class: ACCEPT
    /// flipped iter 2 verdict iterate→pass, ClassifyPassGate returned
    /// OperatorAccepted as designed, then this body check silently
    /// re-stalled the workstream because finalDel.Score=0 from the
    /// pre-empted iter. Without the operator-accept bypass, Phase 70
    /// can never unstick a generator-timeout race even though the
    /// agent's commit is already on origin and ready to ship.</para>
    /// </summary>
    /// <param name="passClass">Result of <see cref="ClassifyPassGate"/>
    /// on the deliverable row driving this PR.</param>
    /// <param name="prBody">The assembled PR body about to be sent
    /// to the provider.</param>
    /// <returns>True iff the orchestrator should refuse to open the
    /// PR and re-stall the workstream.</returns>
    internal static bool ShouldRefusePrBody(PassGateClassification passClass, string? prBody)
    {
        if (passClass == PassGateClassification.OperatorAccepted) return false;
        return PrBodyHasFailureMarker(prBody);
    }

    /// <summary>
    /// Phase 75 Fix 1 — classifies a deliverable row's verdict=pass
    /// state into one of three buckets so the orchestrator's
    /// "cached-files commit + PR open" branch can refuse a corrupt
    /// pass while still honouring genuine passes and operator
    /// overrides. Pure function over the row fields; exposed
    /// internal for unit testing.
    /// </summary>
    /// <param name="verdict">Deliverable verdict (e.g. "pass",
    /// "iterate"). Treated case-insensitively.</param>
    /// <param name="score">Deliverable score (0-100).</param>
    /// <param name="failureKind">Non-null when the underlying
    /// generator invocation aborted (transport timeout, max-turns
    /// cap, etc.); null on a clean evaluator run.</param>
    /// <param name="notes">Deliverable notes; Phase 70 Accept
    /// embeds an "OPERATOR ACCEPT (…)" prefix line for the
    /// operator-override case.</param>
    /// <returns>An enum classifying the row; corrupt-pass rows are
    /// the ones the orchestrator must refuse.</returns>
    internal enum PassGateClassification
    {
        /// <summary>Verdict isn't "pass" — gate doesn't apply.</summary>
        NotPass,

        /// <summary>Verdict=pass AND score >= PassThreshold AND no
        /// FailureKind — the gen-eval loop legitimately approved
        /// the work. Open the PR.</summary>
        NaturalPass,

        /// <summary>Verdict=pass AND notes contain the Phase 70
        /// "OPERATOR ACCEPT (…)" audit prefix — operator's manual
        /// override. Open the PR regardless of score/failure.</summary>
        OperatorAccepted,

        /// <summary>Verdict=pass but neither NaturalPass nor
        /// OperatorAccepted applies — score below threshold OR
        /// non-null FailureKind, without an Accept audit prefix.
        /// Almost certainly a buggy verdict flip; refuse to open PR
        /// and demote back to "iterate" so gen-eval can run.</summary>
        CorruptPass,
    }

    internal static PassGateClassification ClassifyPassGate(
        string? verdict, int score, string? failureKind, string? notes)
    {
        if (!string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase))
        {
            return PassGateClassification.NotPass;
        }
        var hasAcceptNote = !string.IsNullOrEmpty(notes)
            && notes.Contains("OPERATOR ACCEPT (", StringComparison.Ordinal);
        if (hasAcceptNote) return PassGateClassification.OperatorAccepted;
        var isNatural = score >= ScoringConstants.PassThreshold && string.IsNullOrEmpty(failureKind);
        return isNatural ? PassGateClassification.NaturalPass : PassGateClassification.CorruptPass;
    }

    private sealed class WorkstreamPassGateRefusedException : Exception
    {
        public WorkstreamPassGateRefusedException(string workstreamId, string message)
            : base($"Workstream {workstreamId} pass gate refused commit/PR: {message}")
        {
            this.WorkstreamId = workstreamId;
        }

        public string WorkstreamId { get; }
    }

    /// <summary>
    /// 2026-05-28 — auto-heal the "pass-stalled-no-PR" pattern. When the
    /// gen↔eval loop's evaluator scored a pass but the post-pass commit/PR
    /// step failed (e.g. <c>Win32Exception</c> starting git in a transiently
    /// missing worktree path, scratch dir scrubbed before
    /// <c>ReadAllFiles</c>, <c>.git/</c> leak into commit dict before the
    /// 2026-05-28 <see cref="CrewStoryArtifactStrategy.ReadAllFiles"/> fix),
    /// the orchestrator stamps Status=stalled at
    /// <c>DriveOneQaE2eWorkstreamAsync</c>:3593 / equivalent impl-path
    /// sites. The latest deliverable retains its verdict=pass row but the
    /// outer claim sweep at <see cref="DriveImplementAsync"/> was skipping
    /// stalled rows — the only recovery was an operator <c>Retry</c> click.
    /// Observed 2026-05-28 on <c>code-intelligence:AST-PARSER-FOR-ADDIT</c>
    /// stage 1.5-e2e: ~11 h with iter-3 score 94 and Status=stalled because
    /// the post-pass <c>Process.Start("git")</c> threw Win32 ("working
    /// directory missing").
    ///
    /// <para>This helper runs the same recovery as the operator
    /// <c>Retry</c> endpoint
    /// (<see cref="WorkstreamRecovery.SoftResetForResumeAsync"/>) when
    /// the eligibility pattern matches: latest deliverable verdict=pass
    /// AND no PR opened yet (<c>PrsJson</c> is empty). Both branches of
    /// the lifecycle ensure the workstream re-enters the loop with a
    /// fresh gen↔eval pair on a zeroed cost ledger; the prior chain
    /// stays in deliverables so the regenerated artifacts converge.</para>
    ///
    /// <para>Returns <c>true</c> when the heal fired (caller should NOT
    /// skip the workstream on this tick). Returns <c>false</c> on
    /// non-eligible stalls — operator triage required for
    /// <c>.gitignore</c> blockers, sloppy-3-attempts, build-gate
    /// failures, merge-PR Phase 79c exhaustion, etc.</para>
    /// </summary>
    private async Task<bool> TryAutoHealPassStalledAsync(
        ControlDbContext control, ProductDbContext db, WorkstreamRow w,
        CancellationToken ct)
    {
        // Non-empty PrsJson means the prior attempt at least opened a PR —
        // the stall is a real merge-side problem (Phase 79c exhausted,
        // failing required check, branch protection, etc.) that the
        // operator must triage. Auto-healing would re-run gen↔eval and
        // race against the open PR.
        var prsList = ParseStringList(w.PrsJson);
        if (prsList.Count > 0) return false;

        // SQLite cannot translate ORDER BY on DateTimeOffset; materialize
        // then sort client-side (mirrors SloppyReassignmentReconciler.cs:184
        // and the rest of this codebase). NOT AsNoTracking — the caller
        // SoftResetForResumeAsync mutates lastDel.Verdict / lastDel.Notes
        // and relies on EF change tracking to persist the demotion on
        // SaveChangesAsync. Filter first by WorkItemId so the materialized
        // set stays bounded to this workstream's deliverables (typically
        // 3–5 rows per qa-e2e).
        var delivs = await db.Deliverables
            .Where(d => d.WorkItemId == w.Id)
            .ToListAsync(ct);
        var lastDel = delivs
            .OrderByDescending(d => d.Iter)
            .ThenByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        // Eligibility: latest verdict is `pass` OR the latest score crossed the
        // pass threshold (covers the "checkbox-attention gate demoted an actual
        // pass to iterate, generator then made no file changes, no-progress
        // detector stalled" pattern — observed code-intelligence:AST-PARSER-
        // FOR-ADDIT rusttreesitterparser-implementation 2026-05-28: iters
        // 5/6/7 scored 94 with verdict iterate, iter 8 zero changes → stalled
        // "No progress: …". The 2026-05-28 DeliverableLoop high-confidence
        // bypass prevents the recurrence; this clause heals rows that were
        // stalled BEFORE that fix landed and re-enables the loop to commit
        // the already-good artifact.
        if (lastDel is null
            || (!string.Equals(lastDel.Verdict, "pass", StringComparison.Ordinal)
                && lastDel.Score < ScoringConstants.PassThreshold))
        {
            return false;
        }

        // Slim reset — DELIBERATELY NOT calling WorkstreamRecovery.SoftResetForResumeAsync
        // here. That helper was designed for operator Retry on a sloppy-capped row, where
        // the burned pair history is the problem and pair rotation is the cure. Auto-heal
        // hits a different shape: the pair just PASSED at the evaluator's threshold; the
        // failure was downstream (post-pass commit/PR step). Rotating the pair throws away
        // the converged session context (agent_memory + shared iter chain) and lands the
        // next claim on a fresh pair that may be weaker for this task — observed
        // 2026-05-28 on code-intelligence:AST-PARSER-FOR-ADDIT stage 1.5-e2e:
        // SoftResetForResumeAsync's PriorPairsJson wipe rotated XQ→WG as generator, and
        // the new pair regressed from 94 to 84 across two iters before an operator Restart
        // (Restart wipes deliverables too, so it costs a full gen↔eval cycle from iter 1).
        //
        // What this slim path does: un-stall + demote the latest deliverable to 'iterate'
        // so the DeliverableLoop's seeding logic re-enters at iter+1 with the same prior
        // chain. Crucially we keep PriorPairsJson, PairAttemptCount, and agent_usage
        // intact so the next claim picks the SAME pair (which already converged once) and
        // resumes against their cached agent_memory. The prior chain is the whole point.
        var priorStatus = w.Status;
        var priorVerdict = lastDel.Verdict;
        var priorStallReason = w.StallReason;
        w.Status = "active";
        w.StallReason = null;
        w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lastDel.Verdict = "iterate";
        var demoteNote = $"AUTO-HEAL (orchestrator-outer-drive) — verdict demoted from '{priorVerdict}' "
                       + "to 'iterate' because the post-pass commit/PR step failed without opening a PR. "
                       + $"Prior stall reason: {priorStallReason ?? "(none recorded)"}. "
                       + "Pair preserved (no PriorPairsJson wipe, no agent_usage wipe) so the next claim "
                       + "resumes against the same converged session.";
        lastDel.Notes = string.IsNullOrEmpty(lastDel.Notes)
            ? demoteNote
            : lastDel.Notes + "\n\n" + demoteNote;

        // Re-queue the work item so CrewQueueWorker claims it on the next tick.
        // PriorPairsJson stays untouched → same pair, cached agent_memory, fast re-converge.
        var wi = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == w.Id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            wi.FailureReason = null;
        }

        await db.SaveChangesAsync(ct);

        this.logger?.LogWarning(
            "Auto-healed pass-stalled workstream {WsId}: priorStatus={PriorStatus} "
            + "priorVerdict={PriorVerdict} (slim reset; pair + agent_memory preserved)",
            w.Id, priorStatus, priorVerdict);

        return true;
    }

    /// <summary>
    /// Execute the gen↔eval loop, commit, and open PR for a single workstream.
    /// Designed to run in parallel across workstreams — uses its own
    /// <see cref="ProductDbContext"/> scope for DB writes and re-checks
    /// workstream status before finalizing (guards against cancel/restart
    /// that may have fired mid-loop).
    /// </summary>
    private async Task DriveOneWorkstreamImplAsync(
        StorySpawnRow spawn, WorkstreamRow w, RepoRow? repo,
        string baseBranch, IHubContext<ForgeHub>? hub, CancellationToken ct)
    {
        // docs/forge/e2e.md Stage 5.1 — qa-e2e workstreams take a
        // shorter path: they don't need target discovery (no impl files
        // to edit), don't run the impl gen↔eval loop directly (the QA
        // strategy's ProduceStageE2eAsync drives its own loop with the
        // post-pass dotnet-test gate), and open a `[e2e]`-prefixed PR
        // that references the impl-sibling PR for traceability. The
        // dep-gate at the top of DriveImplementAsync already ensures
        // we don't reach here until the impl sibling's PR has merged
        // (impl ws.Status == "complete"), so the e2e tests run against
        // shipped code.
        if (IsQaE2e(w))
        {
            await this.DriveOneQaE2eWorkstreamAsync(spawn, w, repo, baseBranch, hub, ct);
            return;
        }

        // Check if the gen/eval loop already passed for this workstream.
        // Use a fresh scope so we don't race the original DbContext.
        using var scope = this.services.CreateScope();
        var control2 = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var lastDel = (await db2.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == w.Id)
                .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        IReadOnlyDictionary<string, string> files;

        // Phase 75 Fix 1 — tighten the "already passed → commit + PR" gate.
        // Before this fix, the only check was `lastDel.Verdict == "pass"`.
        // Observed 2026-05-17 on failover-cluster:XRAFT
        // stage-8.2-chaos-and-stress-testing: iter 7 was a generator-timeout
        // (verdict=iterate, score=0, FailureKind=generator-timeout), but
        // PR #42 was opened anyway with body literally containing
        // "GENERATOR FAILED" + "Score: 0 | Iterations: 7". Some path
        // briefly flipped iter 7's verdict to pass long enough for this
        // gate to fire, then it got demoted back. ClassifyPassGate
        // (internal static helper below) buckets the verdict=pass
        // state into:
        //   - NaturalPass    : score >= 90 AND no FailureKind → commit
        //   - OperatorAccepted: Phase 70 ACCEPT audit prefix in Notes → commit
        //   - CorruptPass    : verdict=pass but neither of the above —
        //                      refuse + demote to "iterate" so gen-eval
        //                      can rebuild a real verdict on the next tick.
        var passClass = lastDel is null
            ? PassGateClassification.NotPass
            : ClassifyPassGate(lastDel.Verdict, lastDel.Score, lastDel.FailureKind, lastDel.Notes);
        if (passClass == PassGateClassification.CorruptPass)
        {
            this.logger?.LogWarning(
                "Phase 75: workstream {WsId} has verdict=pass but score={Score} "
                + "(< {Threshold}) AND failureKind={FailureKind} AND no OPERATOR ACCEPT "
                + "audit prefix — refusing to commit + open PR. This row was "
                + "likely flipped by a buggy path. Demoting verdict back to "
                + "'iterate' so gen-eval can produce a real verdict on the next tick.",
                w.Id, lastDel!.Score, ScoringConstants.PassThreshold,
                lastDel.FailureKind ?? "(none)");
            // Take a writeable handle on the corrupt-pass row and demote
            // it back to "iterate" so DeliverableLoop's terminal-replay
            // branch doesn't keep returning early. Audit prefix preserves
            // the trail of "this was a pass at one point, the orchestrator
            // refused it because score/FailureKind disagreed".
            var liveDel = await db2.Deliverables
                .FirstOrDefaultAsync(d => d.Id == lastDel.Id, ct);
            if (liveDel is not null)
            {
                liveDel.Verdict = "iterate";
                var demoteNote = $"PHASE 75 CORRUPT-PASS REJECT — orchestrator refused to "
                    + $"commit a verdict=pass row with score={lastDel.Score} "
                    + $"(< {ScoringConstants.PassThreshold}) and failureKind="
                    + $"{lastDel.FailureKind ?? "(none)"} and no OPERATOR ACCEPT audit. "
                    + $"Verdict demoted to 'iterate' for the next gen-eval cycle.";
                liveDel.Notes = string.IsNullOrEmpty(liveDel.Notes)
                    ? demoteNote
                    : liveDel.Notes + "\n\n" + demoteNote;
                await db2.SaveChangesAsync(ct);
            }
            throw new WorkstreamPassGateRefusedException(
                w.Id,
                $"latest deliverable {lastDel.Id} was a corrupt pass (score={lastDel.Score}, "
                + $"failureKind={lastDel.FailureKind ?? "(none)"}).");
        }

        if (passClass is PassGateClassification.NaturalPass or PassGateClassification.OperatorAccepted)
        {
            this.logger?.LogInformation(
                "Workstream {WsId} already passed (iter {Iter}, score {Score}, source={Source}). Skipping gen/eval, using cached workspace.",
                w.Id, lastDel!.Iter, lastDel.Score,
                passClass == PassGateClassification.OperatorAccepted ? "operator-accept" : "natural-pass");
            files = await this.strategy.ReadCachedWorkstreamFilesAsync(spawn, w, ct);
        }
        else if (lastDel is not null && lastDel.Verdict is "stalled" or "stalled-budget" or "reject" or "sloppy" or "stalled-questions" or "stalled-no-convergence")
        {
            // Phase 47 — non-pass terminal verdicts route to a triage state
            // instead of re-spawning the gen↔eval loop.
            //
            // Phase 59 fix — `stalled-questions` is a soft park: the
            // workstream is waiting for operator answers, NOT failed. If
            // questions are still unanswered, return without stalling
            // (ImplSpawnGuard suppresses no-progress counting). If all
            // questions are answered, fall through to re-enter the
            // gen↔eval loop at iter N+1.
            if (string.Equals(lastDel.Verdict, "stalled-questions", StringComparison.Ordinal))
            {
                var hasUnanswered = await db2.DeliverableQuestions.AsNoTracking()
                    .AnyAsync(q => q.WorkItemId == w.Id && q.AnsweredAt == null, ct);
                if (hasUnanswered)
                {
                    this.logger?.LogInformation(
                        "DriveOneWorkstreamImpl: ws={WsId} verdict=stalled-questions iter={Iter} — "
                        + "unanswered questions remain; soft-parking without stalling.",
                        w.Id, lastDel.Iter);
                    return;
                }
                // All answered — fall through to produce next iteration.
                this.logger?.LogInformation(
                    "DriveOneWorkstreamImpl: ws={WsId} verdict=stalled-questions iter={Iter} — "
                    + "all questions answered; resuming gen-eval.",
                    w.Id, lastDel.Iter);
                files = await this.strategy.ProduceWorkstreamChangesAsync(spawn, w, ct);
                goto AfterProduce;
            }

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
                    await this.TryRouteToSloppyReassignAsync(control2, db2, w, consecutiveNoProgress: 0, ct);
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
                var crew = await control2.Crews.AsNoTracking()
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
                var usageRows = await control2.AgentUsage.AsNoTracking()
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

        // Phase 75 follow-up — ProduceWorkstreamChangesAsync may have
        // materialized files, but the orchestrator must still verify the
        // persisted final verdict before it commits those files or opens a PR.
        // The local UPDATE-HISTORY-IN-CL stage-1.3 incident proved that a
        // concurrent/failed eval path can leave files available while no
        // natural pass deliverable exists. In that state, opening or merging a
        // PR makes the UI read as complete without score evidence.
        var finalPassClass = finalDel is null
            ? PassGateClassification.NotPass
            : ClassifyPassGate(finalDel.Verdict, finalDel.Score, finalDel.FailureKind, finalDel.Notes);
        if (finalPassClass == PassGateClassification.CorruptPass)
        {
            this.logger?.LogWarning(
                "Phase 75: workstream {WsId} produced files but latest deliverable {DeliverableId} "
                + "is a corrupt pass (score={Score}, failureKind={FailureKind}); refusing commit + PR.",
                w.Id, finalDel!.Id, finalDel.Score, finalDel.FailureKind ?? "(none)");

            var liveDel = await db2.Deliverables.FirstOrDefaultAsync(d => d.Id == finalDel.Id, ct);
            if (liveDel is not null)
            {
                liveDel.Verdict = "iterate";
                var demoteNote = $"PHASE 75 POST-LOOP CORRUPT-PASS REJECT — orchestrator refused to "
                    + $"commit a verdict=pass row with score={finalDel.Score} "
                    + $"(< {ScoringConstants.PassThreshold}) and failureKind="
                    + $"{finalDel.FailureKind ?? "(none)"} and no OPERATOR ACCEPT audit. "
                    + $"Verdict demoted to 'iterate' for the next gen-eval cycle.";
                liveDel.Notes = string.IsNullOrEmpty(liveDel.Notes)
                    ? demoteNote
                    : liveDel.Notes + "\n\n" + demoteNote;
                await db2.SaveChangesAsync(ct);
            }

            throw new WorkstreamPassGateRefusedException(
                w.Id,
                $"post-loop deliverable {finalDel.Id} was a corrupt pass (score={finalDel.Score}, "
                + $"failureKind={finalDel.FailureKind ?? "(none)"}).");
        }

        if (finalPassClass is not (PassGateClassification.NaturalPass or PassGateClassification.OperatorAccepted))
        {
            var verdict = finalDel?.Verdict ?? "(none)";
            var score = finalDel?.Score.ToString(CultureInfo.InvariantCulture) ?? "(none)";
            this.logger?.LogWarning(
                "Phase 75: workstream {WsId} produced {FileCount} file(s) but latest deliverable "
                + "is not a valid pass (verdict={Verdict}, score={Score}); refusing commit + PR.",
                w.Id, files.Count, verdict, score);
            throw new WorkstreamPassGateRefusedException(
                w.Id,
                $"latest deliverable was not a valid pass (verdict={verdict}, score={score}).");
        }

        if (files.Count > 0)
        {
            var wsSlug = await WorkstreamWorktreeSlug.ComputeAsync(w, control2, db2, ct);
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
                ? await control2.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == genAgentId, ct) : null;
            var evalAgent = !string.IsNullOrEmpty(evalAgentId)
                ? await control2.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evalAgentId, ct) : null;
            var genName = genAgent?.Name ?? genAgentId ?? "unknown";
            var evalName = evalAgent?.Name ?? evalAgentId;

            // Resolve models from crew config.
            string? genModel = null, evalModel = null;
            var genCrew = !string.IsNullOrEmpty(finalDel.CrewId)
                ? await control2.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == finalDel.CrewId, ct) : null;
            if (genCrew is not null)
            {
                genModel = genAgent?.GeneratorModel ?? genCrew.Model;
                var evalCrew = !string.IsNullOrEmpty(genCrew.PairedWithCrewId)
                    ? await control2.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == genCrew.PairedWithCrewId, ct) : null;
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

        // Phase 75 Fix 3 — last-mile sanity check on the PR body. If the
        // assembled body contains generator-failure markers or an
        // explicit score-zero header, refuse to open the PR. The orchestrator
        // gate at the top of this method already rejects corrupt-pass
        // states; this is belt-and-suspenders for any future path that
        // bypasses the gate (e.g. a new endpoint that calls OpenPrAsync
        // directly with a body the orchestrator didn't review). Observed
        // 2026-05-17 PR #42 description embedded "GENERATOR FAILED"
        // + "Score: 0 | Iterations: 7" — the operator immediately
        // recognised it as broken-on-the-PR-page; making the orchestrator
        // recognise it too prevents the embarrassment of a broken PR
        // ever reaching the reviewer.
        //
        // Phase 70 × Phase 75 cross-cut (2026-05-27, local:UPDATE-HISTORY-IN-CL
        // stage-1.4) — OPERATOR ACCEPT overrides bypass the body check. The
        // operator's `POST /accept` is an explicit "open this PR" signal,
        // typically issued on a generator-timeout race where the agent's
        // commit IS on the branch but score=0 and the original
        // "GENERATOR FAILED" text is still pinned in `finalDel.Notes`.
        // Without the bypass, Phase 70 ACCEPT silently re-stalls the
        // workstream forever, defeating its design intent. See
        // <see cref="ShouldRefusePrBody"/> for the combined gate.
        if (ShouldRefusePrBody(finalPassClass, prBody))
        {
            this.logger?.LogError(
                "Phase 75: workstream {WsId} PR body contains failure markers "
                + "(GENERATOR FAILED or Score: 0). Refusing to open PR. "
                + "Body preview: {Preview}",
                w.Id, prBody.Length <= 400 ? prBody : prBody[..400] + "...");
            // Mark the workstream as stalled with a clear reason so the
            // operator sees the refusal in the UI. The gen-eval path
            // will retry on the next tick after the operator clears the
            // stall via Restart/Retry.
            var stalledRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (stalledRow is not null && stalledRow.Status is not "cancelled" and not "complete")
            {
                stalledRow.Status = WorkstreamStatuses.Stalled;
                stalledRow.StallReason =
                    "Phase 75: refused to open PR because the body embedded a "
                    + "generator-failure marker (GENERATOR FAILED or Score: 0). "
                    + "The orchestrator gate also rejected the verdict=pass row "
                    + "as corrupt. Restart or Retry to re-run gen-eval.";
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }
            return;
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
        freshRow.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await TryCompleteWorkItemExecutionAsync(db2, w.Id, ct);
        await db2.SaveChangesAsync(ct);

        await EmitChange(hub, "ws-pr-opened", spawn, ct);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 5.1 — true when the workstream is a
    /// trailing per-stage QA workstream (emitted by Stage 3.1's
    /// decomposer for any Stage with at least one acceptance scenario).
    /// The orchestrator routes these through
    /// <see cref="DriveOneQaE2eWorkstreamAsync"/> instead of the impl
    /// gen↔eval loop.
    /// </summary>
    private static bool IsQaE2e(WorkstreamRow w) =>
        string.Equals(w.Category, WorkstreamCategories.QaE2e, StringComparison.Ordinal);

    /// <summary>
    /// True for recoverable agent-transport failures — the Copilot SDK pipe /
    /// JSON-RPC channel dropped mid-call ("The pipe is being closed", broken
    /// pipe, transport death), commonly concurrent-dispatch collateral. The
    /// work itself isn't broken, so these should be retried (via the impl
    /// path's spawn-guard backoff) rather than terminal-stalled. Walks the
    /// inner-exception chain so a wrapped transport error is still caught.
    /// </summary>
    private static bool IsTransientTransportError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? string.Empty;
            if (m.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("pipe is broken", StringComparison.OrdinalIgnoreCase)
                || m.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
                || m.Contains("transport silent", StringComparison.OrdinalIgnoreCase)
                || m.Contains("transport died", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse the generator ids (slot 0 of each pair) out of a work item's
    /// <see cref="WorkItemRow.PriorPairsJson"/>. Used by the qa-e2e driver to
    /// rotate to a never-tried generator on a reassignment retry. Malformed
    /// JSON yields an empty set (treated as "no prior attempts").
    /// </summary>
    internal static HashSet<string> ExtractPriorGenerators(string? priorPairsJson)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(priorPairsJson)) return result;
        try
        {
            var pairs = JsonSerializer.Deserialize<List<string[]>>(priorPairsJson);
            if (pairs is not null)
            {
                foreach (var p in pairs)
                {
                    if (p is { Length: > 0 } && !string.IsNullOrEmpty(p[0]))
                    {
                        result.Add(p[0]);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException) { /* malformed — treat as no priors */ }
        return result;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 5.1 — per-workstream driver for
    /// <see cref="WorkstreamCategories.QaE2e"/> rows. Delegates artifact
    /// production to <see cref="IStoryArtifactStrategy.ProduceStageE2eAsync"/>
    /// (which runs its own gen↔eval loop + post-pass <c>dotnet test</c>
    /// gate per Stage 4.3), commits the result to the qa-e2e branch
    /// computed by Stage 3.1's decomposer, opens a PR with an
    /// <c>[e2e]</c>-prefixed title that references the impl-sibling PR
    /// id, and flips the workstream to <c>review</c> so the standard
    /// Phase 28 review-→-merge → done lifecycle picks it up. The
    /// dep-gate on the impl sibling is enforced by the calling sweep
    /// (<see cref="DriveImplementAsync"/>), not here — this method only
    /// runs after the sibling has reached <c>complete</c>.
    /// </summary>
    private async Task DriveOneQaE2eWorkstreamAsync(
        StorySpawnRow spawn, WorkstreamRow w, RepoRow? repo,
        string baseBranch, IHubContext<ForgeHub>? hub, CancellationToken ct)
    {
        using var scope = this.services.CreateScope();
        var control2 = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Re-check status from the live DB — operator may have cancelled
        // between the claim sweep and this task running, or a prior tick
        // already opened the PR (idempotency).
        var freshRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
        if (freshRow is null) return;
        if (freshRow.Status is "cancelled" or "paused" or "complete" or "merging" or "review")
        {
            this.logger?.LogDebug(
                "qa-e2e workstream {WsId} status={Status}; skipping (idempotent).",
                w.Id, freshRow.Status);
            return;
        }
        if (ParseStringList(freshRow.PrsJson).Count > 0)
        {
            // PR already opened by an earlier tick. Just mark review and
            // bail so the review loop picks it up.
            freshRow.Status = "review";
            await db2.SaveChangesAsync(ct);
            return;
        }

        // Run the QA producer. The deterministic strategy returns files
        // directly; the LLM-driven strategy runs its own gen↔eval loop
        // with the post-pass dotnet-test gate. Failures surface here as
        // exceptions; we mark the row stalled with a clear reason.
        //
        // Phase 88: sloppy verdicts (iter-cap, agent-refusal, score-
        // plateau, token-cap, etc.) are now thrown as
        // SloppyVerdictException so we can mirror the impl-path's
        // PendingReassign bookkeeping (CrewQueueWorker.cs:2848-2884) and
        // let SloppyReassignmentReconciler rotate the pair. Without
        // this, pre-Phase-88 qa-e2e iter-cap exits left the work item
        // ExecutionStatus untouched and the reconciler (which filters
        // on ExecutionStatus == PendingReassign) never picked it up,
        // requiring operator Restart to unstick.
        IReadOnlyDictionary<string, string> files;
        try
        {
            files = await this.strategy.ProduceStageE2eAsync(spawn, w, ct);
        }
        catch (SloppyVerdictException ex)
        {
            this.logger?.LogWarning(
                "qa-e2e producer returned sloppy verdict for {WsId}: reason={Reason}, iters={Iters}, score={Score}; "
                + "marking stalled + parking for SloppyReassignmentReconciler.",
                w.Id, ex.SloppyReason, ex.Iterations, ex.FinalScore);

            var liveWs = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            var liveWi = await db2.WorkItems.FirstOrDefaultAsync(wi => wi.Id == w.Id, ct);
            if (liveWi is not null)
            {
                liveWi.ExecutionStatus = ExecutionStatuses.PendingReassign;
                liveWi.UpdatedAt = DateTimeOffset.UtcNow;

                // Mirror CrewQueueWorker.cs:2861-2877 PriorPairsJson
                // append with dedupe so the reconciler's pair-exclusion
                // filter doesn't re-pick the same refusing pair.
                var priorPairs = new List<string[]>();
                if (!string.IsNullOrWhiteSpace(liveWi.PriorPairsJson))
                {
                    try
                    {
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string[]>>(liveWi.PriorPairsJson);
                        if (parsed is not null) priorPairs = parsed;
                    }
                    catch (System.Text.Json.JsonException) { /* malformed — start fresh */ }
                }
                var pairList = ParseStringList(liveWs?.AgentsJson ?? w.AgentsJson);
                var gen = pairList.Count > 0 ? pairList[0] : string.Empty;
                var eval = pairList.Count > 1 ? pairList[1] : string.Empty;
                var currentPair = new[] { gen ?? string.Empty, eval ?? string.Empty };
                var alreadyLast = priorPairs.Count > 0
                    && priorPairs[priorPairs.Count - 1].Length == 2
                    && string.Equals(priorPairs[priorPairs.Count - 1][0], currentPair[0], StringComparison.Ordinal)
                    && string.Equals(priorPairs[priorPairs.Count - 1][1], currentPair[1], StringComparison.Ordinal);
                if (!alreadyLast) priorPairs.Add(currentPair);
                liveWi.PriorPairsJson = System.Text.Json.JsonSerializer.Serialize(priorPairs);
            }
            if (liveWs is not null && liveWs.Status is not "cancelled" and not "complete")
            {
                liveWs.Status = WorkstreamStatuses.Stalled;
                liveWs.StallReason = string.IsNullOrEmpty(ex.SloppyReason)
                    ? $"qa-e2e sloppy: {ex.Iterations} iters, final score {ex.FinalScore}"
                    : $"qa-e2e sloppy ({ex.SloppyReason}): {ex.Iterations} iters, final score {ex.FinalScore}";
            }
            // Save both rows in one transaction so we never have a
            // visible "stalled workstream without parked work item".
            await db2.SaveChangesAsync(ct);

            await EmitChange(hub, "ws-stalled", spawn, ct);
            return;
        }
        catch (WorkstreamInFlightException ifEx)
        {
            // Phase 74 follow-up (2026-05-31) — ProduceStageE2eAsync yields with
            // WorkstreamInFlightException when DeliverableLoop's per-workItemId
            // mutex proved another qa-e2e run already owns this workstream. This
            // is a transient no-op, NOT a producer failure: the winning in-flight
            // run produces + commits the e2e and owns the real verdict. Mirror the
            // impl path's dedicated handler (DriveImplementAsync ~line 2539) — emit
            // a skipped-concurrent finish and return WITHOUT marking stalled. The
            // generic catch below MUST NOT see this exception, or it records
            // "qa-e2e producer failed: WorkstreamInFlightException" and clobbers
            // the live run's status to stalled (observed 2026-05-31 on
            // react-and-neo4j-nvl-web-ui stage-vite-and-typescript-scaffold-e2e
            // right after the ProduceStageE2eAsync skipped-concurrent fix deployed:
            // the e2e producer correctly yielded, but this call site stalled it).
            this.logger?.LogInformation(
                "Workstream {WsId} qa-e2e skipped because another run is in-flight: {Message}",
                w.Id, ifEx.Message);
            await this.EmitSpawnEventAsync(w.Id, ImplSpawnGuard.SpawnFinishedKind, new
            {
                wsId = w.Id,
                spawnId = spawn.Id,
                success = false,
                skippedConcurrent = true,
                exceptionType = nameof(WorkstreamInFlightException),
                message = ifEx.Message,
            }, CancellationToken.None);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient agent-transport failures (Copilot SDK pipe drop /
            // JSON-RPC channel death — often concurrent-dispatch collateral)
            // are recoverable: the channel died, not the work. Don't terminal-
            // stall. Rethrow so DriveImplementAsync's per-workstream failure
            // path emits spawn-finished(success=false); ImplSpawnGuard then
            // applies its backoff window and the next tick retries. A genuinely
            // persistent failure still escalates via the guard's consecutive-
            // failure / no-progress thresholds, so this can't spin forever.
            if (IsTransientTransportError(ex))
            {
                this.logger?.LogWarning(ex,
                    "qa-e2e producer hit a transient transport error for {WsId}; "
                    + "rethrowing for backoff+retry instead of stalling.", w.Id);
                throw;
            }

            this.logger?.LogWarning(ex,
                "qa-e2e producer threw for workstream {WsId}; marking stalled.", w.Id);
            var stalledRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (stalledRow is not null && stalledRow.Status is not "cancelled" and not "complete")
            {
                stalledRow.Status = WorkstreamStatuses.Stalled;
                stalledRow.StallReason = $"qa-e2e producer failed: {ex.GetType().Name}: {ex.Message}";
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }
            return;
        }

        if (files.Count == 0)
        {
            // Producer succeeded but emitted nothing — treat the same as
            // a stall so the operator sees the row needs attention.
            var emptyRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (emptyRow is not null && emptyRow.Status is not "cancelled" and not "complete")
            {
                emptyRow.Status = WorkstreamStatuses.Stalled;
                emptyRow.StallReason = "qa-e2e producer emitted zero files; no PR to open.";
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }
            return;
        }

        // Phase 86 — recheck status after the LLM producer returns; operator
        // may have paused/cancelled the workstream mid-loop (Pair 2 caught by
        // an operator action between producer start and return). Without this
        // recheck the stale `w` row from the outer dispatcher slips through.
        var afterProducerRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
        if (afterProducerRow is null) return;
        if (afterProducerRow.Status is "cancelled" or "paused" or "complete" or "merging" or "review")
        {
            this.logger?.LogDebug(
                "qa-e2e workstream {WsId} status={Status} after producer; skipping commit (idempotent).",
                w.Id, afterProducerRow.Status);
            return;
        }

        // Commit qa-e2e files to the workstream branch. The slug
        // computation reuses WorkstreamWorktreeSlug.Compute which
        // (Stage 3.2) shadows the impl sibling's `stage-{N.M}-{slug}`
        // with a `-e2e` suffix so the on-disk paths are intuitive.
        var wsSlug = await WorkstreamWorktreeSlug.ComputeAsync(w, control2, db2, ct);

        // Phase 86 — the LLM producer used a temp workspace
        // (CrewStoryArtifactStrategy.PrepareWorkspace), NOT a git worktree.
        // Materialise the git worktree HERE so the auto-fix preflight has a
        // working directory for `git check-ignore`, AND so CommitFilesAsync
        // reuses the same worktree slug deterministically.
        await this.git.EnsureWorktreeAsync(repo, w.Branch, baseBranch, ct, worktreeSlug: wsSlug);

        // Phase 86 — auto-fix preflight + commit + merge under one
        // try/catch. When the repo's root `.gitignore` would exclude the
        // qa-e2e output paths (OneBranch `/*/` + allowlist case), the
        // preflight appends a narrow `!/tests/` exception as a prep commit
        // BEFORE the actual file commit. On failure (nested .gitignore
        // blocker, etc.) the fail-fast guard inside CommitFilesAsync still
        // throws GitIgnoredPathException — we catch it here and STALL
        // instead of bubbling up. Without this catch the outer
        // DriveImplementAsync sweep re-enters the workstream on every tick
        // and spins forever burning LLM tokens (observed 3 retry cycles in
        // task_transcript_event before this fix).
        //
        // Note: only `GitIgnoredPathException` is caught; transient git /
        // transport errors continue to bubble for the existing backoff +
        // retry path in DriveImplementAsync (matches the producer's
        // IsTransientTransportError contract at line ~3217).
        const string e2eBaseDir = "tests";
        try
        {
            await this.git.EnsurePathsCommittableAsync(
                repo, w.Branch, files.Keys, e2eBaseDir, ct);

            await this.git.CommitFilesAsync(repo, w.Branch, wsSlug, files,
                $"qa-e2e({w.Id}): {w.Name}", ct);

            // Merge latest base into the branch so the PR diff is minimal.
            await this.git.MergeUpstreamIntoBranchAsync(repo, w.Branch, baseBranch, ct: ct);
        }
        catch (GitIgnoredPathException ex)
        {
            this.logger?.LogWarning(ex,
                "qa-e2e commit hit gitignored paths for workstream {WsId} on branch {Branch}; "
                + "marking stalled (auto-fix could not land the allowlist exception, "
                + "or a nested .gitignore is blocking). Paths: {Paths}",
                w.Id, w.Branch, string.Join(", ", ex.IgnoredPaths));

            var stalledRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (stalledRow is not null && stalledRow.Status is not "cancelled" and not "complete")
            {
                stalledRow.Status = WorkstreamStatuses.Stalled;
                // The exception's message already includes the actionable
                // operator guidance (PlanDocBaseDir / nested-gitignore hint)
                // and the raw `git check-ignore -v` output. Preserve it
                // verbatim so the UI Stalled badge renders enough context.
                stalledRow.StallReason = $"qa-e2e gitignored paths: {ex.Message}";
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }
            return;
        }

        // Skip the PR when the commit produced no diff vs base. This happens
        // when every file the producer emitted matched a repo `.gitignore`
        // rule (observed on the `update` repo: its `.gitignore` line `/*/`
        // ignores all top-level dirs, so the producer's `tests/...` artifacts
        // were staged as nothing and CommitFilesAsync committed an empty
        // tree). Opening a PR then just creates empty review noise, so stall
        // with an actionable reason instead.
        if (!await this.git.BranchHasChangesVsBaseAsync(repo, w.Branch, baseBranch, ct))
        {
            var noChangeRow = await db2.Workstreams.FirstOrDefaultAsync(ws => ws.Id == w.Id, ct);
            if (noChangeRow is not null && noChangeRow.Status is not "cancelled" and not "complete")
            {
                noChangeRow.Status = WorkstreamStatuses.Stalled;
                noChangeRow.StallReason =
                    $"qa-e2e produced {files.Count} file(s) but committed nothing to "
                    + $"{w.Branch} — every path was excluded by the repo's .gitignore "
                    + "(the e2e output root is gitignored in this repo). No PR opened. "
                    + "Fix the repo .gitignore or point the e2e output at a tracked test "
                    + "directory, then Restart.";
                await db2.SaveChangesAsync(ct);
                await EmitChange(hub, "ws-stalled", spawn, ct);
            }

            this.logger?.LogWarning(
                "qa-e2e {WsId}: branch {Branch} has no diff vs {Base} after commit "
                + "({FileCount} files emitted, all gitignored?); skipping empty PR.",
                w.Id, w.Branch, baseBranch, files.Count);
            return;
        }

        // Resolve the impl-sibling PR id (if any) for the body link.
        // The sibling's PlanAnchor is this row's anchor with the `/e2e`
        // suffix stripped; the sibling's PR id is the first entry in
        // its PrsJson (PersistDecompositionAsync writes it there at
        // impl PR open time).
        var implPrRef = await ResolveImplSiblingPrAsync(db2, w, ct);

        var prTitle = $"[e2e] {w.Name}";
        var prBodyBuilder = new System.Text.StringBuilder()
            .AppendLine($"## {prTitle}")
            .AppendLine()
            .AppendLine($"**Story:** {spawn.StoryId}")
            .AppendLine($"**Stage anchor:** `{w.PlanAnchor}`")
            .AppendLine($"**Workstream:** `{w.Id}`")
            .AppendLine()
            .AppendLine("Trailing qa-e2e artifacts for the stage: Gherkin `.feature` file +")
            .AppendLine("Reqnroll step-definitions + (for `compose`/`lab-*` setup types)")
            .AppendLine("pipeline YAML stubs. See docs/forge/e2e.md Stage 4.3 for the")
            .AppendLine("producer contract.");
        if (!string.IsNullOrEmpty(implPrRef))
        {
            prBodyBuilder.AppendLine().AppendLine($"**Impl PR:** {implPrRef}");
        }

        var prId = await this.provider.OpenPrAsync(new StoryPrRequest(
            Branch: w.Branch,
            BaseBranch: baseBranch,
            Title: prTitle,
            Body: prBodyBuilder.ToString(),
            Repo: repo,
            WorkItemId: w.Id,
            Workstream: w.Id,
            Epic: spawn.StoryId,
            AutoMerge: AutoMergeOf(spawn)), ct);

        var liveRow = await db2.Workstreams.FirstAsync(ws => ws.Id == w.Id, ct);
        var prsList = ParseStringList(liveRow.PrsJson);
        if (!prsList.Contains(prId)) prsList.Add(prId);
        liveRow.PrsJson = JsonSerializer.Serialize(prsList);
        liveRow.Status = "review";
        liveRow.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await TryCompleteWorkItemExecutionAsync(db2, w.Id, ct);
        await db2.SaveChangesAsync(ct);
        await EmitChange(hub, "ws-pr-opened", spawn, ct);
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 5.1 — find the impl-sibling workstream's
    /// PR id for the qa-e2e body link. Returns null when no sibling
    /// exists or it hasn't opened a PR yet (shouldn't reach here in
    /// production because the dep-gate ensures the sibling is
    /// <c>complete</c>, but tolerated for defense-in-depth).
    /// </summary>
    private static async Task<string?> ResolveImplSiblingPrAsync(
        ProductDbContext db, WorkstreamRow qaWorkstream, CancellationToken ct)
    {
        var qaAnchor = qaWorkstream.PlanAnchor;
        if (string.IsNullOrEmpty(qaAnchor)
            || !qaAnchor.EndsWith(CrewStoryArtifactStrategy.QaE2eAnchorSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        var implAnchor = qaAnchor[..^CrewStoryArtifactStrategy.QaE2eAnchorSuffix.Length];
        var impl = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.StorySpawnId == qaWorkstream.StorySpawnId
                                    && w.PlanAnchor == implAnchor, ct);
        if (impl is null) return null;
        var prs = ParseStringList(impl.PrsJson);
        return prs.Count > 0 ? prs[0] : null;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 5.2 — done-gate predicate. A spawn is
    /// complete when every active workstream (impl + qa-e2e) has
    /// <c>Status == "complete"</c>. Workstreams in terminal-but-not-
    /// shipped states (<c>WorkstreamStatuses.Historic</c>,
    /// <c>"cancelled"</c>) are excluded from the requirement — they
    /// belong to a superseded decomposition or were operator-cancelled.
    /// <c>"paused"</c> is also excluded so an operator pause on one
    /// workstream doesn't permanently block the spawn from advancing.
    /// Returns <c>false</c> when the spawn has zero non-terminal
    /// workstreams (defensive — an empty decomposition shouldn't flip
    /// to done; the operator should re-decompose).
    /// </summary>
    internal static async Task<bool> IsSpawnDoneAsync(
        ProductDbContext db, string spawnId, CancellationToken ct)
    {
        var rows = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawnId)
            .Select(w => w.Status)
            .ToListAsync(ct);
        if (rows.Count == 0) return false;

        var anyActive = false;
        foreach (var status in rows)
        {
            if (status == WorkstreamStatuses.Historic) continue;
            if (status == "cancelled") continue;
            if (status == "paused") continue;
            anyActive = true;
            if (status != "complete") return false;
        }
        return anyActive;
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 5.2 — when every workstream is
    /// <c>complete</c>, flip <c>StorySpawn.Phase</c> to <c>done</c>,
    /// stamp <c>UpdatedAt</c>, persist, and emit the standard <c>done</c>
    /// SignalR event so the UI re-fetches. No-ops when the spawn isn't
    /// done yet OR when the phase is already <c>done</c>. Returns
    /// <c>true</c> when it advanced the phase (so callers can chain
    /// into <c>DriveDoneAsync</c> on the same tick).
    /// </summary>
    private async Task<bool> TryAdvanceToDoneAsync(
        ProductDbContext db, IHubContext<ForgeHub>? hub, StorySpawnRow spawn, CancellationToken ct)
    {
        if (string.Equals(spawn.Phase, StorySpawnPhases.Done, StringComparison.Ordinal))
        {
            return false;
        }
        if (!await IsSpawnDoneAsync(db, spawn.Id, ct))
        {
            return false;
        }

        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnRow.Phase = StorySpawnPhases.Done;
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "done", spawnRow, ct);

        this.logger?.LogInformation(
            "Spawn {SpawnId} advanced to Done via the Stage 5.2 review-side gate "
            + "(every workstream Status=complete). Previous phase: {PrevPhase}.",
            spawn.Id, spawn.Phase);
        return true;
    }

    // ── review ───────────────────────────────────────────────────────────────
    // internal (was private) so ReviewDispatchRaceGuardTests can exercise the
    // IsTaskActive concurrent-dispatch guard in isolation, without driving the
    // full DriveAsync flow (which would also run DriveImplementAsync).
    internal async Task<bool> DriveReviewAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
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

        // 2026-05-26 — concurrent-dispatch guard. The impl DeliverableLoop runs
        // a workstream's generator in the BACKGROUND (it can't block the per-spawn
        // drive tick for the full wall-clock budget), so a workstream can be
        // Status="review" (PR opened) while its impl loop is STILL iterating for
        // the same workItemId. If DriveReviewAsync then dispatches a reviewer /
        // review-fix agent for that workItemId, it collides with the live impl
        // invocation and AgentRunnerDispatcher's Phase-74 guard throws
        // "Concurrent dispatch refused" → review-failed → the workstream stalls.
        // (Observed: update-history stage-1.3 — iter-1 generator-timeout, loop
        // auto-retried iter-2 while a Review dispatch raced it.) Skip any
        // workstream whose impl/agent invocation is still live; the next tick
        // re-evaluates once the loop has terminated (pass / iter-cap / stall).
        var reviewRunner = this.services.GetService<Forge.Api.Agents.IAgentRunner>();

        foreach (var w in workstreams)
        {
            // 2026-05-13 — include "complete" so late-arriving review comments
            // on already-merged PRs still get addressed by the fix loop. Without
            // this, any comment posted after the upstream merge would be
            // stranded (PrSyncJob imports it with State="active" but the
            // workstream is out of DriveReviewAsync's scan window).
            if (w.Status != "review" && w.Status != "merging" && w.Status != "complete") continue;

            // Serialise dispatch per workItemId (see comment above): defer this
            // workstream's review while its impl/generator invocation is still live.
            if (reviewRunner is not null && reviewRunner.IsTaskActive(w.Id))
            {
                this.logger?.LogDebug(
                    "DriveReviewAsync: deferring ws {WsId} this tick — an agent invocation is still active "
                    + "for this workItemId (avoids the Phase-74 concurrent-dispatch stall).", w.Id);
                continue;
            }

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
                if (await this.TrySkipReviewFixOnStalePrAsync(control, db, spawn, w, prId, activeComments, repo, ct))
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
                    await TryCloseWorkItemAsync(control, db, w.Id, ct);
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

                    // docs/forge/e2e.md Stage 5.2 — done-gate. When this
                    // merge was the last outstanding workstream, advance
                    // the spawn directly from review → done WITHOUT
                    // visiting the legacy qa phase. Legacy paused
                    // spawns still drain via DriveQaAsync; new spawns
                    // skip qa entirely (the qa-e2e per-stage workstreams
                    // get merged via this same DriveReviewAsync path).
                    if (await this.TryAdvanceToDoneAsync(db, hub, spawn, ct))
                    {
                        return changed;
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
                await TryCloseWorkItemAsync(control, db, w.Id, ct);
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

                // docs/forge/e2e.md Stage 5.2 — done-gate (external-merge
                // fast-path). Same as the merging→complete branch above:
                // when this was the last outstanding workstream, advance
                // straight to done skipping the legacy qa phase.
                if (await this.TryAdvanceToDoneAsync(db, hub, spawn, ct))
                {
                    return changed;
                }
                continue;
            }

            // Abandoned-PR guard (2026-06-24). A PR whose source (or target)
            // branch was deleted upstream is mapped to Col=="abandoned" /
            // IsActive==false — NOT "merged", so the fast-path above misses it.
            // Attempting ApproveAndMergeAsync on it makes ADO return 400
            // TF401398 ("the pull request cannot be activated because the
            // source and/or the target branch no longer exists"), which the
            // merge-failure classifier then MISLABELS as a provider-auth failure
            // — the operator re-authenticates in vain. The PR is terminal and
            // unmergeable as-is, so stall this row with an accurate,
            // recreate-on-Restart reason instead of attempting an impossible
            // merge. Observed local:NET10-MIGRATION stage-1.2
            // persistence-and-http-client-compatibility-spike PR #16210445 on
            // repo download.
            if (prRowAtReview is not null
                && (string.Equals(prRowAtReview.Col, "abandoned", StringComparison.OrdinalIgnoreCase)
                    || (!prRowAtReview.IsActive
                        && !string.Equals(prRowAtReview.Col, "merged", StringComparison.OrdinalIgnoreCase))))
            {
                if (w.Status is not "cancelled" and not "paused" and not "complete")
                {
                    w.Status = WorkstreamStatuses.Stalled;
                    // Deliberately free of the tokens the UI's isAuthStall
                    // heuristic keys on (401 / unauthorized / authentication /
                    // "not a merge conflict") so this renders as a generic
                    // "PR MERGE BLOCKED", not the auth banner.
                    w.StallReason =
                        $"PR {prId} was abandoned upstream and its source branch no longer exists, so it "
                        + "can no longer be merged. Restart this workstream to recreate the branch and PR "
                        + "from the current base.";
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (Exception saveEx)
                    {
                        this.logger?.LogWarning(saveEx,
                            "DriveReviewAsync: failed to persist abandoned-PR stall for ws {WsId}; "
                            + "next tick will re-enter and retry the transition.", w.Id);
                    }
                    this.logger?.LogWarning(
                        "Workstream {WsId} PR {PrId} is abandoned upstream (Col={Col}, IsActive={Active}); "
                        + "stalled with recreate-on-Restart guidance instead of attempting an unmergeable merge.",
                        w.Id, prId, prRowAtReview.Col, prRowAtReview.IsActive);
                    await EmitChange(hub, "ws-stalled", spawn, ct);
                    await this.EmitSpawnEventAsync(w.Id, "task.merge-failed", new
                    {
                        wsId = w.Id,
                        prId,
                        spawnId = spawn.Id,
                        message = $"PR {prId} abandoned upstream (source branch deleted).",
                        exceptionType = "AbandonedPr",
                        kind = "abandoned",
                    }, ct);
                    changed = true;
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
                IReadOnlyList<ReviewComment> comments;
                try
                {
                    comments = await this.strategy.ProduceReviewCommentsAsync(spawn, w, prId, round: 0, ct);
                }
                catch (Exception revEx) when (revEx is not OperationCanceledException)
                {
                    // A reviewer-agent failure (Max turns reached, parse error,
                    // non-transient transport drop) is a WORKSTREAM-level
                    // failure. Stall just THIS row instead of letting it bubble
                    // to TickOneSpawnAsync and halt the whole spawn via
                    // spawn.LastError ("ORCHESTRATOR HALTED"). Mirrors the
                    // per-workstream merge-failure catches below. Transient
                    // transport errors rethrow for backoff+retry (the agent
                    // channel died, not the work) — same policy as the qa-e2e
                    // and impl paths.
                    if (IsTransientTransportError(revEx)) throw;
                    await this.StallWorkstreamForReviewFailureAsync(control, db, hub, spawn, w, prId, revEx, ct);
                    changed = true;
                    continue;
                }
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
                        await this.ApproveAndMergeAsync(control, db, prId, w.Branch, reviewer, repo, baseBranch, ct);
                        w.Status = "merging";
                        await db.SaveChangesAsync(ct);
                        changed = true;
                        continue;
                    }
                    catch (EmptyPrMergeRefusedException emptyEx)
                    {
                        await this.StallForEmptyMergeAsync(db, hub, spawn, w, emptyEx, ct);
                        changed = true;
                        continue;
                    }
                    catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
                    {
                        await this.TryResolveConflictsAndRetryMergeAsync(
                            control, db, hub, spawn, w, prId, reviewer, repo, baseBranch, mergeEx, ct);
                        changed = true;
                        continue;
                    }
                }

                foreach (var c in comments)
                {
                    await this.provider.PostPrCommentAsync(prId, c.FilePath, c.Line, c.Body, reviewer, ct);
                }

                // Re-load active comments and defer fix loop to pass 2.
                allComments = await db.PrComments.Where(c => c.PrId == prId).ToListAsync(ct);
                activeComments = allComments.Where(c => c.State == "active").ToList();
                if (activeComments.Count > 0)
                {
                    fixNeeded.Add((w, prId, activeComments));
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
                await this.ApproveAndMergeAsync(control, db, prId, w.Branch, approveReviewer, repo, baseBranch, ct);
                w.Status = "merging";
                await db.SaveChangesAsync(ct);
                changed = true;
            }
            catch (EmptyPrMergeRefusedException emptyEx)
            {
                await this.StallForEmptyMergeAsync(db, hub, spawn, w, emptyEx, ct);
                changed = true;
            }
            catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
            {
                await this.TryResolveConflictsAndRetryMergeAsync(
                    control, db, hub, spawn, w, prId, approveReviewer, repo, baseBranch, mergeEx, ct);
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
        ControlDbContext control, ProductDbContext db, StorySpawnRow spawn, WorkstreamRow w, string prId,
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
            var controlDefense = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var dbDefense = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            if (await this.TrySkipReviewFixOnStalePrAsync(controlDefense, dbDefense, spawn, w, prId, activeComments, repo, ct))
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
                    var slugControl = slugScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var slugDb = slugScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var wsSlug = await WorkstreamWorktreeSlug.ComputeAsync(w, slugControl, slugDb, ct);
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
                // 2026-05-26 — reset the per-comment timeout counter on
                // success so a future unrelated comment on the same ws
                // doesn't inherit stale failure count from an earlier one.
                this.reviewFixTimeoutCounter.TryRemove((w.Id, oc.Id), out _);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 2026-05-26 — circuit breaker for budget-exhaustion
                // failures. AgentRunnerDispatcher surfaces hard-timeout /
                // per-turn-stuck / transport-silent as failures whose
                // messages contain either "hard timeout" or "exceeded …
                // budget" (the dispatcher's own error template). Everything
                // else (commit failures, comment-post 5xx, etc.) keeps the
                // legacy "log + retry next tick" behaviour because those are
                // typically transient and self-heal.
                var isBudgetExhaustion = ex.Message.Contains("hard timeout", StringComparison.OrdinalIgnoreCase)
                    || (ex.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
                        && ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase));
                if (isBudgetExhaustion)
                {
                    var key = (w.Id, oc.Id);
                    var count = this.reviewFixTimeoutCounter.AddOrUpdate(key, 1, (_, v) => v + 1);
                    this.logger?.LogWarning(ex,
                        "RunReviewFixLoopAsync: review-fix budget exhausted on comment {CommentId} on PR {PrId} "
                        + "for ws {WsId} (consecutive attempt {Count}/{Max}).",
                        oc.Id, prId, w.Id, count, MaxReviewFixTimeouts);
                    if (count >= MaxReviewFixTimeouts)
                    {
                        try
                        {
                            var systemReply =
                                $"System: review-fix exceeded its wall-clock budget on {count} consecutive attempts. "
                                + "Auto-resolving so the workstream can advance — please address the reviewer's "
                                + "concern manually if it remains relevant. "
                                + $"Last failure: {TrimStack(ex.Message, 200)}";
                            await this.provider.ReplyPrCommentAsync(oc.Id, systemReply, ReviewFixBudgetResolverId, ct);
                            await this.provider.ResolvePrCommentAsync(oc.Id, ReviewFixBudgetResolverId, ct);
                            if (this.taskChannel is not null)
                            {
                                await this.taskChannel.EmitAsync(w.Id, "review-fix.budget-exhausted", new
                                {
                                    wsId = w.Id,
                                    spawnId = spawn.Id,
                                    prId,
                                    commentId = oc.Id,
                                    attempts = count,
                                    filePath = oc.FilePath,
                                    line = oc.Line,
                                    lastError = TrimStack(ex.Message, 400),
                                }, ct: ct);
                            }
                            this.reviewFixTimeoutCounter.TryRemove(key, out _);
                        }
                        catch (Exception escalateEx) when (escalateEx is not OperationCanceledException)
                        {
                            this.logger?.LogError(escalateEx,
                                "RunReviewFixLoopAsync: failed to auto-resolve comment {CommentId} after {Count} "
                                + "consecutive budget exhaustions; will retry the escalation next tick.",
                                oc.Id, count);
                        }
                    }
                }
                else
                {
                    this.logger?.LogWarning(ex,
                        "DriveReviewAsync: failed to fix comment {CommentId} on PR {PrId} for ws {WsId}; "
                        + "will retry next tick. surviving comments will still be processed in this loop.",
                        oc.Id, prId, w.Id);
                }
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
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
            // Classify the failure so the operator gets an ACTIONABLE reason
            // instead of a one-size-fits-all "resolve the conflict" message.
            // A 401/403 on the provider merge API is an AUTH failure, not a
            // merge conflict — telling the operator to "rebase the branch"
            // is misleading and wastes their time (observed on
            // code-intelligence:CLEAN-CODE PR #70/#71: Forge's GitHub OAuth
            // token expired → PUT /pulls/N/merge → 401, while the git-CLI
            // credential still worked so fetch/push succeeded). The agent
            // resolver can't fix auth, so this never routes through
            // TryResolveConflictsAndRetryMergeAsync — the operator must
            // re-authenticate, then Restart.
            w.StallReason = IsAuthError(ex)
                ? $"Merge failed for PR {prId}: provider authentication failed ({FlattenMessage(ex)}). "
                    + "This is NOT a merge conflict — Forge's provider token (GitHub OAuth / ADO) is "
                    + "expired or unauthorized. Re-authenticate the provider in Forge, then Restart. "
                    + "(The git-CLI credential can still be valid, so clone/fetch/push may keep working.)"
                : $"Merge failed for PR {prId}: {FlattenMessage(ex)}. "
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
            // Let the UI render the right banner: auth → "re-authenticate",
            // conflict → "agent is resolving / resolve + Restart", other → generic.
            kind = IsAuthError(ex) ? "auth" : (IsMergeConflictError(ex) ? "conflict" : "other"),
        }, ct);
    }

    /// <summary>
    /// True when the merge failed because the provider rejected our
    /// credentials (GitHub <c>401 Unauthorized "Requires authentication"</c> /
    /// <c>"Bad credentials"</c>, ADO <c>TF400813</c> / 401 / 403). This is an
    /// auth/token problem, NOT a merge conflict — the agent resolver can't fix
    /// it, so the operator must re-authenticate the provider. Distinguished
    /// from a normal 403 rate-limit by the auth wording.
    /// </summary>
    internal static bool IsAuthError(Exception ex)
    {
        if (ex is null) return false;

        // Prefer the typed HTTP status when the provider populated it. The
        // GitHub merge path throws HttpRequestException(statusCode: …), so a
        // real 401/403 there is unambiguous and needs no message sniffing.
        if (ex is System.Net.Http.HttpRequestException hre
            && hre.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            return true;
        }

        var msg = ex.Message ?? string.Empty;

        // Provider auth phrases (GitHub 401 bodies + the ADO identity-denied code).
        if (msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Requires authentication", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("TF400813", StringComparison.OrdinalIgnoreCase)) // ADO: identity not authorized
        {
            return true;
        }

        // HTTP 401/403 rendered as a parenthesised status token, e.g.
        // AdoProvider's "ADO PR merge failed (401) at …". Anchored on the
        // "(NNN)" form so it CANNOT match the "401" buried in unrelated ADO
        // error CODES like TF401398 ("PR cannot be activated — source/target
        // branch gone", which is a 400) or TF401019. A bare Contains("401")
        // matched TF401398 and sent operators on fruitless re-auths (observed
        // local:NET10-MIGRATION PR #16210445 on repo download, 2026-06-24).
        return AuthHttpStatusToken.IsMatch(msg);
    }

    // "(401)" / "(403)" as the providers render an HTTP status inside a thrown
    // message. Parenthesis-anchored so it never matches a TFxxxxxx error code.
    private static readonly System.Text.RegularExpressions.Regex AuthHttpStatusToken =
        new(@"\(40[13]\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Route a failure of the reviewer-agent invocation
    /// (<see cref="IStoryArtifactStrategy.ProduceReviewCommentsAsync"/>) to a
    /// workstream-scoped <c>stalled</c> transition, instead of letting it
    /// escape <see cref="DriveReviewAsync"/> and trip the outer
    /// <see cref="TickOneSpawnAsync"/> catch — which sets
    /// <see cref="StorySpawnRow.LastError"/>, surfaces as "ORCHESTRATOR HALTED",
    /// and freezes EVERY workstream in the spawn.
    ///
    /// <para>Triggered by: the reviewer agent hitting its turn cap
    /// (<c>Max turns (N) reached</c>), a parse/transport failure that isn't
    /// transient, etc. A reviewer that can't converge on ONE workstream is a
    /// workstream-level failure — the operator Restarts just that row.
    /// Mirrors <see cref="StallWorkstreamForMergeFailureAsync"/> (Phase 49's
    /// per-workstream merge-failure isolation), closing the same hole for the
    /// Step-1 reviewer pass that was previously left unguarded: observed on
    /// code-intelligence:CLEAN-CODE, where a Review agent hit Max turns (60) on
    /// the policy-publish workstream and halted the entire spawn.</para>
    /// </summary>
    private async Task StallWorkstreamForReviewFailureAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkstreamRow w, string prId, Exception ex, CancellationToken ct)
    {
        // 2026-06-03 — a "Concurrent dispatch refused" here is NOT a reviewer
        // convergence failure: it's the Phase-74 per-workItemId mutex rejecting
        // the review dispatch because the impl/generator DeliverableLoop for the
        // SAME workItemId re-activated (e.g. an auto-retry iteration) in the
        // TOCTOU window between DriveReviewAsync's IsTaskActive defer-guard
        // (~line 4260) and the actual reviewer dispatch. Stalling here would
        // clobber a workstream whose impl already PASSED to review. Treat it as
        // a benign defer: leave the row in "review" so the next tick
        // re-evaluates — the impl loop terminates eventually (pass / iter-cap /
        // stall), after which IsTaskActive is false and the review proceeds.
        // Mirrors the impl/qa-e2e WorkstreamInFlightException no-op handling.
        // Observed: code-intelligence:PR-INGESTER mgmt-verb-read-co-change-clusters
        // stalled "Review failed for PR #396: Concurrent dispatch refused".
        if (FlattenMessage(ex).Contains("Concurrent dispatch refused", StringComparison.OrdinalIgnoreCase))
        {
            this.logger?.LogInformation(
                "DriveReviewAsync: deferring ws {WsId} review this tick — concurrent-dispatch refused "
                + "(impl loop re-activated in the IsTaskActive TOCTOU window); will retry next tick.",
                w.Id);
            await this.EmitSpawnEventAsync(w.Id, "task.review-deferred", new
            {
                wsId = w.Id,
                prId,
                spawnId = spawn.Id,
                reason = "concurrent-dispatch-refused",
            }, ct);
            return;
        }

        this.logger?.LogWarning(ex,
            "DriveReviewAsync: ProduceReviewCommentsAsync failed for ws {WsId} PR {PrId}. "
            + "Marking stalled; spawn continues with remaining workstreams.",
            w.Id, prId);

        if (w.Status is not "cancelled" and not "paused" and not "complete")
        {
            w.Status = WorkstreamStatuses.Stalled;
            w.StallReason =
                $"Review failed for PR {prId}: {FlattenMessage(ex)}. "
                + "The reviewer agent did not converge (e.g. hit its turn cap). "
                + "Restart the workstream to re-run the review.";
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                this.logger?.LogWarning(saveEx,
                    "DriveReviewAsync: failed to persist stalled status for ws {WsId} after review failure; "
                    + "next tick will re-enter and retry the transition.", w.Id);
            }
            await EmitChange(hub, "ws-stalled", spawn, ct);
        }

        await this.EmitSpawnEventAsync(w.Id, "task.review-failed", new
        {
            wsId = w.Id,
            prId,
            spawnId = spawn.Id,
            message = FlattenMessage(ex),
            exceptionType = ex.GetType().Name,
        }, ct);
    }

    /// <summary>
    /// Phase 79 — true when <paramref name="ex"/> is the specific
    /// 405 / merge-conflict variety the agent resolver can act on
    /// (vs failing required status checks, branch protection, etc.).
    /// The GitHub API returns 405 for all three, distinguished only
    /// by the response body wording.
    /// </summary>
    internal static bool IsMergeConflictError(Exception ex)
    {
        if (ex is null) return false;
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("Pull Request has merge conflicts", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("merge conflict", StringComparison.OrdinalIgnoreCase))
            return true;
        // Ado uses different wording — "cannot be completed due to merge conflicts".
        if (msg.Contains("merge conflicts", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// True for a TRANSIENT, retryable merge failure where the upstream base
    /// (or head) ref moved between the mergeability computation and the merge
    /// call — GitHub's <c>405 "Base branch was modified. Review and try the
    /// merge again."</c> (and the symmetric "Head branch was modified"). This
    /// is NOT a real conflict and needs NO operator action: it's a benign race
    /// when a sibling PR merges into the shared base (e.g. many qa-e2e PRs all
    /// targeting <c>feature/clean-code</c>) just before this PR's merge PUT.
    ///
    /// <para>The fix-and-retry loop (<see cref="TryResolveConflictsAndRetryMergeAsync"/>)
    /// handles it for free: <c>MergeBaseIntoBranchAndDetectConflictsAsync</c>
    /// fetches the new base tip and merges it in — clean (no agent needed) when
    /// there's no real conflict, agent-resolved when there is — then re-pushes
    /// and re-attempts the PR merge against the current tip. Treating this as a
    /// permanent operator-gated stall (the old behaviour) stranded mergeable
    /// PRs on a 30-second race; observed on code-intelligence:CLEAN-CODE
    /// <c>2.1-e2e</c> PR #64.</para>
    /// </summary>
    internal static bool IsRetryableMergeRace(Exception ex)
    {
        if (ex is null) return false;
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Base branch was modified", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Head branch was modified", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("try the merge again", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phase 79c — agent-driven merge-conflict resolution. Called by the
    /// orchestrator's two merge-fail catches (Step 1 no-comments path
    /// + Step 3 active-comments-resolved path) in <see cref="DriveReviewAsync"/>.
    ///
    /// <para>Algorithm:
    /// <list type="number">
    ///   <item>Discriminate via <see cref="IsMergeConflictError"/>. If not
    ///         a merge conflict (failing CI, branch protection, network,
    ///         etc.), stall immediately — agent can't fix those.</item>
    ///   <item>Loop up to <see cref="ScoringConstants.MaxResolveAttempts"/>:
    ///         <list type="bullet">
    ///           <item><see cref="IStoryGitOps.MergeBaseIntoBranchAndDetectConflictsAsync"/>
    ///                 — fetch base + run <c>git merge origin/{base}</c>
    ///                 in the workstream worktree.</item>
    ///           <item>State=Clean → no conflicts; push the resulting
    ///                 merge commit (or FF) via
    ///                 <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/>
    ///                 with an empty resolved-files dict.</item>
    ///           <item>State=Conflicted → strategy resolver writes
    ///                 resolved files → ContinueMergeAndPushAsync(resolved)
    ///                 finalises the merge commit and plain-pushes.</item>
    ///           <item>State=MergeFailed → break + stall with detail.</item>
    ///         </list></item>
    ///   <item>On a successful push, retry <see cref="ApproveAndMergeAsync"/>.
    ///         If it succeeds, ws.Status="merging"; if it fails AGAIN with
    ///         a new conflict (e.g. another sibling PR landed during the
    ///         resolver run), the outer loop retries until cap.</item>
    ///   <item>After cap exhausted, stall via
    ///         <see cref="StallWorkstreamForMergeFailureAsync"/> with the
    ///         LAST failure detail.</item>
    /// </list></para>
    ///
    /// <para>Phase 79c replaces the original rebase + <c>--force-with-lease</c>
    /// flow with a merge + plain push so reviewer comments stay anchored to
    /// their original lines (force-push routinely orphaned late-arriving
    /// comments). The agent resolver itself is unchanged.</para>
    /// </summary>
    private async Task TryResolveConflictsAndRetryMergeAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkstreamRow w, string prId, string reviewerAgentId,
        RepoRow? repo, string baseBranch, Exception originalMergeEx, CancellationToken ct)
    {
        // 1. Attempt resolution for genuine merge conflicts AND for the
        //    transient "base/head branch was modified" race (a sibling PR
        //    merged into the shared base just before our merge PUT). Both are
        //    fixed by the same loop below: re-merge the current base tip into
        //    the ws branch (clean for a pure race, agent-resolved for a real
        //    conflict) and re-attempt the PR merge. Everything else (failing
        //    CI, branch protection, network) the agent can't fix → stall.
        if (!IsMergeConflictError(originalMergeEx) && !IsRetryableMergeRace(originalMergeEx))
        {
            this.logger?.LogInformation(
                "Phase 79c: merge failure on ws {WsId} PR {PrId} is NOT a merge conflict "
                + "or retryable base-moved race (message='{Msg}'); stalling immediately — "
                + "agent can't fix this class of error.",
                w.Id, prId, FlattenMessage(originalMergeEx));
            await this.StallWorkstreamForMergeFailureAsync(control, db, hub, spawn, w, prId, originalMergeEx, ct);
            return;
        }

        await this.EmitSpawnEventAsync(w.Id, "task.merge-conflict-resolve-started", new
        {
            wsId = w.Id, prId, spawnId = spawn.Id, baseBranch,
        }, ct);

        Exception lastFailure = originalMergeEx;
        for (var attempt = 1; attempt <= ScoringConstants.MaxResolveAttempts; attempt++)
        {
            this.logger?.LogInformation(
                "Phase 79c: ws {WsId} PR {PrId} resolve attempt {Attempt}/{Max}.",
                w.Id, prId, attempt, ScoringConstants.MaxResolveAttempts);

            // 2. Fetch + merge base into ws branch in the worktree.
            MergeResult merge;
            try
            {
                merge = await this.git.MergeBaseIntoBranchAndDetectConflictsAsync(repo, w.Branch, baseBranch, ct);
            }
            catch (Exception rex) when (rex is not OperationCanceledException)
            {
                this.logger?.LogWarning(rex,
                    "Phase 79c: MergeBaseIntoBranchAndDetectConflictsAsync threw for ws {WsId} attempt {Attempt}.", w.Id, attempt);
                lastFailure = rex;
                break;
            }

            if (merge.State == MergeState.MergeFailed)
            {
                lastFailure = new InvalidOperationException($"Merge failed: {merge.Detail}");
                this.logger?.LogWarning(
                    "Phase 79c: merge failed on ws {WsId} attempt {Attempt}: {Detail}",
                    w.Id, attempt, merge.Detail);
                break;
            }

            // 3. Resolve conflicts (if any), then finalize the merge commit
            //    + push. Unlike rebase, a merge produces ALL conflicts in
            //    one batch — no inner conflict-round loop is needed.
            IReadOnlyDictionary<string, string> resolved;
            if (merge.State == MergeState.Conflicted)
            {
                try
                {
                    resolved = await this.strategy.ProduceConflictResolutionAsync(
                        spawn, w, merge.ConflictedFiles, baseBranch, ct);
                }
                catch (Exception sex) when (sex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(sex,
                        "Phase 79c: agent resolver failed for ws {WsId} attempt {Attempt}.", w.Id, attempt);
                    lastFailure = sex;
                    await this.git.AbortMergeAsync(repo, w.Branch, ct);
                    break;
                }
            }
            else
            {
                // Merge was clean (FF or merge commit with no conflicts);
                // pass an empty dict to ContinueMergeAndPushAsync so it
                // takes the push-only path.
                resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            MergeResult finalize;
            try
            {
                finalize = await this.git.ContinueMergeAndPushAsync(repo, w.Branch, resolved, ct);
            }
            catch (Exception cex) when (cex is not OperationCanceledException)
            {
                this.logger?.LogWarning(cex,
                    "Phase 79c: ContinueMergeAndPushAsync threw for ws {WsId} attempt {Attempt}.", w.Id, attempt);
                lastFailure = cex;
                await this.git.AbortMergeAsync(repo, w.Branch, ct);
                break;
            }

            if (finalize.State == MergeState.Conflicted)
            {
                // Resolver left markers in place; give up on this attempt
                // and let the outer loop try again (or stall when cap hits).
                lastFailure = new InvalidOperationException(
                    $"Agent resolver did not produce a clean resolution on attempt {attempt} "
                    + $"({finalize.ConflictedFiles.Count} file(s) still conflicted).");
                await this.git.AbortMergeAsync(repo, w.Branch, ct);
                continue;
            }
            if (finalize.State == MergeState.MergeFailed)
            {
                lastFailure = new InvalidOperationException(
                    $"Merge finalize/push failed: {finalize.Detail}");
                break;
            }

            // 4. Push succeeded → retry the upstream PR merge.
            try
            {
                await this.ApproveAndMergeAsync(control, db, prId, w.Branch, reviewerAgentId, repo, baseBranch, ct);
                w.Status = "merging";
                await db.SaveChangesAsync(ct);
                await this.EmitSpawnEventAsync(w.Id, "task.merge-conflict-resolved", new
                {
                    wsId = w.Id, prId, spawnId = spawn.Id, attempt,
                }, ct);
                this.logger?.LogInformation(
                    "Phase 79c: ws {WsId} PR {PrId} merge succeeded after {Attempt} attempt(s).",
                    w.Id, prId, attempt);
                return;
            }
            catch (Exception remergeEx) when (remergeEx is not OperationCanceledException)
            {
                lastFailure = remergeEx;
                // If the new failure is ALSO a merge conflict OR the base
                // moved again (another sibling PR landed during our resolver
                // run), continue the outer loop for another attempt. Otherwise
                // stall — different class of failure (CI, protection).
                if (!IsMergeConflictError(remergeEx) && !IsRetryableMergeRace(remergeEx))
                {
                    this.logger?.LogInformation(
                        "Phase 79c: merge retry on ws {WsId} hit a NON-conflict failure ({Msg}); stalling.",
                        w.Id, FlattenMessage(remergeEx));
                    break;
                }
                this.logger?.LogInformation(
                    "Phase 79c: merge retry on ws {WsId} hit another conflict (probably a sibling PR landed); retrying.",
                    w.Id);
            }
        }

        // 5. Exhausted. Stall with the LAST failure detail.
        this.logger?.LogWarning(
            "Phase 79c: ws {WsId} PR {PrId} resolver exhausted {Max} attempt(s); stalling. Last error: {Msg}",
            w.Id, prId, ScoringConstants.MaxResolveAttempts, FlattenMessage(lastFailure));
        await this.StallWorkstreamForMergeFailureAsync(control, db, hub, spawn, w, prId, lastFailure, ct);
    }

    // ── qa ───────────────────────────────────────────────────────────────────
    /// <summary>
    /// docs/forge/e2e.md Stage 5.3 — per-process tracking of spawns
    /// we've already emitted the legacy-drain warning for. Prevents
    /// log spam on every orchestrator tick while a paused
    /// <c>Phase='qa'</c> spawn slowly drains its step-def PRs.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> WarnedLegacyDrainSpawns =
        new(StringComparer.Ordinal);

    private async Task<bool> DriveQaAsync(
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        await this.EnsureSpawnBaseBranchAsync(spawn, repo, ct);
        var baseBranch = BaseBranchOf(spawn);

        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);

        // docs/forge/e2e.md Stage 5.3 — legacy-drain guard. New-shape
        // spawns (Stage 3.1+) carry qa-e2e workstreams in the same
        // workstream set as the impl rows; their drain runs via
        // DriveImplementAsync + DriveReviewAsync's Stage 5.2 done-gate
        // (review → done). They MUST NOT touch the legacy QA-batch
        // path because that path would:
        //   (a) re-author Gherkin feature files for the whole story
        //       (legacy assumes one .feature per workstream, not the
        //       per-stage `.feature` Stage 4.3 emits), and
        //   (b) open a single story-level QA PR (legacy assumes one
        //       per spawn, not per stage).
        // Skip the batch path when:
        //   - no legacy QaPrId exists on the spawn (a paused-mid-drain
        //     spawn would carry one — that case still drains), AND
        //   - the workstream set carries at least one qa-e2e row
        //     (proves Stage 3.1's decomposer emitted them).
        if (string.IsNullOrEmpty(spawn.QaPrId)
            && workstreams.Any(w => string.Equals(w.Category, WorkstreamCategories.QaE2e, StringComparison.Ordinal)))
        {
            this.logger?.LogInformation(
                "DriveQaAsync no-op for spawn {SpawnId}: Phase={Phase} but workstream set carries "
                + "qa-e2e rows (Stage 3.1+ new shape). The Stage 5.2 done-gate inside DriveReviewAsync "
                + "advances this spawn review → done via the qa-e2e workstream PR merges; the legacy "
                + "QA-batch path stays parked. No legacy QaPrId to drain.",
                spawn.Id, spawn.Phase);
            return false;
        }

        // docs/forge/e2e.md Stage 5.3 — one-time warning per process
        // when an existing QaPrId is detected. Operators triaging a
        // paused-mid-drain spawn see this warning ONCE in the log and
        // know the legacy drain code is responsible for finishing it.
        if (!string.IsNullOrEmpty(spawn.QaPrId)
            && WarnedLegacyDrainSpawns.TryAdd(spawn.Id, 0))
        {
            this.logger?.LogWarning(
                "Legacy DriveQaAsync drain: spawn {SpawnId} carries QaPrId={QaPrId} from a "
                + "pre-Stage-3.1 Forge version. Draining the legacy QA-batch + step-def PRs to "
                + "completion. New spawns route qa-e2e per docs/forge/e2e.md Stage 5.1+ and never "
                + "enter Phase=qa. Operator action: none required — drain proceeds automatically.",
                spawn.Id, spawn.QaPrId);
        }

        // Step 1 — produce the QA feature files & open the QA PR (idempotent).
        if (string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qaBranch = spawn.QaBranch ?? $"story/{GitSafe(spawn.StoryId)}/qa";
            await this.git.EnsureBranchAsync(repo, qaBranch, baseBranch, ct);
            // docs/forge/e2e.md Stage 4.1 — DriveQaAsync is the legacy
            // Phase 5 QA-batch drain path (Stage 5.3 keeps it alive for
            // paused legacy spawns only). The new code path emits per-stage
            // qa-e2e artifacts via ProduceStageE2eAsync inside the impl
            // driver. Suppress CS0618 here because the obsolete call is
            // intentional: this branch runs only when spawn.QaPrId is set
            // by an older Forge version on a paused spawn.
#pragma warning disable CS0618 // Type or member is obsolete
            var files = await this.strategy.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);
#pragma warning restore CS0618 // Type or member is obsolete
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
            await this.ApproveAndMergeAsync(control, db, prId, qaBranch, qaReviewer, repo, baseBranch, ct);
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
            // docs/forge/e2e.md Stage 4.1 — legacy step-def drain path. Same
            // back-compat suppression as the qa-features call above; new
            // spawns route through ProduceStageE2eAsync instead.
#pragma warning disable CS0618 // Type or member is obsolete
            var files = await this.strategy.ProduceStepDefinitionsAsync(spawn, w, ct);
#pragma warning restore CS0618 // Type or member is obsolete
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
            await this.ApproveAndMergeAsync(control, db, prId, stepBranch, qaId, repo, baseBranch, ct);

            // Stash on workstream PrsJson so artifacts endpoint surfaces it.
            var prsList = ParseStringList(w.PrsJson);
            if (!prsList.Contains(prId)) prsList.Add(prId);
            w.PrsJson = JsonSerializer.Serialize(prsList);
            w.Status = "complete";
            await db.SaveChangesAsync(ct);
            await TryCloseWorkItemAsync(control, db, w.Id, ct);
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
        ControlDbContext control, ProductDbContext db, IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn, WorkItemRow story, RepoRow? repo, CancellationToken ct)
    {
        // Transition the upstream ADO work item to Closed (idempotent).
        // ProductStory-sourced spawns have no upstream provider record — we
        // mark the local ProductStoryRow as 'done' instead.
        //
        // The guard below is the "one-shot per spawn" contract the scheduler
        // relies on (see SpawnDependencyScheduler.cs: "DriveDoneAsync
        // short-circuits once the WI state is already closed"). Subsequent
        // ticks must hit this short-circuit so we don't re-emit story-closed
        // every 15 s.
        if (story.State is "closed" or "resolved") return false;

        // `story` is the WorkItemRow loaded (or synthesised) by RunOneStepAsync.
        // For ProductStory-sourced spawns the synthetic row is stamped with
        // Provider="local" — those have no upstream provider record to
        // transition, so the provider call is a no-op (and historically
        // returned early WITHOUT touching wi.State, leaving the guard above
        // permanently un-tripped). Drive the local Provider check off the
        // synthetic row's Provider field rather than "row exists" because
        // RunOneStepAsync guarantees the row always exists by this point.
        var hasUpstream = !string.IsNullOrEmpty(story.Provider)
            && !string.Equals(story.Provider, "local", StringComparison.OrdinalIgnoreCase);
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

        // Stamp the local WorkItemRow as closed so the next-tick guard at the
        // top of this method short-circuits. For upstream rows
        // ProviderStoryProviderOps.TransitionWorkItemAsync already writes
        // wi.State on the underlying DB row, but it does so in its own DI
        // scope so the EF-tracked `story` entity in this scope still
        // observes the original "active" value — re-stamping here makes the
        // guard reliable regardless of which provider path executed. For
        // local synthetic rows (Provider="local") the provider call is a
        // silent no-op, so this stamp is the only place wi.State becomes
        // "closed".
        story.State = "closed";
        story.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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
    /// <summary>
    /// Stall a workstream whose PR merge was refused by the empty-PR guard
    /// (<see cref="EmptyPrMergeRefusedException"/>). Distinct from a merge
    /// conflict: no resolver retry — the branch committed nothing (usually a
    /// gitignored output path), so the row needs operator attention.
    /// </summary>
    private async Task StallForEmptyMergeAsync(
        ProductDbContext db, IHubContext<ForgeHub>? hub, StorySpawnRow spawn,
        WorkstreamRow w, EmptyPrMergeRefusedException ex, CancellationToken ct)
    {
        this.logger?.LogWarning(
            "Refused to merge {PrId} for ws {WsId}: branch has no diff vs {Base}; stalling.",
            ex.PrId, w.Id, ex.BaseBranch);
        w.Status = WorkstreamStatuses.Stalled;
        w.StallReason =
            $"Refused to merge {ex.PrId}: branch has no diff vs {ex.BaseBranch} "
            + "(empty PR). The producer committed nothing — usually the output "
            + "path is excluded by the repo's .gitignore. No merge performed. "
            + "Fix the .gitignore (or the output path), then Restart.";
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "ws-stalled", spawn, ct);
    }

    private async Task ApproveAndMergeAsync(
        ControlDbContext control,
        ProductDbContext db,
        string prId, string branch, string reviewerAgentId,
        RepoRow? repo, string baseBranch, CancellationToken ct)
    {
        // Merge-side empty-PR guard. The open-side guard
        // (DriveOneQaE2eWorkstreamAsync) blocks NEW empty PRs, but PRs opened
        // before it existed can still reach this merge path; merging one lands
        // nothing and falsely marks the stage done. Refuse to merge a branch
        // with no diff vs base. BranchHasChangesVsBaseAsync fails open (returns
        // true) on a git error, so a transient check failure never blocks a
        // legitimate merge.
        if (repo is not null
            && !await this.git.BranchHasChangesVsBaseAsync(repo, branch, baseBranch, ct))
        {
            // 2026-05-26 — `git diff --quiet base...branch` reports "no diff"
            // for BOTH (a) genuinely empty branches AND (b) branches already
            // merged into base (merge-base collapses to branch.tip, so the
            // three-dot walk is empty). Without disambiguating, the operator
            // sees "fix your .gitignore" — but really an out-of-band merge
            // already happened on GitHub/ADO and our review crew lost the race.
            // Probe ancestry: when branch.tip is reachable from base, the
            // merge already landed. Reconcile local state (PrRow.Col=merged)
            // so the orchestrator's merging→complete advance fires on the next
            // tick without waiting for PrSyncJob's 2-hour cadence.
            if (await this.git.IsBranchAlreadyMergedIntoBaseAsync(repo, branch, baseBranch, ct))
            {
                var localPr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
                if (localPr is not null && localPr.Col != "merged")
                {
                    localPr.Col = "merged";
                    localPr.IsActive = false;
                    localPr.LastSyncAt = DateTimeOffset.UtcNow.ToString("O");
                    await WorkstreamMergeAdvancer.AdvanceForMergedPrAsync(control, db, prId, ct);
                    await db.SaveChangesAsync(ct);
                }
                this.logger?.LogInformation(
                    "PR {PrId} branch {Branch} already merged into {Base} (external merge); "
                    + "reconciled local state to merged, skipping provider approve/merge.",
                    prId, branch, baseBranch);
                return;
            }
            throw new EmptyPrMergeRefusedException(prId, branch, baseBranch);
        }

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
    /// <summary>
    /// Resolves the (generator, evaluator) agent pair for a workstream.
    /// Phase 88: <paramref name="workstreamCategory"/> selects between
    /// <c>crew-eng</c> (impl + plan-doc + any non-QA category) and
    /// <c>crew-qa</c> (when category equals
    /// <see cref="WorkstreamCategories.QaE2e"/>). Pre-Phase-88 callers
    /// implicitly used <c>crew-eng</c>; pass
    /// <see cref="WorkstreamCategories.Impl"/> for the legacy behavior.
    /// </summary>
    /// <remarks>
    /// Method name is preserved (rather than renamed to <c>ResolvePairAsync</c>)
    /// to keep blast radius small on tests; the doc-comment is the source of
    /// truth for the contract now that qa-e2e is routed too.
    /// </remarks>
    internal static async Task<string[]> ResolveImplPairAsync(
        ControlDbContext control,
        ProductDbContext db,
        AgentAllocationLoader allocation,
        HashSet<string> busyAgents,
        int seedOffset,
        string workstreamCategory,
        CancellationToken ct)
    {
        // Phase 88 — qa-e2e workstreams draw from crew-qa (test-attackers,
        // memory agents, ai-test-engineer) instead of crew-eng (which
        // includes refusal-prone personas like code-refactor-defender
        // that would self-veto a "write more tests" task). Pre-Phase-88,
        // the const "crew-eng" caused all qa-e2e workstreams to draw
        // from the wrong roster.
        var isQa = string.Equals(workstreamCategory, WorkstreamCategories.QaE2e, StringComparison.Ordinal);
        var prefix = isQa ? "crew-qa" : "crew-eng";
        var fallbackTeam = isQa ? "QA" : "Engineer";
        var fallbackDefaultId = isQa ? "qa-default" : "eng-default";
        var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct);
        if (crew is null)
        {
            var fallback = allocation.ForTeam(fallbackTeam).FirstOrDefault()?.Id ?? fallbackDefaultId;
            return new[] { fallback };
        }

        var evaluatorCrew = !string.IsNullOrEmpty(crew.PairedWithCrewId)
            ? await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct)
            : null;

        // Generator roster — engineer crew members. The schedule-gate
        // (on-shift filter) is the orchestrator's responsibility; for
        // story-spawn workstreams we treat all members as eligible
        // because the spawn was already through the operator's
        // explicit approval. Fall back to the lead when no members.
        // Ordered by AgentId for a stable round-robin baseline; the
        // per-claim seed below rotates the picked index.
        var genRoster = await control.CrewMembers.AsNoTracking()
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

        // LRU — prefer the least-recently-used (or never-used) engineer so
        // successive workstream claims rotate across the roster instead of
        // re-picking the same generator. Recency comes from the cost ledger
        // (agent_usage); equal-recency ties fall back to the seed shuffle.
        var genLastActive = await Forge.Api.Services.BusyAgentsHelper.LoadLastActiveAsync(control, genRoster, ct);

        var (genAgent, _) = PairDiversityResolver.PickGenerator(
            genRoster, busyAgents, crew.Lead, seed, genLastActive);

        // No free roster member AND no lead — last-resort fallback.
        if (string.IsNullOrEmpty(genAgent))
        {
            genAgent = crew.DefaultGeneratorAgentId
                ?? crew.Lead
                ?? allocation.ForTeam(fallbackTeam).FirstOrDefault()?.Id
                ?? fallbackDefaultId;
        }
        busyAgents.Add(genAgent);

        // Evaluator roster — paired crew's members (or same crew minus
        // the picked generator for a standalone crew). Phase 39 gen != eval
        // is enforced inside PickEvaluator.
        var evalRosterCrewId = evaluatorCrew?.Id ?? crew.Id;
        var evalRoster = await control.CrewMembers.AsNoTracking()
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

        // LRU — recency for the eval roster (plus the preferred eval, which
        // PickEvaluator inserts at the head even when it isn't a roster
        // member) so the evaluator rotates oldest-first; the configured
        // default now only wins equal-recency ties, not unconditionally.
        var evalRecencyIds = string.IsNullOrEmpty(preferredEval)
            ? (IReadOnlyCollection<string>)evalRoster
            : evalRoster.Append(preferredEval).ToList();
        var evalLastActive = await Forge.Api.Services.BusyAgentsHelper.LoadLastActiveAsync(control, evalRecencyIds, ct);

        var (evalAgent, _) = PairDiversityResolver.PickEvaluator(
            evalRoster, busyAgents, preferredEval, genAgent,
            seed: unchecked(Random.Shared.Next() + seedOffset), lastActiveByAgent: evalLastActive);

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
        // Phase 83 — read from the repo's plan-doc base dir (default
        // docs/stories). MUST match CrewStoryArtifactStrategy's write path or
        // this gate waits forever for docs that landed elsewhere.
        var docDir = StoryPaths.PlanDocDir(repo?.PlanDocBaseDir, spawn.StoryId);
        var paths = new[]
        {
            $"{docDir}/tech-spec.md",
            $"{docDir}/architecture.md",
            $"{docDir}/implementation-plan.md",
            $"{docDir}/e2e-scenarios.md",
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

    private async Task<bool> TryMoveExistingPrWorkstreamToReviewAsync(
        ProductDbContext db,
        IHubContext<ForgeHub>? hub,
        StorySpawnRow spawn,
        WorkstreamRow workstream,
        IReadOnlyDictionary<string, List<string>> prIdsByWorkstream,
        CancellationToken ct)
    {
        var prIds = ParseStringList(workstream.PrsJson);
        if (prIdsByWorkstream.TryGetValue(workstream.Id, out var storedPrIds))
        {
            foreach (var prId in storedPrIds)
            {
                if (!prIds.Contains(prId, StringComparer.Ordinal))
                {
                    prIds.Add(prId);
                }
            }
        }

        if (prIds.Count == 0)
        {
            return false;
        }

        workstream.PrsJson = JsonSerializer.Serialize(prIds);
        workstream.Status = "review";
        workstream.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await TryCompleteWorkItemExecutionAsync(db, workstream.Id, ct);
        await db.SaveChangesAsync(ct);
        await EmitChange(hub, "ws-pr-existing", spawn, ct);
        return true;
    }

    private static async Task<bool> TryCompleteWorkItemExecutionAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null || string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.Ordinal))
        {
            return false;
        }

        wi.ExecutionStatus = ExecutionStatuses.Completed;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Best-effort work item state update when a workstream completes.
    /// Tolerates missing rows — the orchestrator should never crash
    /// because of a stale id.
    /// </summary>
    private static async Task TryCloseWorkItemAsync(ControlDbContext control, ProductDbContext db, string workstreamId, CancellationToken ct)
    {
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workstreamId, ct);
        if (wi is null) return;
        var changed = false;
        if (!string.Equals(wi.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            wi.State = "closed";
            changed = true;
        }

        if (!string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.Ordinal))
        {
            wi.ExecutionStatus = ExecutionStatuses.Completed;
            changed = true;
        }

        if (!changed) return;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
