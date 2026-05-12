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
        using (var scope = this.services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            // Include Done-phase spawns: when the QA pass advances the phase
            // to "done", the WI/ProductStory closure work happens on the
            // *next* drive (DriveDoneAsync). Filtering them out here would
            // strand the spawn at done with the work item still active.
            // DriveDoneAsync short-circuits once the WI state is already
            // closed, so this remains effectively a one-shot per spawn.
            active = await db.StorySpawns
                .AsNoTracking()
                .Select(s => new ValueTuple<string, string>(s.StoryId, s.Id))
                .ToListAsync(ct);
        }
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
            var driveOk = await this.RunWithTimeoutAsync(
                "DriveAsync", spawnId, sid,
                () => orchestrator.DriveAsync(sid, ct), ct);

            // If DriveAsync timed out, skip the reconciler for this tick.
            // The next tick will pick up a fresh PlanReconciler invocation
            // against whatever DriveAsync eventually persists (if it ever
            // returns); racing them in the same tick post-timeout would
            // burn CPU on stale state.
            if (!driveOk) return;

            if (reconciler is not null)
            {
                await this.RunWithTimeoutAsync(
                    "PlanReconciler", spawnId, sid,
                    () => reconciler.ReconcileAsync(spawnId, ct), ct);
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
