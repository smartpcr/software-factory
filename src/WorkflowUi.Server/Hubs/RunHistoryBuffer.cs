// -----------------------------------------------------------------------
// <copyright file="RunHistoryBuffer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hubs;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Per-run ring buffer for telemetry events. Capacity is 1000 per run.
/// Late-joiners can replay from the buffer.
/// </summary>
public class RunHistoryBuffer
{
    public const int DefaultCapacity = 1000;

    private readonly ConcurrentDictionary<Guid, RunBuffer> buffers = new();

    /// <summary>
    /// Appends a run event to the buffer, evicting the oldest if at capacity.
    /// </summary>
    public void Append(RunEvent runEvent)
    {
        var buffer = this.buffers.GetOrAdd(runEvent.RunId, _ => new RunBuffer());
        lock (buffer)
        {
            if (buffer.Events.Count >= DefaultCapacity)
            {
                buffer.Events.RemoveAt(0);
            }

            buffer.Events.Add(runEvent);
        }
    }

    /// <summary>
    /// Replays events for a run starting from the given sequence number.
    /// </summary>
    public ReplayResponse Replay(Guid runId, long fromSeq)
    {
        if (!this.buffers.TryGetValue(runId, out var buffer))
        {
            return new ReplayResponse { NotFound = true };
        }

        lock (buffer)
        {
            if (buffer.Events.Count == 0)
            {
                return new ReplayResponse { Events = [], Truncated = false };
            }

            var oldestSeq = buffer.Events[0].Seq;
            var truncated = fromSeq < oldestSeq;
            var events = buffer.Events
                .Where(e => e.Seq >= fromSeq)
                .OrderBy(e => e.Seq)
                .ToList();

            return new ReplayResponse
            {
                Events = events,
                Truncated = truncated,
                OldestAvailableSeq = oldestSeq,
            };
        }
    }

    /// <summary>
    /// Removes a run's buffer (e.g. after eviction timeout).
    /// </summary>
    public void Evict(Guid runId)
    {
        this.buffers.TryRemove(runId, out _);
    }

    /// <summary>
    /// Gets the number of tracked runs.
    /// </summary>
    public int RunCount => this.buffers.Count;

    private class RunBuffer
    {
        public List<RunEvent> Events { get; } = new(DefaultCapacity);
    }
}

/// <summary>
/// Response from a replay request.
/// </summary>
public class ReplayResponse
{
    public bool NotFound { get; set; }

    public bool Truncated { get; set; }

    public long OldestAvailableSeq { get; set; }

    public List<RunEvent> Events { get; set; } = [];
}
