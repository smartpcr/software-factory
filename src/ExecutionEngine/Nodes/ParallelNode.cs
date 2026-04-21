// -----------------------------------------------------------------------
// <copyright file="ParallelNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Fan-out parallel branches node.
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

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        nodeContext.OutputData["joinMode"] = parallelDef.JoinMode;
        nodeContext.OutputData["timeout"] = parallelDef.Timeout;
        nodeContext.OutputData["branches"] = string.Join(",", parallelDef.Branches);

        // Route a NodeCompleteMessage to each branch port so branch children get triggered
        if (workflowContext.Router != null)
        {
            foreach (var branch in parallelDef.Branches)
            {
                var branchMsg = new Messages.NodeCompleteMessage
                {
                    NodeId = this.NodeId,
                    Timestamp = DateTime.UtcNow,
                    NodeInstanceId = Guid.NewGuid(),
                    NodeContext = nodeContext,
                    SourcePort = branch,
                };
                await workflowContext.Router.RouteMessageAsync(branchMsg, workflowContext, cancellationToken);
            }
        }

        return new NodeInstance
        {
            NodeId = this.Definition!.NodeId,
            Status = NodeExecutionStatus.Completed,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ExecutionContext = nodeContext,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }
}