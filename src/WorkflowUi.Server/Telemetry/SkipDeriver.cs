// -----------------------------------------------------------------------
// <copyright file="SkipDeriver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Telemetry;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;

/// <summary>
/// Derives synthetic <see cref="RunEventKind.NodeSkipped"/> events for
/// IfElse and Switch nodes when a branch is not taken.
/// Only IfElse and Switch sources trigger skip derivation (Noop, ForEach siblings do NOT).
/// </summary>
public class SkipDeriver
{
    private readonly SequenceAllocator sequenceAllocator;
    private readonly ConcurrentDictionary<(Guid RunId, string NodeId), bool> emittedSkips = new();

    public SkipDeriver(SequenceAllocator sequenceAllocator)
    {
        this.sequenceAllocator = sequenceAllocator;
    }

    /// <summary>
    /// Given an EdgeActivated event (synthetic), derives NodeSkipped events
    /// for sibling branches that were NOT taken.
    /// </summary>
    /// <param name="edgeEvent">The triggering EdgeActivated event.</param>
    /// <param name="connections">The workflow's connection graph.</param>
    /// <param name="nodes">The workflow's node definitions.</param>
    /// <returns>Synthetic NodeSkipped events (at most one per nodeId per run).</returns>
    public IReadOnlyList<RunEvent> Derive(
        RunEvent edgeEvent,
        IReadOnlyList<NodeConnection>? connections,
        IReadOnlyList<NodeDefinition>? nodes)
    {
        if (connections == null || nodes == null || edgeEvent.Payload == null)
        {
            return [];
        }

        var sourceNodeId = edgeEvent.Payload.TryGetValue("sourceNodeId", out var src) ? src?.ToString() : null;
        var targetNodeId = edgeEvent.Payload.TryGetValue("targetNodeId", out var tgt) ? tgt?.ToString() : null;

        if (sourceNodeId == null || targetNodeId == null)
        {
            return [];
        }

        // Only IfElse and Switch nodes generate skip events
        var sourceNode = nodes.FirstOrDefault(n => n.NodeId == sourceNodeId);
        if (sourceNode == null)
        {
            return [];
        }

        var isIfElse = sourceNode.RuntimeType == RuntimeType.IfElse;
        var isSwitch = sourceNode.RuntimeType == RuntimeType.Switch;

        if (!isIfElse && !isSwitch)
        {
            return [];
        }

        // Find sibling edges from the same source with the same trigger type
        var triggerType = edgeEvent.Payload.TryGetValue("triggerMessageType", out var tt)
            ? tt?.ToString()
            : null;

        if (!Enum.TryParse<MessageType>(triggerType, out var msgType))
        {
            return [];
        }

        var siblingTargets = connections
            .Where(c => c.SourceNodeId == sourceNodeId
                && c.IsEnabled
                && c.TriggerMessageType == msgType
                && c.TargetNodeId != targetNodeId)
            .Select(c => c.TargetNodeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reason = isIfElse ? "BranchNotTaken" : "SwitchCaseNoMatch";
        var results = new List<RunEvent>();

        foreach (var skippedNodeId in siblingTargets)
        {
            var key = (edgeEvent.RunId, skippedNodeId);
            if (!this.emittedSkips.TryAdd(key, true))
            {
                continue;
            }

            var skippedNode = nodes.FirstOrDefault(n => n.NodeId == skippedNodeId);
            results.Add(new RunEvent
            {
                RunId = edgeEvent.RunId,
                Seq = this.sequenceAllocator.Next(edgeEvent.RunId),
                Kind = RunEventKind.NodeSkipped,
                Synthetic = true,
                TimestampUtc = edgeEvent.TimestampUtc,
                NodeId = skippedNodeId,
                NodeName = skippedNode?.NodeName,
                Payload = new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["sourceNodeId"] = sourceNodeId,
                },
            });
        }

        return results;
    }

    /// <summary>
    /// Resets the deduplication state for a run.
    /// </summary>
    public void Reset(Guid runId)
    {
        var keysToRemove = this.emittedSkips.Keys.Where(k => k.RunId == runId).ToList();
        foreach (var key in keysToRemove)
        {
            this.emittedSkips.TryRemove(key, out _);
        }
    }
}
