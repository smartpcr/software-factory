// -----------------------------------------------------------------------
// <copyright file="DelayNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Wait/pause node that delays execution for a specified duration.
/// </summary>
public class DelayNode : ExecutableNodeBase
{
    private const int MaxDelayMs = 30000;

    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not DelayNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(DelayNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var delayDef = (DelayNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        var delayMs = delayDef.Unit.ToLowerInvariant() switch
        {
            "s" => delayDef.Duration * 1000,
            "ms" => delayDef.Duration,
            _ => delayDef.Duration,
        };

        // Cap delay at 30 seconds
        delayMs = Math.Min(delayMs, MaxDelayMs);

        nodeContext.OutputData["duration"] = delayDef.Duration;
        nodeContext.OutputData["unit"] = delayDef.Unit;
        nodeContext.OutputData["actualDelayMs"] = delayMs;

        await Task.Delay(delayMs, cancellationToken);

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