// -----------------------------------------------------------------------
// <copyright file="EdgeActivationDeriver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Workflow;

/// <summary>
/// Derives synthetic <see cref="RunEventKind.EdgeActivated"/> events from node
/// completion events by correlating with the workflow connection graph.
/// </summary>
public class EdgeActivationDeriver
{
    private readonly SequenceAllocator sequenceAllocator;

    public EdgeActivationDeriver(SequenceAllocator sequenceAllocator)
    {
        this.sequenceAllocator = sequenceAllocator;
    }

    /// <summary>
    /// Given a node-level RunEvent and the workflow graph, derives synthetic
    /// EdgeActivated events for each matching outgoing connection.
    /// </summary>
    /// <param name="nodeEvent">The triggering node event (Completed/Failed/Cancelled).</param>
    /// <param name="connections">The workflow's connection graph.</param>
    /// <returns>Synthetic EdgeActivated RunEvents, ordered by Priority desc then TargetNodeId asc.</returns>
    public IReadOnlyList<RunEvent> Derive(RunEvent nodeEvent, IReadOnlyList<NodeConnection>? connections)
    {
        if (connections == null || nodeEvent.NodeId == null)
        {
            return [];
        }

        var triggerType = this.ProjectTriggerType(nodeEvent.Kind);
        if (triggerType == null)
        {
            return [];
        }

        var matching = connections
            .Where(c => c.SourceNodeId == nodeEvent.NodeId
                && c.IsEnabled
                && c.TriggerMessageType == triggerType.Value)
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.TargetNodeId, StringComparer.Ordinal)
            .ToList();

        return matching.Select(c => new RunEvent
        {
            RunId = nodeEvent.RunId,
            Seq = this.sequenceAllocator.Next(nodeEvent.RunId),
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            TimestampUtc = nodeEvent.TimestampUtc,
            NodeId = c.SourceNodeId,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = c.SourceNodeId,
                ["targetNodeId"] = c.TargetNodeId,
                ["triggerMessageType"] = c.TriggerMessageType.ToString(),
                ["condition"] = c.Condition ?? string.Empty,
            },
        }).ToList();
    }

    /// <summary>
    /// Projects a RunEventKind to the corresponding MessageType for edge matching.
    /// Progress is non-terminal and produces no synthetic edges.
    /// Next produces edges only for TriggerMessageType.Next.
    /// </summary>
    private MessageType? ProjectTriggerType(RunEventKind kind)
    {
        return kind switch
        {
            RunEventKind.NodeCompleted => MessageType.Complete,
            RunEventKind.NodeFailed => MessageType.Fail,
            RunEventKind.NodeCancelled => MessageType.Cancel,
            _ => null,
        };
    }
}
