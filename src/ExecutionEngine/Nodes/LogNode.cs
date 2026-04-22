// -----------------------------------------------------------------------
// <copyright file="LogNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using System.Text.RegularExpressions;
using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Log message output node. Resolves template placeholders from input data.
/// </summary>
public class LogNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not LogNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(LogNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var logDef = (LogNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        // Resolve template placeholders
        var resolvedMessage = ResolveTemplate(logDef.MessageTemplate, logDef.ParameterBindings, nodeContext, workflowContext);

        nodeContext.OutputData["level"] = logDef.Level.ToString().ToLowerInvariant();
        nodeContext.OutputData["message"] = resolvedMessage;
        nodeContext.OutputData["messageTemplate"] = logDef.MessageTemplate;

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

    private static string ResolveTemplate(
        string template,
        Dictionary<string, string> bindings,
        NodeExecutionContext context,
        WorkflowExecutionContext workflowContext)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return Regex.Replace(template, @"\{(\w+)\}", match =>
        {
            var name = match.Groups[1].Value;

            // Check explicit bindings first
            if (bindings.TryGetValue(name, out var expression))
            {
                return ResolveExpression(expression, context);
            }

            // Check input data by exact key
            if (context.InputData.TryGetValue(name, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }

            // Check input data with "body." prefix (webhook payload fields)
            if (context.InputData.TryGetValue($"body.{name}", out var bodyValue))
            {
                return bodyValue?.ToString() ?? string.Empty;
            }

            // Check workflow global variables
            if (workflowContext.Variables.TryGetValue(name, out var globalValue))
            {
                return globalValue?.ToString() ?? string.Empty;
            }

            // Unresolved placeholder — keep as-is
            return match.Value;
        });
    }

    private static string ResolveExpression(string expression, NodeExecutionContext context)
    {
        // Simple dot-notation resolution: "input.userId" → context.InputData["userId"]
        if (expression.StartsWith("input.", StringComparison.OrdinalIgnoreCase))
        {
            var key = expression[6..];
            if (context.InputData.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }
        }

        // Direct key match
        if (context.InputData.TryGetValue(expression, out var directValue))
        {
            return directValue?.ToString() ?? string.Empty;
        }

        return $"{{{expression}}}";
    }
}