// -----------------------------------------------------------------------
// <copyright file="SequenceAllocator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Collections.Concurrent;

/// <summary>
/// Thread-safe, per-run monotonic sequence number allocator.
/// Each call to Next(runId) returns a value strictly greater than the previous.
/// </summary>
public class SequenceAllocator
{
    private readonly ConcurrentDictionary<Guid, StrongBox> counters = new();

    /// <summary>
    /// Returns the next sequence number for the given run, starting at 1.
    /// Thread-safe via Interlocked.Increment on a boxed counter.
    /// </summary>
    public long Next(Guid runId)
    {
        var box = this.counters.GetOrAdd(runId, _ => new StrongBox());
        return System.Threading.Interlocked.Increment(ref box.Value);
    }

    /// <summary>
    /// Returns the current (last allocated) sequence for the given run.
    /// Returns 0 if no events have been allocated for this run.
    /// </summary>
    public long Current(Guid runId)
    {
        return this.counters.TryGetValue(runId, out var box)
            ? System.Threading.Interlocked.Read(ref box.Value)
            : 0;
    }

    /// <summary>
    /// Resets the counter for a run (e.g. on eviction).
    /// </summary>
    public void Reset(Guid runId)
    {
        this.counters.TryRemove(runId, out _);
    }

    private class StrongBox
    {
        public long Value;
    }
}
