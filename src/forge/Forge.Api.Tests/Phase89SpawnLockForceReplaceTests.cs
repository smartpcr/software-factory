// -----------------------------------------------------------------------
// <copyright file="Phase89SpawnLockForceReplaceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 89 — the per-spawn lock in <see cref="SpawnDependencyScheduler"/>
/// must auto-recover when a holder is permanently wedged. Before Phase 89,
/// once <c>spawnLocks[spawnId]</c> was held without release (e.g. an
/// orchestrator <see cref="StorySpawnOrchestrator.DriveAsync"/> deadlocked
/// inside the Copilot SDK pipe handshake), the ONLY recovery path was a
/// full process restart. On 2026-05-08 this DOS'd
/// code-intelligence:CLEAN-CODE for 17 minutes after a single Forge.Api
/// restart because the previous session's orphan stayed alive across the
/// process recycle (it didn't — but a NEW orphan from the same wedge
/// pattern re-formed within seconds).
///
/// <para>The fix: after the lock has been held past
/// <see cref="SpawnDependencyScheduler.DefaultSpawnLockForceReplaceMultiplier"/>
/// × <c>stuckTaskTimeout</c> (5 × 60 s = 5 min in production), the
/// next tick force-replaces the entry with a fresh semaphore and
/// acquires it. The abandoned semaphore is left for its wedged holder
/// to (eventually) release; that release lands on a now-orphaned
/// semaphore with no waiters and is harmless.</para>
///
/// <para>These tests drive
/// <see cref="SpawnDependencyScheduler.TryAcquireSpawnLockAsync"/>
/// directly (internal-visible to the test assembly) so the force-replace
/// policy can be exercised on millisecond budgets without standing up the
/// full orchestrator stack.</para>
/// </summary>
public sealed class Phase89SpawnLockForceReplaceTests
{
    private static SpawnDependencyScheduler NewScheduler(
        TimeSpan? stuckTaskTimeout = null,
        TimeSpan? forceReplaceThreshold = null)
    {
        // Empty service collection — TryAcquireSpawnLockAsync doesn't
        // resolve anything from DI; the IServiceProvider is just stashed
        // for use by ExecuteAsync (which we never call here).
        var services = new ServiceCollection().BuildServiceProvider();
        return new SpawnDependencyScheduler(
            services,
            stuckTaskTimeout ?? TimeSpan.FromMilliseconds(60),
            NullLogger<SpawnDependencyScheduler>.Instance,
            spawnLockForceReplaceThreshold: forceReplaceThreshold);
    }

    // ── 1. Wedged lock past threshold → force-replaced + acquired ─────────

    /// <summary>
    /// Acquire a lock, hold it past the force-replace threshold without
    /// releasing, then attempt a second acquire. Outcome must be
    /// <see cref="SpawnDependencyScheduler.SpawnLockAcquireOutcome.ForceReplaced"/>
    /// and the returned entry must be a DIFFERENT instance than the
    /// original (the wedged one is abandoned in the dict slot).
    /// </summary>
    [Fact]
    public async Task WedgedLock_HeldBeyondThreshold_IsForceReplacedOnNextAcquire()
    {
        var scheduler = NewScheduler(
            stuckTaskTimeout: TimeSpan.FromMilliseconds(20),
            forceReplaceThreshold: TimeSpan.FromMilliseconds(80));
        const string spawnId = "spawn-wedged";

        // First acquire — fresh entry, succeeds.
        var first = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.Acquired, first.Outcome);
        Assert.NotNull(first.Entry.AcquiredAt);

        // DO NOT release — simulate a wedged orchestrator drive.
        // Wait past the 80 ms threshold (use 200 ms to account for
        // scheduler jitter in CI without flaking).
        await Task.Delay(200);

        // Second acquire — same spawnId. Must force-replace.
        var second = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.ForceReplaced, second.Outcome);
        Assert.NotSame(first.Entry, second.Entry);
        Assert.NotNull(second.Entry.AcquiredAt);

        // The wedged holder's eventual Release on the OLD entry lands on
        // an abandoned semaphore (no waiters). Simulate that here to
        // verify it doesn't blow up subsequent ticks.
        first.Entry.Semaphore.Release();

        // A third acquire under the threshold against the new entry MUST
        // skip (verifies we didn't accidentally promote the abandoned
        // release into the new entry's count).
        var third = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.Skipped, third.Outcome);
        Assert.Same(second.Entry, third.Entry);
    }

    // ── 2. Lock held under threshold → next tick still skips ──────────────

    /// <summary>
    /// Counterpart: when the lock has been held LESS than the threshold,
    /// the second acquire must <see cref="SpawnDependencyScheduler.SpawnLockAcquireOutcome.Skipped"/>
    /// (do nothing) — we don't want to force-replace healthy
    /// long-running drives just because the operator happened to run a
    /// 4-minute reasoning chain. The wedged-DOS guard must NOT
    /// mis-trigger on legit slow work.
    /// </summary>
    [Fact]
    public async Task LockHeldUnderThreshold_NextAcquireSkips()
    {
        var scheduler = NewScheduler(
            stuckTaskTimeout: TimeSpan.FromSeconds(60),
            forceReplaceThreshold: TimeSpan.FromMinutes(5));
        const string spawnId = "spawn-healthy-slow";

        var first = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.Acquired, first.Outcome);

        // Don't release; second acquire immediately. Held for ~ms — far
        // under the 5-min threshold.
        var second = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.Skipped, second.Outcome);
        Assert.Same(first.Entry, second.Entry);

        // Release on the original; next acquire succeeds (entry intact).
        first.Entry.Semaphore.Release();
        first.Entry.AcquiredAt = null;

        var third = await scheduler.TryAcquireSpawnLockAsync(spawnId, CancellationToken.None);
        Assert.Equal(SpawnDependencyScheduler.SpawnLockAcquireOutcome.Acquired, third.Outcome);
        Assert.Same(first.Entry, third.Entry);
    }
}
