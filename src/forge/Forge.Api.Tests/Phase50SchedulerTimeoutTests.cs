// -----------------------------------------------------------------------
// <copyright file="Phase50SchedulerTimeoutTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 50 follow-up — regression-lock for the "scheduler stuck on first
/// tick" bug.
///
/// Production observation 2026-05-12 on smartpcr/qq:
/// <see cref="SpawnDependencyScheduler.TickOnceAsync"/> launches one
/// <c>Task.Run</c> per spawn and awaits <c>Task.WhenAll(driveTasks)</c>.
/// A single hung <c>DriveImplementAsync</c> for the <c>raft:RAFT-PROTOCOL</c>
/// spawn left its driveTask in a permanent await state; <c>Task.WhenAll</c>
/// therefore never completed, and the scheduler never reached its 15s
/// delay-then-loop. Verified by grepping the EF Core SQL log for the
/// scheduler's two-column projection over <c>story_spawns</c> — the query
/// appeared exactly once across 25 K log lines, at startup. Every other
/// spawn (including <c>qq:TELEGRAM-MESSENGER-S</c> sitting at
/// <c>phase=decompose</c> with all 4 plan docs at verdict=pass) was
/// stranded for 1h+ until the operator triggered <c>POST /spawn/retry</c>
/// manually.
///
/// <para>The fix wraps each <c>DriveAsync</c> and <c>ReconcileAsync</c>
/// call in <see cref="SpawnDependencyScheduler.RunWithTimeoutAsync"/> —
/// <c>Task.WhenAny(work, Task.Delay(stuckTaskTimeout))</c>. On timeout
/// the scheduler logs + moves on; the orphaned task continues running on
/// the thread pool so an in-flight agent invocation still finishes, but a
/// hung await never strands the other spawns or the next tick.</para>
///
/// These tests exercise <c>RunWithTimeoutAsync</c> directly with a short
/// (200 ms) timeout so they run fast and don't require the orchestrator
/// stack.
/// </summary>
public class Phase50SchedulerTimeoutTests
{
    private static SpawnDependencyScheduler NewScheduler(TimeSpan timeout)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new SpawnDependencyScheduler(services, timeout, NullLogger<SpawnDependencyScheduler>.Instance);
    }

    /// <summary>
    /// The headline regression test: when the action under
    /// <c>RunWithTimeoutAsync</c> hangs forever, the helper MUST return
    /// <c>false</c> after the configured timeout instead of awaiting
    /// indefinitely. Without this, one hung spawn strands the entire
    /// scheduler's <c>Task.WhenAll(driveTasks)</c> and no subsequent tick
    /// ever fires.
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_HungTask_ReturnsFalse_WithinBudget()
    {
        var scheduler = NewScheduler(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Action that never completes (uses cancellation-less Task.Delay so
        // even if scheduler.RunWithTimeoutAsync's ct propagates, the
        // helper's WhenAny is what races against the inner delay; this
        // approximates the production hang where DriveImplementAsync gets
        // stuck inside a child await that doesn't honor the scheduler ct).
        var result = await scheduler.RunWithTimeoutAsync(
            what: "DriveAsync",
            spawnId: "spawn-hang",
            storyId: "story-hang",
            action: () => Task.Delay(TimeSpan.FromSeconds(30)),
            ct: CancellationToken.None);

        sw.Stop();
        Assert.False(result, "RunWithTimeoutAsync should return false when the action exceeds the timeout.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"RunWithTimeoutAsync took {sw.ElapsedMilliseconds}ms — should have returned around the 200ms timeout, not the 30s action delay.");
    }

    /// <summary>
    /// Sibling-case: when the action completes quickly, the helper MUST
    /// observe its successful completion and return <c>true</c>. This is
    /// the steady-state path (DriveAsync usually returns in milliseconds);
    /// breaking it would silently lose all forward progress.
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_FastTask_ReturnsTrue()
    {
        var scheduler = NewScheduler(TimeSpan.FromSeconds(5));

        var result = await scheduler.RunWithTimeoutAsync(
            what: "DriveAsync",
            spawnId: "spawn-fast",
            storyId: "story-fast",
            action: () => Task.CompletedTask,
            ct: CancellationToken.None);

        Assert.True(result, "RunWithTimeoutAsync should return true when the action completes within the timeout.");
    }

    /// <summary>
    /// Sibling-case: when the action throws asynchronously (e.g., a
    /// database exception inside DriveAsync), the helper MUST catch it
    /// and return <c>true</c> so the per-tick reconciler still runs and
    /// the scheduler proceeds. This mirrors the old behavior of the
    /// inline <c>try/catch (Exception ex)</c> block, just routed through
    /// the new helper.
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_AsyncException_IsCaught_AndReturnsTrue()
    {
        var scheduler = NewScheduler(TimeSpan.FromSeconds(5));

        var result = await scheduler.RunWithTimeoutAsync(
            what: "DriveAsync",
            spawnId: "spawn-throws",
            storyId: "story-throws",
            action: async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("simulated DriveAsync failure");
            },
            ct: CancellationToken.None);

        Assert.True(result, "Async exception should be caught and return true so the reconciler still runs this tick.");
    }

    /// <summary>
    /// Sibling-case: when the action throws SYNCHRONOUSLY before returning
    /// a Task (e.g., a null-deref in the strategy's setup before its
    /// first await), the helper MUST catch the throw and return
    /// <c>true</c>. Without this guard, <see cref="TickOnceAsync"/>'s
    /// per-spawn body would propagate the synchronous exception and bypass
    /// the reconciler step.
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_SynchronousException_IsCaught_AndReturnsTrue()
    {
        var scheduler = NewScheduler(TimeSpan.FromSeconds(5));

        var result = await scheduler.RunWithTimeoutAsync(
            what: "DriveAsync",
            spawnId: "spawn-sync-throw",
            storyId: "story-sync-throw",
            action: () => throw new InvalidOperationException("simulated synchronous failure"),
            ct: CancellationToken.None);

        Assert.True(result, "Synchronous exception should be caught and return true so the reconciler still runs this tick.");
    }

    /// <summary>
    /// Parallel hung-task isolation: when multiple instances of the
    /// helper run concurrently (mirroring the scheduler's per-spawn
    /// Task.Run fan-out), a hang in one MUST NOT delay completion of
    /// the others beyond the timeout. This is the property that makes
    /// the production tick's Task.WhenAll proceed instead of blocking
    /// on a single stranded spawn.
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_HungSibling_DoesNotDelayFastSibling()
    {
        var scheduler = NewScheduler(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var hung = scheduler.RunWithTimeoutAsync(
            what: "DriveAsync", spawnId: "spawn-A", storyId: "story-A",
            action: () => Task.Delay(TimeSpan.FromSeconds(30)),
            ct: CancellationToken.None);
        var fast = scheduler.RunWithTimeoutAsync(
            what: "DriveAsync", spawnId: "spawn-B", storyId: "story-B",
            action: () => Task.CompletedTask,
            ct: CancellationToken.None);

        // Sanity-check the fast one resolves while the hung one is still
        // waiting on its timeout (Task.WhenAll on both should be bounded
        // by the timeout, not by the 30s hung action).
        await Task.WhenAll(hung, fast);
        sw.Stop();

        Assert.True(await fast, "Fast task should succeed normally.");
        Assert.False(await hung, "Hung task should time out and return false.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Parallel WhenAll took {sw.ElapsedMilliseconds}ms — fast task should not have been delayed by hung sibling beyond the 300ms timeout.");
    }

    /// <summary>
    /// Cancellation propagation: when the outer <c>CancellationToken</c>
    /// is signalled mid-execution, the helper MUST NOT log a noisy
    /// "failed" warning — process-shutdown OCEs are silently swallowed
    /// (matches the production scheduler's <c>OperationCanceledException
    /// when (ct.IsCancellationRequested)</c> filter, just routed through
    /// the new helper).
    /// </summary>
    [Fact]
    public async Task RunWithTimeout_OperationCancelled_OnShutdown_ReturnsTrue()
    {
        var scheduler = NewScheduler(TimeSpan.FromSeconds(5));
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        var result = await scheduler.RunWithTimeoutAsync(
            what: "DriveAsync",
            spawnId: "spawn-shutdown",
            storyId: "story-shutdown",
            action: () => Task.FromCanceled(cts.Token),
            ct: cts.Token);

        Assert.True(result, "Shutdown-triggered cancellation should be swallowed silently and the helper should still return true.");
    }
}
