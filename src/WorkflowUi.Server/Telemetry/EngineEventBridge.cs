// -----------------------------------------------------------------------
// <copyright file="EngineEventBridge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Collections.Generic;
using System.Xml;
using ExecutionEngine.Events;
using Microsoft.Extensions.Logging;

/// <summary>
/// Adapts engine CLR events into transport-neutral <see cref="RunEvent"/> DTOs
/// and hands them to an <see cref="IRunEventSink"/>.
/// This is the single point where engine truth is translated into UI truth.
/// </summary>
public class EngineEventBridge
{
    private readonly IRunEventSink sink;
    private readonly SequenceAllocator sequenceAllocator;
    private readonly ILogger<EngineEventBridge> logger;

    public EngineEventBridge(
        IRunEventSink sink,
        SequenceAllocator sequenceAllocator,
        ILogger<EngineEventBridge> logger)
    {
        this.sink = sink;
        this.sequenceAllocator = sequenceAllocator;
        this.logger = logger;
    }

    /// <summary>
    /// Maps a single engine event to a <see cref="RunEvent"/> and publishes it.
    /// Unknown event types are silently logged and ignored (never thrown).
    /// </summary>
    public async Task HandleAsync(WorkflowEvent engineEvent, CancellationToken ct = default)
    {
        var runEvent = this.MapEvent(engineEvent);
        if (runEvent == null)
        {
            this.logger.LogWarning(
                "Unknown engine event type {EventType} ignored",
                engineEvent.GetType().Name);
            return;
        }

        runEvent.Seq = this.sequenceAllocator.Next(runEvent.RunId);
        await this.sink.PublishAsync(runEvent, ct);
    }

    /// <summary>
    /// Maps an engine event to a RunEvent. Returns null for unknown types.
    /// </summary>
    internal RunEvent? MapEvent(WorkflowEvent engineEvent)
    {
        return engineEvent switch
        {
            WorkflowStartedEvent e => this.MapWorkflowStarted(e),
            WorkflowCompletedEvent e => this.MapWorkflowCompleted(e),
            WorkflowFailedEvent e => this.MapWorkflowFailed(e),
            WorkflowCancelledEvent e => this.MapWorkflowCancelled(e),
            NodeStartedEvent e => this.MapNodeStarted(e),
            NodeCompletedEvent e => this.MapNodeCompleted(e),
            NodeFailedEvent e => this.MapNodeFailed(e),
            NodeCancelledEvent e => this.MapNodeCancelled(e),
            NodeProgressEvent e => this.MapNodeProgress(e),
            _ => null,
        };
    }

    private RunEvent MapWorkflowStarted(WorkflowStartedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.WorkflowStarted,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            Payload = new Dictionary<string, object>
            {
                ["workflowId"] = e.WorkflowId,
                ["workflowName"] = e.WorkflowName,
                ["totalNodes"] = e.TotalNodes,
            },
        };
    }

    private RunEvent MapWorkflowCompleted(WorkflowCompletedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.WorkflowCompleted,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            Payload = new Dictionary<string, object>
            {
                ["workflowId"] = e.WorkflowId,
                ["duration"] = XmlConvert.ToString(e.Duration),
                ["status"] = e.Status.ToString(),
            },
        };
    }

    private RunEvent MapWorkflowFailed(WorkflowFailedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.WorkflowFailed,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            Payload = new Dictionary<string, object>
            {
                ["workflowId"] = e.WorkflowId,
                ["duration"] = XmlConvert.ToString(e.Duration),
                ["errorMessage"] = e.ErrorMessage,
            },
        };
    }

    private RunEvent MapWorkflowCancelled(WorkflowCancelledEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.WorkflowCancelled,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            Payload = new Dictionary<string, object>
            {
                ["workflowId"] = e.WorkflowId,
                ["duration"] = XmlConvert.ToString(e.Duration),
                ["reason"] = e.Reason,
            },
        };
    }

    private RunEvent MapNodeStarted(NodeStartedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.NodeStarted,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            NodeId = e.NodeId,
            NodeName = e.NodeName,
            Payload = new Dictionary<string, object>
            {
                ["nodeInstanceId"] = e.NodeInstanceId.ToString(),
            },
        };
    }

    private RunEvent MapNodeCompleted(NodeCompletedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.NodeCompleted,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            NodeId = e.NodeId,
            NodeName = e.NodeName,
            Payload = new Dictionary<string, object>
            {
                ["nodeInstanceId"] = e.NodeInstanceId.ToString(),
                ["duration"] = XmlConvert.ToString(e.Duration),
            },
        };
    }

    private RunEvent MapNodeFailed(NodeFailedEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.NodeFailed,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            NodeId = e.NodeId,
            NodeName = e.NodeName,
            Payload = new Dictionary<string, object>
            {
                ["nodeInstanceId"] = e.NodeInstanceId.ToString(),
                ["errorMessage"] = e.ErrorMessage,
                ["duration"] = XmlConvert.ToString(e.Duration),
            },
        };
    }

    private RunEvent MapNodeCancelled(NodeCancelledEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.NodeCancelled,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            NodeId = e.NodeId,
            NodeName = e.NodeName,
            Payload = new Dictionary<string, object>
            {
                ["nodeInstanceId"] = e.NodeInstanceId.ToString(),
                ["reason"] = e.Reason,
            },
        };
    }

    private RunEvent MapNodeProgress(NodeProgressEvent e)
    {
        return new RunEvent
        {
            RunId = e.WorkflowInstanceId,
            Kind = RunEventKind.NodeProgress,
            Synthetic = false,
            TimestampUtc = e.Timestamp,
            NodeId = e.NodeId,
            NodeName = e.NodeName,
            Payload = new Dictionary<string, object>
            {
                ["nodeInstanceId"] = e.NodeInstanceId.ToString(),
                ["progressPercent"] = e.ProgressPercent,
                ["status"] = e.Status,
            },
        };
    }
}
