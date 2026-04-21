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

        nodeContext.OutputData["mode"] = EnumMemberHelper.ToEnumMemberValue(aiDef.Mode);
        nodeContext.OutputData["model"] = EnumMemberHelper.ToEnumMemberValue(aiDef.Model);
        nodeContext.OutputData["inputMapping"] = aiDef.InputMapping;
        nodeContext.OutputData["outputKey"] = aiDef.OutputKey;
        nodeContext.OutputData["confidence"] = aiDef.Confidence;
        nodeContext.OutputData["maxTokens"] = aiDef.MaxTokens;

        if (!string.IsNullOrEmpty(aiDef.SkillName))
        {
            nodeContext.OutputData["skillName"] = aiDef.SkillName;
        }

        if (!string.IsNullOrEmpty(aiDef.SubAgentName))
        {
            nodeContext.OutputData["subAgentName"] = aiDef.SubAgentName;
        }

        if (!string.IsNullOrEmpty(aiDef.UserPrompt))
        {
            nodeContext.OutputData["userPrompt"] = aiDef.UserPrompt;
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