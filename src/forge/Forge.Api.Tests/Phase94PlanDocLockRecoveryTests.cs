// -----------------------------------------------------------------------
// <copyright file="Phase94PlanDocLockRecoveryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-06-23 — the per-doc production lock
/// (<see cref="CrewStoryArtifactStrategy.PlanDocProductionLocks"/>) recovers
/// from a hung holder via a progress heartbeat, AND now cancels the hung
/// holder's drive so it actually STOPS instead of running on after being
/// bypassed. Before this, a drive that hung while holding the lock (a runaway
/// Copilot child the inactivity watchdog couldn't see, on a background drive
/// launched with CancellationToken.None) wedged the doc: every orchestrator
/// tick threw <see cref="PlanDocInFlightException"/> ("skipped (in-flight)")
/// and only a manual server restart cleared it (observed on
/// code-intelligence:ENABLE-MCP architecture.md). These exercise the extracted
/// <see cref="CrewStoryArtifactStrategy.AcquirePlanDocProductionLockAsync"/>.
/// </summary>
public class Phase94PlanDocLockRecoveryTests
{
    private const string Doc = "plan-doc:spawn-x:architecture.md";
    private const string RepoPath = "docs/stories/x/architecture.md";
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxDrive = TimeSpan.FromHours(1);

    public Phase94PlanDocLockRecoveryTests() =>
        CrewStoryArtifactStrategy.ResetPlanDocProductionLocksForTesting();

    private static Task<CrewStoryArtifactStrategy.PlanDocLock> AcquireAsync() =>
        CrewStoryArtifactStrategy.AcquirePlanDocProductionLockAsync(
            Doc, RepoPath, Stale, MaxDrive, logger: null, ct: CancellationToken.None);

    [Fact]
    public async Task FirstAcquire_Succeeds_StampsHeartbeat_AndArmsCts()
    {
        var entry = await AcquireAsync();

        Assert.NotNull(entry);
        Assert.NotEqual(0, entry.HeartbeatTicks);          // acquire stamps a heartbeat
        Assert.Equal(0, entry.Sem.CurrentCount);            // held
        Assert.NotNull(entry.Cts);                          // drive cancellation armed
        Assert.False(entry.Cts!.IsCancellationRequested);
    }

    [Fact]
    public async Task SecondAcquire_WhileHeldAndFresh_ThrowsInFlight()
    {
        var first = await AcquireAsync();

        // Holder is live (just stamped a heartbeat) → contender bails transiently.
        await Assert.ThrowsAsync<PlanDocInFlightException>(AcquireAsync);

        Assert.Equal(0, first.Sem.CurrentCount);            // original holder still owns it
        Assert.False(first.Cts!.IsCancellationRequested);   // and is NOT cancelled
    }

    [Fact]
    public async Task SecondAcquire_WhenHeartbeatStale_RecoversAndCancelsHungHolder()
    {
        var first = await AcquireAsync();

        // Simulate a HUNG holder: backdate the heartbeat past the staleness
        // window (a live holder would have refreshed it each iter).
        first.HeartbeatTicks = DateTimeOffset.UtcNow.AddMinutes(-31).Ticks;

        var recovered = await AcquireAsync();

        Assert.NotSame(first, recovered);                   // a brand-new lock entry
        Assert.Equal(0, recovered.Sem.CurrentCount);        // contender holds the fresh lock
        Assert.False(recovered.Cts!.IsCancellationRequested);

        // The crux of the "make it actually stop" fix: the hung holder's drive
        // token is now cancelled, so ProducePlanFileAsync's awaits throw and the
        // drive exits instead of running on.
        Assert.True(first.Cts!.IsCancellationRequested);

        // The hung holder's eventual release must not corrupt the new lock —
        // it lands on the orphaned semaphore.
        first.Sem.Release();
        Assert.Equal(0, recovered.Sem.CurrentCount);        // fresh lock still held
    }

    [Fact]
    public async Task AfterRelease_NextAcquire_Succeeds_WithFreshCts()
    {
        var first = await AcquireAsync();
        var firstCts = first.Cts;
        first.Sem.Release();                                // healthy drive finished

        var second = await AcquireAsync();
        Assert.Same(first, second);                         // same entry, re-taken
        Assert.Equal(0, second.Sem.CurrentCount);
        Assert.NotSame(firstCts, second.Cts);               // a fresh CTS for the new drive
        Assert.False(second.Cts!.IsCancellationRequested);
    }
}
