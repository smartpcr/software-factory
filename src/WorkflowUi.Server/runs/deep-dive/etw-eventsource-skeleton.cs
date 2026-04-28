// -----------------------------------------------------------------------
// <copyright file="WorkflowUiServerEventSource.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Diagnostics.Tracing;

/// <summary>
/// ETW EventSource for <c>WorkflowUi.Server</c>. Produces ETL events that the
/// <c>EtlIterator</c> pipeline ingests into Hub Kusto tables named
/// <c>['ETL-Microsoft-WorkflowUi-Server.{EventName}']</c>.
/// <para>
/// Event methods map 1:1 to the query targets defined in the investigation:
///   Q01f → RunFailed, Q02f → RequestStop, Q03f → UnhandledException,
///   Q04f → NodeFailed, Q05f → AuditEvent, Q06f → ClientAttached,
///   Q07f → RunCompleted.
/// </para>
/// </summary>
[EventSource(Name = "Microsoft-WorkflowUi-Server")]
public sealed class WorkflowUiServerEventSource : EventSource
{
    /// <summary>
    /// Singleton instance. Use <c>WorkflowUiServerEventSource.Log</c> to emit events.
    /// </summary>
    public static readonly WorkflowUiServerEventSource Log = new();

    private WorkflowUiServerEventSource()
    {
    }

    // ------------------------------------------------------------------
    //  Event 1 — RunFailed
    //  Table:   ETL-Microsoft-WorkflowUi-Server.RunFailed
    //  Query:   Q01f (score 32)
    //  Source:  RunManager — workflow failure path
    //  Columns: runId, errorMessage, duration
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when a workflow run ends with an unhandled exception.
    /// </summary>
    [Event(1, Level = EventLevel.Error, Message = "Run {0} failed after {2}ms: {1}")]
    public void RunFailed(string runId, string errorMessage, long durationMs)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(1, runId, errorMessage, durationMs);
        }
    }

    // ------------------------------------------------------------------
    //  Event 2 — RunCompleted
    //  Table:   ETL-Microsoft-WorkflowUi-Server.RunCompleted
    //  Query:   Q07f (score 22)
    //  Source:  RunManager — workflow completion path
    //  Columns: runId, status, duration
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when a workflow run completes (any terminal status).
    /// </summary>
    [Event(2, Level = EventLevel.Informational, Message = "Run {0} completed with status {1} in {2}ms")]
    public void RunCompleted(string runId, string status, long durationMs)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(2, runId, status, durationMs);
        }
    }

    // ------------------------------------------------------------------
    //  Event 3 — NodeFailed
    //  Table:   ETL-Microsoft-WorkflowUi-Server.NodeFailed
    //  Query:   Q04f (score 28)
    //  Source:  EngineEventBridge.HandleAsync → RunEventKind.NodeFailed
    //  Columns: nodeId, nodeName, runId
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when a single DAG node fails during execution.
    /// </summary>
    [Event(3, Level = EventLevel.Error, Message = "Node {1} ({0}) failed in run {2}: {3}")]
    public void NodeFailed(string nodeId, string nodeName, string runId, string errorMessage)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(3, nodeId, nodeName, runId, errorMessage);
        }
    }

    // ------------------------------------------------------------------
    //  Event 4 — UnhandledException
    //  Table:   ETL-Microsoft-WorkflowUi-Server.UnhandledException
    //  Query:   Q03f (score 32)
    //  Source:  ProblemDetailsWriter.UseProblemDetailsExceptionHandler
    //  Columns: correlationId, exceptionType, message
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when an unhandled exception reaches the global error handler.
    /// </summary>
    [Event(4, Level = EventLevel.Critical, Message = "Unhandled exception [{1}] for {0}: {2}")]
    public void UnhandledException(string correlationId, string exceptionType, string message)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(4, correlationId, exceptionType, message);
        }
    }

    // ------------------------------------------------------------------
    //  Event 5 — RequestStop
    //  Table:   ETL-Microsoft-WorkflowUi-Server.RequestStop
    //  Query:   Q02f (score 20)
    //  Source:  RequestLoggingEnricher.InvokeAsync
    //  Columns: method, path, statusCode, elapsedMs
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when an HTTP request completes, recording method, path, status, and duration.
    /// </summary>
    [Event(5, Level = EventLevel.Informational, Message = "HTTP {0} {1} -> {2} in {3}ms")]
    public void RequestStop(string method, string path, int statusCode, long elapsedMs)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(5, method, path, statusCode, elapsedMs);
        }
    }

    // ------------------------------------------------------------------
    //  Event 6 — AuditEvent
    //  Table:   ETL-Microsoft-WorkflowUi-Server.AuditEvent
    //  Query:   Q05f (score 18)
    //  Source:  AuditMiddleware.InvokeAsync
    //  Columns: action, path, userId, outcomeCode
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted for every mutating HTTP operation (POST, PUT, PATCH, DELETE).
    /// </summary>
    [Event(6, Level = EventLevel.Informational, Message = "Audit: {0} {1} by {2} -> {3}")]
    public void AuditEvent(string action, string path, string userId, int outcomeCode)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(6, action, path, userId, outcomeCode);
        }
    }

    // ------------------------------------------------------------------
    //  Event 7 — ClientAttached
    //  Table:   ETL-Microsoft-WorkflowUi-Server.ClientAttached
    //  Query:   Q06f (score 18)
    //  Source:  TelemetryHub.AttachRun
    //  Columns: runId, connectionId
    // ------------------------------------------------------------------

    /// <summary>
    /// Emitted when a SignalR client attaches to a workflow run's event stream.
    /// </summary>
    [Event(7, Level = EventLevel.Informational, Message = "Client {1} attached to run {0}")]
    public void ClientAttached(string runId, string connectionId)
    {
        if (this.IsEnabled())
        {
            this.WriteEvent(7, runId, connectionId);
        }
    }
}
