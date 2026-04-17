// -----------------------------------------------------------------------
// <copyright file="TelemetryHubSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hubs;

using Microsoft.AspNetCore.SignalR;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// <see cref="IRunEventSink"/> implementation that publishes events
/// through the SignalR <see cref="TelemetryHub"/> to per-run groups,
/// and appends to the <see cref="RunHistoryBuffer"/>.
/// </summary>
public class TelemetryHubSink : IRunEventSink
{
    private readonly IHubContext<TelemetryHub, ITelemetryClient> hubContext;
    private readonly RunHistoryBuffer historyBuffer;

    public TelemetryHubSink(
        IHubContext<TelemetryHub, ITelemetryClient> hubContext,
        RunHistoryBuffer historyBuffer)
    {
        this.hubContext = hubContext;
        this.historyBuffer = historyBuffer;
    }

    /// <inheritdoc/>
    public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        this.historyBuffer.Append(runEvent);
        await this.hubContext.Clients
            .Group(runEvent.RunId.ToString())
            .ReceiveRunEvent(runEvent);
    }
}
