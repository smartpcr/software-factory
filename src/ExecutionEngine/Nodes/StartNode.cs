// -----------------------------------------------------------------------
// <copyright file="StartNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Start node — the entry point of a workflow.
/// Supports manual, schedule, and event trigger types.
/// </summary>
public class StartNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not StartNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(StartNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var startDef = (StartNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        // Record trigger metadata in output
        nodeContext.OutputData["triggerType"] = startDef.TriggerType.ToString();
        nodeContext.OutputData["triggerTime"] = startTime.ToString("O");

        if (startDef.TriggerType == TriggerType.Schedule && !string.IsNullOrEmpty(startDef.Schedule))
        {
            nodeContext.OutputData["schedule"] = startDef.Schedule;
        }

        if (startDef.TriggerType == TriggerType.Event && !string.IsNullOrEmpty(startDef.EventName))
        {
            nodeContext.OutputData["eventName"] = startDef.EventName;
        }

        if (startDef.TriggerType == TriggerType.WebHook)
        {
            nodeContext.OutputData["webHookMethod"] = startDef.WebHookMethod;
            nodeContext.OutputData["webHookRoute"] = $"/webhook/{startDef.WebHookRoute}";
            if (!string.IsNullOrEmpty(startDef.BodyJsonSchema))
            {
                nodeContext.OutputData["bodyJsonSchema"] = startDef.BodyJsonSchema;
            }

            // Output messageType is the JSON object parsed from the request body
            nodeContext.OutputData["messageType"] = "json";

            // Copy test payload fields (body.*) from workflow variables to output
            foreach (var kvp in workflowContext.Variables)
            {
                if (kvp.Key.StartsWith("body.", StringComparison.Ordinal))
                {
                    nodeContext.OutputData[kvp.Key] = kvp.Value;
                }
            }

            if (workflowContext.Variables.TryGetValue("__webhookBody", out var rawBody))
            {
                nodeContext.OutputData["__webhookBody"] = rawBody;
            }
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