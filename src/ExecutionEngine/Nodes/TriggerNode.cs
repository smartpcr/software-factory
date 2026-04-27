// -----------------------------------------------------------------------
// <copyright file="TriggerNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// HTTP webhook trigger node.
/// </summary>
public class TriggerNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not TriggerNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(TriggerNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var triggerDef = (TriggerNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        nodeContext.OutputData["method"] = triggerDef.Method;
        nodeContext.OutputData["route"] = triggerDef.Route;
        nodeContext.OutputData["triggerTime"] = startTime.ToString("O");

        if (!string.IsNullOrEmpty(triggerDef.Parameters))
        {
            nodeContext.OutputData["parameters"] = triggerDef.Parameters;
        }

        await Task.Delay(10, cancellationToken);

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