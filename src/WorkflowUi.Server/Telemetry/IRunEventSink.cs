// -----------------------------------------------------------------------
// <copyright file="IRunEventSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

/// <summary>
/// Abstraction for receiving mapped run events from the bridge.
/// Implementations: InMemoryRunEventSink (test), TelemetryHubSink (SignalR).
/// </summary>
public interface IRunEventSink
{
    /// <summary>
    /// Publishes a run event to downstream consumers.
    /// </summary>
    Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken = default);
}
