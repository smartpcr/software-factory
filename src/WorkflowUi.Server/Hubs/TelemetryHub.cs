// -----------------------------------------------------------------------
// <copyright file="TelemetryHub.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hubs;

using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

/// <summary>
/// SignalR hub for streaming telemetry events to per-run groups.
/// Clients call AttachRun/DetachRun to subscribe to specific workflow runs.
/// </summary>
public class TelemetryHub : Hub<ITelemetryClient>
{
    private static readonly ConcurrentDictionary<string, HashSet<Guid>> ConnectionRuns = new();
    private readonly RunHistoryBuffer historyBuffer;
    private readonly ILogger<TelemetryHub> logger;

    public TelemetryHub(RunHistoryBuffer historyBuffer, ILogger<TelemetryHub> logger)
    {
        this.historyBuffer = historyBuffer;
        this.logger = logger;
    }

    /// <summary>
    /// Subscribes the calling client to a run's event group.
    /// </summary>
    public async Task AttachRun(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new HubException("runId must not be empty.");
        }

        await this.Groups.AddToGroupAsync(this.Context.ConnectionId, runId.ToString());

        var runs = ConnectionRuns.GetOrAdd(this.Context.ConnectionId, _ => new HashSet<Guid>());
        lock (runs)
        {
            runs.Add(runId);
        }

        this.logger.LogInformation(
            "Client {ConnectionId} attached to run {RunId}",
            this.Context.ConnectionId,
            runId);
    }

    /// <summary>
    /// Unsubscribes the calling client from a run's event group.
    /// </summary>
    public async Task DetachRun(Guid runId)
    {
        await this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, runId.ToString());

        if (ConnectionRuns.TryGetValue(this.Context.ConnectionId, out var runs))
        {
            lock (runs)
            {
                runs.Remove(runId);
            }
        }

        this.logger.LogInformation(
            "Client {ConnectionId} detached from run {RunId}",
            this.Context.ConnectionId,
            runId);
    }

    /// <summary>
    /// Replays buffered events for a run starting from a given sequence number.
    /// </summary>
    public ReplayResponse Replay(Guid runId, long fromSeq)
    {
        return this.historyBuffer.Replay(runId, fromSeq);
    }

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectionRuns.TryRemove(this.Context.ConnectionId, out var runs))
        {
            HashSet<Guid> snapshot;
            lock (runs)
            {
                snapshot = new HashSet<Guid>(runs);
            }

            foreach (var runId in snapshot)
            {
                await this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, runId.ToString());
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Gets the runs a connection is attached to (for testing).
    /// </summary>
    internal static IReadOnlySet<Guid> GetConnectionRuns(string connectionId)
    {
        if (ConnectionRuns.TryGetValue(connectionId, out var runs))
        {
            lock (runs)
            {
                return new HashSet<Guid>(runs);
            }
        }

        return new HashSet<Guid>();
    }
}
