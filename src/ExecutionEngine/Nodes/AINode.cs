// -----------------------------------------------------------------------
// <copyright file="AINode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// AI skill/subagent node (placeholder — no actual AI call is made).
/// </summary>
public class AINode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not AINodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(AINodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var aiDef = (AINodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        nodeContext.OutputData["mode"] = aiDef.Mode;
        nodeContext.OutputData["model"] = aiDef.Model;
        nodeContext.OutputData["inputMapping"] = aiDef.InputMapping;
        nodeContext.OutputData["outputKey"] = aiDef.OutputKey;
        nodeContext.OutputData["temperature"] = aiDef.Temperature;
        nodeContext.OutputData["maxTokens"] = aiDef.MaxTokens;

        if (!string.IsNullOrEmpty(aiDef.SkillId))
        {
            nodeContext.OutputData["skillId"] = aiDef.SkillId;
        }

        if (!string.IsNullOrEmpty(aiDef.SubagentId))
        {
            nodeContext.OutputData["subagentId"] = aiDef.SubagentId;
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