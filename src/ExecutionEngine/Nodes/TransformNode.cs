// -----------------------------------------------------------------------
// <copyright file="TransformNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Data transformation node.
/// </summary>
public class TransformNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not TransformNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(TransformNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var transformDef = (TransformNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        nodeContext.OutputData["expression"] = transformDef.Expression;

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