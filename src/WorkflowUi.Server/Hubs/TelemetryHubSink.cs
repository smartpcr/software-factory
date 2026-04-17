// -----------------------------------------------------------------------
// <copyright file="TelemetryHubSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WorkflowUi.Server.Persistence;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// <see cref="IRunEventSink"/> implementation that publishes events
/// through the SignalR <see cref="TelemetryHub"/> to per-run groups,
/// appends to the <see cref="RunHistoryBuffer"/>, and persists to JSONL via
/// <see cref="RunHistoryWriter"/>.
/// </summary>
public class TelemetryHubSink : IRunEventSink
{
    private readonly IHubContext<TelemetryHub, ITelemetryClient> hubContext;
    private readonly RunHistoryBuffer historyBuffer;
    private readonly RunHistoryWriter historyWriter;
    private readonly ILogger<TelemetryHubSink> logger;

    public TelemetryHubSink(
        IHubContext<TelemetryHub, ITelemetryClient> hubContext,
        RunHistoryBuffer historyBuffer,
        RunHistoryWriter historyWriter,
        ILogger<TelemetryHubSink> logger)
    {
        this.hubContext = hubContext;
        this.historyBuffer = historyBuffer;
        this.historyWriter = historyWriter;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        this.historyBuffer.Append(runEvent);

        try
        {
            await this.historyWriter.AppendAsync(runEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to persist run event {RunId}/{Seq} to JSONL",
                runEvent.RunId, runEvent.Seq);
        }

        await this.hubContext.Clients
            .Group(runEvent.RunId.ToString())
            .ReceiveRunEvent(runEvent);
    }
}
