// -----------------------------------------------------------------------
// <copyright file="ITelemetryClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hubs;

using WorkflowUi.Server.Telemetry;

/// <summary>
/// Strongly-typed SignalR client contract for telemetry events.
/// </summary>
public interface ITelemetryClient
{
    /// <summary>
    /// Receives a run event from the hub.
    /// </summary>
    Task ReceiveRunEvent(RunEvent runEvent);
}
