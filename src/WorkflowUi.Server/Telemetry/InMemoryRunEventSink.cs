// -----------------------------------------------------------------------
// <copyright file="InMemoryRunEventSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System.Collections.Concurrent;

/// <summary>
/// Test double for <see cref="IRunEventSink"/> that appends events to a concurrent queue.
/// </summary>
public class InMemoryRunEventSink : IRunEventSink
{
    public ConcurrentQueue<RunEvent> Events { get; } = new();

    public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        this.Events.Enqueue(runEvent);
        return Task.CompletedTask;
    }
}
