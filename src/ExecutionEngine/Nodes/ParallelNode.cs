// -----------------------------------------------------------------------
// <copyright file="ParallelNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using System.Collections.Concurrent;
using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Routing;

/// <summary>
/// Fan-out parallel branches node with join mode semantics.
/// Supports "all", "any", and "race" join modes with configurable timeout.
/// </summary>
public class ParallelNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not ParallelNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(ParallelNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <summary>
    /// Gets the available output ports for this node (one per branch).
    /// </summary>
    /// <returns>Array of branch names as port names.</returns>
    public string[] GetAvailablePorts()
    {
        var parallelDef = (ParallelNodeDefinition)this.Definition!;
        return [.. parallelDef.Branches];
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var parallelDef = (ParallelNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;
        var joinMode = (parallelDef.JoinMode ?? "all").ToLowerInvariant();

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        nodeContext.OutputData["joinMode"] = joinMode;
        nodeContext.OutputData["timeout"] = parallelDef.Timeout;
        nodeContext.OutputData["branches"] = string.Join(",", parallelDef.Branches);
        nodeContext.OutputData["branchCount"] = parallelDef.Branches.Count;

        // Discover terminal nodes for each branch via the router's connection graph
        var branchTerminals = new Dictionary<string, HashSet<string>>();
        if (workflowContext.Router != null)
        {
            foreach (var branch in parallelDef.Branches)
            {
                var terminals = workflowContext.Router.FindTerminalNodesFromPort(this.NodeId, branch);
                branchTerminals[branch] = terminals;
            }
        }

        // Store join tracking data in workflow variables
        var trackingPrefix = $"__parallel_{this.NodeId}";
        workflowContext.Variables[$"{trackingPrefix}_branchTerminals"] = branchTerminals;
        workflowContext.Variables[$"{trackingPrefix}_completedBranches"] = new ConcurrentDictionary<string, bool>();
        workflowContext.Variables[$"{trackingPrefix}_expected"] = parallelDef.Branches.Count;
        workflowContext.Variables[$"{trackingPrefix}_joinMode"] = joinMode;
        workflowContext.Variables[$"{trackingPrefix}_deadline"] = startTime.AddMilliseconds(parallelDef.Timeout);
        workflowContext.Variables[$"{trackingPrefix}_nodeContext"] = nodeContext;

        // Route to each branch's specific children (not via default port matching)
        if (workflowContext.Router != null)
        {
            foreach (var branch in parallelDef.Branches)
            {
                var connections = workflowContext.Router.GetConnections(this.NodeId);
                var branchTargets = connections
                    .Where(c => string.Equals(c.SourcePort, branch, StringComparison.OrdinalIgnoreCase) &&
                                c.IsEnabled &&
                                c.TriggerMessageType == MessageType.Complete)
                    .Select(c => c.TargetNodeId)
                    .ToArray();

                if (branchTargets.Length > 0)
                {
                    var branchMsg = new Messages.NodeCompleteMessage
                    {
                        NodeId = this.NodeId,
                        Timestamp = DateTime.UtcNow,
                        NodeInstanceId = Guid.NewGuid(),
                        NodeContext = nodeContext,
                        SourcePort = branch,
                    };
                    await workflowContext.Router.RouteToTargetsAsync(
                        branchMsg, branchTargets, workflowContext, cancellationToken);
                }
            }
        }

        // Return WaitingForJoin — the engine will not route completion downstream.
        // Completion is triggered later by CheckParallelJoins when branches finish.
        return new NodeInstance
        {
            NodeId = this.Definition!.NodeId,
            Status = NodeExecutionStatus.WaitingForJoin,
            StartTime = startTime,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }
}