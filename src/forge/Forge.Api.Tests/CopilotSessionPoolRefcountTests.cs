// -----------------------------------------------------------------------
// <copyright file="CopilotSessionPoolRefcountTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
using Xunit;

/// <summary>
/// Phase 74 Fix 2 — verifies <see cref="PooledCopilotEntry"/> reference
/// counting. The pool's <see cref="CopilotSessionPool.AcquireAsync"/>
/// / <see cref="CopilotSessionPool.ReleaseAsync"/> path is tested at
/// the <see cref="PooledCopilotEntry"/> level (internal API exposed
/// to this assembly via <c>InternalsVisibleTo</c>) because the
/// CopilotClient / CopilotSession factories require a real SDK
/// runtime which the unit-test layer does not stand up.
/// </summary>
public class CopilotSessionPoolRefcountTests
{
    [Fact]
    public void NewEntry_HasZeroRefCount()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        Assert.Equal(0, entry.RefCount);
    }

    [Fact]
    public void IncrementRefCount_BumpsCount()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        entry.IncrementRefCount();
        Assert.Equal(1, entry.RefCount);
        entry.IncrementRefCount();
        Assert.Equal(2, entry.RefCount);
    }

    [Fact]
    public void DecrementRefCount_ReturnsFalseAboveZero_TrueAtZero()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        entry.IncrementRefCount();
        entry.IncrementRefCount();
        // First decrement: still 1 — keep alive.
        Assert.False(entry.DecrementRefCount());
        Assert.Equal(1, entry.RefCount);
        // Second decrement: hits zero — caller must dispose.
        Assert.True(entry.DecrementRefCount());
        Assert.Equal(0, entry.RefCount);
    }

    [Fact]
    public void DecrementRefCount_BelowZero_NoOp()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        Assert.False(entry.DecrementRefCount());
        Assert.Equal(0, entry.RefCount);
    }

    [Fact]
    public async Task DisposeAsync_BlocksFurtherIncrement()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        await entry.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => entry.IncrementRefCount());
    }

    [Fact]
    public async Task DisposeAsync_OnRefcountZero_IsIdempotent()
    {
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        await entry.DisposeAsync();
        await entry.DisposeAsync(); // second call must not throw
    }

    [Fact]
    public async Task TwoAcquiresOneRelease_LeavesAlive()
    {
        // Direct refcount manipulation mirrors what
        // CopilotSessionPool.AcquireAsync / ReleaseAsync do under the
        // hood. Pinning the bare semantics here so the pool's wiring
        // stays honest as it evolves.
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        entry.IncrementRefCount();
        entry.IncrementRefCount();
        var shouldDispose = entry.DecrementRefCount();
        Assert.False(shouldDispose);
        Assert.Equal(1, entry.RefCount);

        // Still usable for the remaining acquirer.
        Assert.False(((dynamic)entry).RefCount <= 0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentIncrementThenConcurrentDecrement_LandsAtZero_WithExactlyOneDisposeTrigger()
    {
        // Production lifecycle: N callers Acquire (Increment) concurrently
        // before any Release (Decrement) fires. The pool's
        // ConcurrentDictionary ensures all N callers see the same entry.
        // Then N Releases fire concurrently; exactly one of them must
        // see DecrementRefCount return true (the one that takes the
        // count from 1 to 0). The interleaved "inc+dec on the same
        // thread tight loop" pattern was a TEST artifact, not a real
        // dispatch pattern — production code always sequences Acquire
        // → use → Release per caller, with no thread doing both ends.
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        const int N = 64;

        // Phase 1: concurrent increments.
        var incTasks = Enumerable.Range(0, N).Select(_ => Task.Run(() => entry.IncrementRefCount())).ToArray();
        await Task.WhenAll(incTasks);
        Assert.Equal(N, entry.RefCount);

        // Phase 2: concurrent decrements.
        var trueReturns = 0;
        var decTasks = Enumerable.Range(0, N).Select(_ => Task.Run(() =>
        {
            if (entry.DecrementRefCount())
            {
                System.Threading.Interlocked.Increment(ref trueReturns);
            }
        })).ToArray();
        await Task.WhenAll(decTasks);

        Assert.Equal(0, entry.RefCount);
        // Exactly one decrement must have crossed to zero. The contract
        // is "true iff this call brought the count to zero from
        // positive". Stronger than ">=1" because two trueReturns would
        // mean a race in the refcount itself (count went below zero
        // then back up — semantically wrong).
        Assert.Equal(1, trueReturns);
    }

    [Fact]
    public async Task IncrementRefCount_AfterDecrementToZero_Throws()
    {
        // Cross-check: once the count crossed to zero, IncrementRefCount
        // throws ObjectDisposedException — this is the contract that
        // prevents use-after-free on the shared session when a racing
        // caller acquires AFTER the pool has decided to dispose.
        var entry = new PooledCopilotEntry(client: null!, session: null!, "ws-1", "agent-g");
        entry.IncrementRefCount();
        Assert.True(entry.DecrementRefCount());
        Assert.Throws<ObjectDisposedException>(() => entry.IncrementRefCount());
        await Task.CompletedTask;
    }
}
