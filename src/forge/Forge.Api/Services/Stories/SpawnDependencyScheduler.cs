// -----------------------------------------------------------------------
// <copyright file="SpawnDependencyScheduler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// forge_implementation.md Phase 28 — hosted service that ticks the
/// <see cref="StorySpawnOrchestrator"/> for every active spawn at a slow
/// cadence. Two responsibilities:
///
/// <list type="bullet">
/// <item>Resume in-flight spawns after server restart (CC.5).</item>
/// <item>Auto-advance downstream workstreams when an upstream PR merges (28.5.7 / 28.11.6).</item>
/// </list>
///
/// Tick is also reachable directly via <see cref="TickOnceAsync"/> so tests
/// don't need to wait for the timer.
/// </summary>
public sealed class SpawnDependencyScheduler : BackgroundService
{
    /// <summary>
    /// Phase 50 follow-up — per-step watchdog. If any single DriveAsync or
    /// ReconcileAsync exceeds this duration, the scheduler stops waiting
    /// and moves on. The orphaned task continues running on the thread
    /// pool (no cancellation — see "Phase 45 follow-up" rationale in
    /// <see cref="TickOnceAsync"/>) so an in-flight agent invocation that
    /// genuinely needs minutes to converge can still finish. The
    /// invariant this restores: ONE hung await must never prevent the
    /// next tick from running.
    ///
    /// <para>Production observation 2026-05-12 on smartpcr/qq:
    /// <c>SpawnDependencyScheduler</c> ticked exactly ONCE since process
    /// restart (verified by grepping the EF Core SQL log for the
    /// scheduler's two-column projection over story_spawns — appeared
    /// once at startup, never again over 1h+). One spawn's DriveAsync
    /// was stuck inside DriveImplementAsync, and the <c>Task.WhenAll</c>
    /// over all driveTasks blocked the whole scheduler. The qq spawn
    /// sat at phase=decompose with all 4 plan docs at verdict=pass for
    /// over an hour, requiring a manual <c>POST /spawn/retry</c> to
    /// advance.</para>
    /// </summary>
    internal static readonly TimeSpan DefaultStuckTaskTimeout = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider services;
    private readonly TimeSpan interval;
    private readonly TimeSpan stuckTaskTimeout;
    private readonly ILogger<SpawnDependencyScheduler>? logger;

    /// <summary>
    /// Phase 78 — per-spawn semaphores that prevent two scheduler ticks
    /// from concurrently driving the same spawn. Without this, a long-
    /// running dispatch in tick N's <see cref="RunWithTimeoutAsync"/>
    /// (abandoned at 60 s but still running on the thread pool per the
    /// Phase 45 follow-up rationale) would race tick N+1's fresh
    /// <see cref="StorySpawnOrchestrator.DriveAsync"/> for the same
    /// spawn — and the second caller's dispatcher entry trips the
    /// Phase 74 <c>ConcurrentDispatchRefusalSentinel</c> throw,
    /// historically surfaced as a misleading "ORCHESTRATOR HALTED"
    /// banner masking the real workstream-level failure.
    ///
    /// <para>Keyed by <c>spawnId</c>. Each tick attempts a non-blocking
    /// <see cref="SemaphoreSlim.WaitAsync(int, CancellationToken)"/>;
    /// on miss, the tick simply skips that spawn this cycle. The
    /// <c>DriveAsync</c> task itself takes care of cleanup via
    /// <c>Release()</c> in a finally block.</para>
    ///
    /// <para>Stale entries don't leak: terminated spawns just sit with
    /// a free semaphore that never gets acquired again — &lt; 100 bytes
    /// per spawn ever spawned. Acceptable for the lifetime of the
    /// process.</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> spawnLocks = new();

    public SpawnDependencyScheduler(IServiceProvider services, ILogger<SpawnDependencyScheduler>? logger = null)
        : this(services, DefaultStuckTaskTimeout, logger)
    {
    }

    /// <summary>
    /// Test-only overload — lets unit tests pass a small timeout (e.g.,
    /// <c>TimeSpan.FromMilliseconds(200)</c>) so a hung-task test doesn't
    /// have to wait the production 60s.
    /// </summary>
    internal SpawnDependencyScheduler(
        IServiceProvider services,
        TimeSpan stuckTaskTimeout,
        ILogger<SpawnDependencyScheduler>? logger = null)
    {
        this.services = services;
        this.logger = logger;
        this.interval = TimeSpan.FromSeconds(15);
        this.stuckTaskTimeout = stuckTaskTimeout;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial delay so we don't race with EnsureCreated / migrations on
        // the very first boot.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await this.TickOnceAsync(ct);
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "SpawnDependencyScheduler tick failed");
            }
            try { await Task.Delay(this.interval, ct); } catch { return; }
        }
    }

    /// <summary>One-shot tick — exposed for tests and the admin endpoint.</summary>
    public async Task TickOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<(string StoryId, string SpawnId)> active;

        // Phase 73 follow-up — story_spawns is a PRODUCT table; after the
        // split it lives in each per-product DB ({slug}.db), NOT in the
        // control DB (forge.db). Without scope context the unscoped
        // ProductDbContext falls back to forge.db whose story_spawns
        // table is an empty stub left behind by the split. Net effect:
        // the scheduler saw 0 active spawns and silently no-op'd every
        // 15 s, leaving fully-passed workstreams permanently un-PR'd.
        //
        // Fan out: enumerate products from control, push each slug into
        // the AsyncLocal slot, query that product's story_spawns,
        // aggregate. Each entry retains its story id so DriveAsync (which
        // pushes its own slug from FromStoryId) routes to the correct
        // per-product DB inside the orchestrator.
        var aggregate = new List<(string StoryId, string SpawnId)>();
        using (var ctlScope = this.services.CreateScope())
        {
            var control = ctlScope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var slugs = await control.Products
                .AsNoTracking()
                .Where(p => p.Slug != null && p.Slug != "")
                .Select(p => p.Slug!)
                .ToListAsync(ct);

            foreach (var slug in slugs)
            {
                using var slugScope = ProductDbContextFactory.PushAsyncLocalSlug(slug);
                using var dbScope = this.services.CreateScope();
                var db = dbScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                // Include Done-phase spawns: when the QA pass advances the phase
                // to "done", the WI/ProductStory closure work happens on the
                // *next* drive (DriveDoneAsync). Filtering them out here would
                // strand the spawn at done with the work item still active.
                // DriveDoneAsync short-circuits once the WI state is already
                // closed, so this remains effectively a one-shot per spawn.
                var rows = await db.StorySpawns
                    .AsNoTracking()
                    .Where(s => s.Phase != StorySpawnPhases.Paused)
                    .Select(s => new ValueTuple<string, string>(s.StoryId, s.Id))
                    .ToListAsync(ct);
                aggregate.AddRange(rows);
            }
        }
        active = aggregate;
        var orchestrator = this.services.GetRequiredService<StorySpawnOrchestrator>();
        // Phase 42 — reconciler is optional in tests; resolve once per tick.
        var reconciler = this.services.GetService<PlanReconciler>();

        // Phase 45 follow-up — drive spawns in parallel and let each one's
        // own AgentContext.WallClockBudget enforce the per-iter cap.
        //
        // Why: the previous design wrapped each DriveAsync in a 30-second
        // CancellationTokenSource, intending to keep one slow spawn from
        // starving the others. With 5 active spawns the cumulative tick
        // took ~150 seconds AND, more critically, every impl-phase agent
        // got cancelled at 30s — long before it could read a few files
        // and land an edit. That cancellation was the proximate reason
        // engineer iters never produced any commits (operator confirmed
        // 2026-05-05 by inspecting the worktree's clean `git status`
        // across 124 iters of trying).
        //
        // Fan-out to Task.WhenAll lets a fast spawn finish quickly without
        // waiting in line behind a slow neighbour. Per-spawn cancellation
        // is now the responsibility of the strategy: BudgetForPhase /
        // AgentContext.WallClockBudget bound any single agent invocation,
        // and concurrent ticks for the same spawn serialise on the
        // RealStoryGitOps branchLocks before they touch git, so the
        // worktree never gets two writers. Errors are caught per-task so
        // one spawn's exception doesn't poison the others.
        //
        // Phase 50 follow-up — Task.WhenAll only completes when EVERY
        // task completes, so a single hung Task.Run strands the entire
        // scheduler. Each step is now wrapped by RunWithTimeoutAsync
        // (production default: 60s). On timeout the scheduler logs a
        // warning, attaches a continuation that observes the orphaned
        // task's eventual outcome (no UnobservedTaskException), and
        // moves on. Crucially we do NOT cancel the underlying task —
        // the Phase 45 rationale above still stands; agent invocations
        // are allowed to run beyond 60s, they just can't strand the
        // scheduler while they do.
        var driveTasks = active.Select(item => Task.Run(async () =>
        {
            var (sid, spawnId) = item;

            // Phase 78 — per-spawn lock. Prevents two ticks driving the
            // same spawn concurrently when tick N's dispatch overran the
            // 60 s RunWithTimeoutAsync and tick N+1 picks up while the
            // orphan is still alive. The lock is acquired non-blocking;
            // on miss, this tick simply skips this spawn and the next
            // tick (15 s later) tries again. CRITICAL: when DriveAsync
            // is orphaned by the timeout, the lock release is chained to
            // the actual orphan task completion (NOT released on
            // RunWithTimeoutAsync's return) so the next tick still
            // skips the spawn while the orphan runs — that's what
            // prevents the Phase 74 concurrent-dispatch throw.
            var spawnLock = this.spawnLocks.GetOrAdd(spawnId, _ => new SemaphoreSlim(1, 1));
            if (!await spawnLock.WaitAsync(0, ct))
            {
                this.logger?.LogDebug(
                    "Skipping spawn {SpawnId} ({StoryId}) this tick — previous DriveAsync still in flight.",
                    spawnId, sid);
                return;
            }

            // Capture the work task BEFORE handing it to RunWithTimeoutAsync
            // so we can attach a release-on-completion continuation when
            // the timeout orphans it. Calling DriveAsync ONCE; the inner
            // Func<Task> just hands the same task to RunWithTimeoutAsync.
            var driveWork = orchestrator.DriveAsync(sid, ct);
            var driveOk = await this.RunWithTimeoutAsync(
                "DriveAsync", spawnId, sid,
                () => driveWork, ct);

            if (!driveOk)
            {
                // Orphaned. Lock stays held until the orphan finishes;
                // continuation runs on a background thread so we don't
                // block this tick's task. ExecuteSynchronously is safe
                // here because Release is non-blocking.
                _ = driveWork.ContinueWith(_ => spawnLock.Release(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            try
            {
                if (reconciler is not null)
                {
                    await this.RunWithTimeoutAsync(
                        "PlanReconciler", spawnId, sid,
                        () => reconciler.ReconcileAsync(spawnId, ct), ct);
                }
            }
            finally
            {
                spawnLock.Release();
            }
        }, ct)).ToArray();

        await Task.WhenAll(driveTasks);
    }

    /// <summary>
    /// Phase 50 follow-up — race <paramref name="action"/> against a
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> for
    /// <see cref="stuckTaskTimeout"/>. Returns <c>true</c> when the action
    /// completed (successfully or with a caught exception) within the
    /// budget; <c>false</c> when it timed out. On timeout, attaches a
    /// continuation that logs the orphaned task's eventual outcome and
    /// prevents <see cref="TaskScheduler.UnobservedTaskException"/> from
    /// firing if it later faults.
    /// <para>Internal so unit tests can drive the same code path with a
    /// short timeout without going through the orchestrator stack.</para>
    /// </summary>
    internal async Task<bool> RunWithTimeoutAsync(
        string what,
        string spawnId,
        string storyId,
        Func<Task> action,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        Task work;
        try
        {
            work = action();
        }
        catch (Exception ex)
        {
            // Synchronous failure before the task even started (e.g., a
            // null-deref in the strategy's setup code). Log + treat as
            // "completed unsuccessfully" so the reconciler still runs.
            this.logger?.LogWarning(ex,
                "{What} threw synchronously for spawn {SpawnId} (story {StoryId}); will retry next interval.",
                what, spawnId, storyId);
            return true;
        }

        var winner = await Task.WhenAny(work, Task.Delay(this.stuckTaskTimeout, ct));
        if (winner == work)
        {
            try
            {
                await work;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Process shutdown — propagate by returning silently.
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex,
                    "{What} failed for spawn {SpawnId} (story {StoryId}); will retry next interval.",
                    what, spawnId, storyId);
            }
            return true;
        }

        // Timeout — log + orphan the task. Background continuation observes
        // the eventual outcome so an unhandled exception inside the orphan
        // doesn't crash the process via UnobservedTaskException.
        var elapsedSec = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
        this.logger?.LogWarning(
            "{What} stuck for spawn {SpawnId} (story {StoryId}); orphaning after {ElapsedSec}s (stuck-task-timeout={TimeoutSec}s). The task continues running in the background; its eventual outcome will be logged.",
            what, spawnId, storyId, elapsedSec, (int)this.stuckTaskTimeout.TotalSeconds);
        _ = work.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    this.logger?.LogWarning(t.Exception,
                        "Orphaned {What} for spawn {SpawnId} faulted post-timeout.",
                        what, spawnId);
                }
                else if (t.IsCompletedSuccessfully)
                {
                    this.logger?.LogInformation(
                        "Orphaned {What} for spawn {SpawnId} eventually completed successfully.",
                        what, spawnId);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
    }
}
