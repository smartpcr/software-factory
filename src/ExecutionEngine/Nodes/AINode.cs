// -----------------------------------------------------------------------
// <copyright file="AINode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using System.Text;
using System.Text.Json;
using ExecutionEngine.AI;
using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// AI skill/subagent node — invokes an AI backend (Copilot or Claude)
/// using a skill or agent definition as the system prompt.
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
        var logger = workflowContext.ServiceProvider?.GetService<ILoggerFactory>()?.CreateLogger<AINode>()
            ?? NullLogger<AINode>.Instance;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        // Record config metadata
        nodeContext.OutputData["mode"] = EnumMemberHelper.ToEnumMemberValue(aiDef.Mode);
        nodeContext.OutputData["backend"] = EnumMemberHelper.ToEnumMemberValue(aiDef.Backend);
        nodeContext.OutputData["model"] = EnumMemberHelper.ToEnumMemberValue(aiDef.Model);

        // Resolve the AI executor from DI
        var executors = workflowContext.ServiceProvider?.GetServices<IAIExecutor>();
        var executor = executors?.FirstOrDefault(e => e.Backend == aiDef.Backend);

        if (executor == null)
        {
            logger.LogWarning("No IAIExecutor registered for backend {Backend}, running in dry-run mode", aiDef.Backend);
            nodeContext.OutputData[aiDef.OutputKey] = "[dry-run] No AI executor available";
            return CompletedInstance(aiDef, startTime, nodeContext, workflowContext);
        }

        // Resolve system prompt from agent/skill definition
        var systemPrompt = aiDef.SystemPrompt;
        var promptResolver = workflowContext.ServiceProvider?.GetService<IAIPromptResolver>();
        AgentDefinitionInfo? agentDef = null;

        if (promptResolver != null)
        {
            try
            {
                using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                loadCts.CancelAfter(TimeSpan.FromSeconds(90));
                switch (aiDef.Mode)
                {
                    case AINodeMode.Skill when !string.IsNullOrEmpty(aiDef.SkillName):
                        agentDef = await promptResolver.LoadSkillAsync(aiDef.SkillName, loadCts.Token);
                        break;
                    case AINodeMode.SubAgent when !string.IsNullOrEmpty(aiDef.SubAgentName):
                        agentDef = await promptResolver.LoadAgentAsync(aiDef.SubAgentName, loadCts.Token);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Agent/skill loading timed out for {Mode}", aiDef.Mode);
            }

            if (agentDef != null)
            {
                systemPrompt = agentDef.SystemPrompt;
                if (!string.IsNullOrEmpty(aiDef.SystemPrompt))
                {
                    systemPrompt += $"\n\n## Additional Instructions\n{aiDef.SystemPrompt}";
                }

                if (agentDef.ModelHint != null)
                {
                    nodeContext.OutputData["agentModelHint"] = agentDef.ModelHint;
                }

                nodeContext.OutputData["agentName"] = agentDef.Name;
                nodeContext.OutputData["agentSummary"] = agentDef.Summary;
            }
        }

        // Build the user prompt — use LLM-generated prompt if we have an agent definition
        string userPrompt;
        var resolvedBindings = new Dictionary<string, string>();
        foreach (var binding in aiDef.InputBindings)
        {
            resolvedBindings[binding.Key] = ResolveInputPath(binding.Value, nodeContext);
        }

        if (agentDef != null && promptResolver != null && (aiDef.Mode == AINodeMode.Skill || aiDef.Mode == AINodeMode.SubAgent))
        {

            // Gather additional context from unmapped input data
            var boundPaths = new HashSet<string>(aiDef.InputBindings.Values, StringComparer.OrdinalIgnoreCase);
            var extraContext = new StringBuilder();
            foreach (var kvp in nodeContext.InputData.Where(k => !boundPaths.Contains(k.Key)))
            {
                var val = kvp.Value?.ToString() ?? "";
                if (val.Length > 500)
                {
                    val = val[..500] + "…";
                }

                extraContext.AppendLine($"- {kvp.Key}: {val}");
            }

            // Stage 2: LLM generates user prompt from summary + bound params (with timeout fallback)
            try
            {
                using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                promptCts.CancelAfter(TimeSpan.FromSeconds(60));
                userPrompt = await promptResolver.GenerateUserPromptAsync(
                    agentDef,
                    resolvedBindings,
                    extraContext.Length > 0 ? extraContext.ToString() : null,
                    promptCts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("LLM prompt generation timed out, using fallback template");
                userPrompt = BuildUserPrompt(aiDef, nodeContext);
            }

            // Append explicit UserPrompt if provided
            if (!string.IsNullOrEmpty(aiDef.UserPrompt))
            {
                userPrompt = $"{ResolveTemplatePlaceholders(aiDef.UserPrompt, nodeContext)}\n\n{userPrompt}";
            }
        }
        else
        {
            userPrompt = BuildUserPrompt(aiDef, nodeContext);
        }

        // Map confidence (0-100) → temperature (0.0-2.0): high confidence = low temperature
        var temperature = (100 - Math.Clamp(aiDef.Confidence, 0, 100)) / 50.0;

        logger.LogInformation(
            "AI execution: mode={Mode}, backend={Backend}, model={Model}",
            aiDef.Mode, aiDef.Backend, EnumMemberHelper.ToEnumMemberValue(aiDef.Model));

        // Wire stdout streaming to UI via IAINodeLogger
        var aiLogger = workflowContext.ServiceProvider?.GetService<IAINodeLogger>();

        var execRequest = new AIExecutionRequest
        {
            Prompt = userPrompt,
            SystemPrompt = systemPrompt,
            Model = EnumMemberHelper.ToEnumMemberValue(aiDef.Model),
            MaxTokens = aiDef.MaxTokens,
            Temperature = temperature,
            Timeout = TimeSpan.FromMinutes(10),
            OnOutputLine = aiLogger != null
                ? line => aiLogger.EmitLine(workflowContext.InstanceId, this.NodeId, line)
                : null,
        };

        var result = await executor.ExecuteAsync(execRequest, cancellationToken);

        // Write results to output
        nodeContext.OutputData[aiDef.OutputKey] = result.Content;
        nodeContext.OutputData["ai.success"] = result.Success;
        nodeContext.OutputData["ai.model"] = result.ModelUsed ?? execRequest.Model;
        nodeContext.OutputData["ai.durationMs"] = (int)result.Duration.TotalMilliseconds;
        nodeContext.OutputData["ai.inputTokens"] = result.InputTokens;
        nodeContext.OutputData["ai.outputTokens"] = result.OutputTokens;

        if (!result.Success)
        {
            nodeContext.OutputData["ai.error"] = result.Error ?? "Unknown error";
            logger.LogError("AI execution failed: {Error}", result.Error);
        }

        var status = result.Success ? NodeExecutionStatus.Completed : NodeExecutionStatus.Failed;
        return new NodeInstance
        {
            NodeId = this.Definition!.NodeId,
            Status = status,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ExecutionContext = nodeContext,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }

    private static string BuildUserPrompt(AINodeDefinition aiDef, NodeExecutionContext nodeContext)
    {
        // For RawPrompt mode, use the explicit user prompt
        if (aiDef.Mode == AINodeMode.RawPrompt && !string.IsNullOrEmpty(aiDef.UserPrompt))
        {
            return ResolveTemplatePlaceholders(aiDef.UserPrompt, nodeContext);
        }

        // For Skill/SubAgent, build prompt from input bindings and data
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(aiDef.UserPrompt))
        {
            sb.AppendLine(ResolveTemplatePlaceholders(aiDef.UserPrompt, nodeContext));
            sb.AppendLine();
        }

        // Resolve explicit input bindings (skill/agent declared parameters)
        if (aiDef.InputBindings.Count > 0)
        {
            sb.AppendLine("## Parameters");
            foreach (var binding in aiDef.InputBindings)
            {
                var resolved = ResolveInputPath(binding.Value, nodeContext);
                sb.AppendLine($"- **{binding.Key}**: {resolved}");
            }

            sb.AppendLine();
        }

        // Include remaining input data as context
        var boundPaths = new HashSet<string>(aiDef.InputBindings.Values, StringComparer.OrdinalIgnoreCase);
        var unmapped = nodeContext.InputData
            .Where(kvp => !boundPaths.Contains(kvp.Key))
            .ToList();

        if (unmapped.Count > 0)
        {
            sb.AppendLine("## Additional Context");
            foreach (var kvp in unmapped)
            {
                var valStr = kvp.Value switch
                {
                    string s => s,
                    null => "(null)",
                    _ when kvp.Value.GetType().IsPrimitive => kvp.Value.ToString(),
                    _ => TrySerialize(kvp.Value),
                };

                if (valStr != null && valStr.Length > 2000)
                {
                    valStr = valStr[..2000] + "… (truncated)";
                }

                sb.AppendLine($"- **{kvp.Key}**: {valStr}");
            }
        }

        return sb.ToString().Trim();
    }

    private static string ResolveInputPath(string path, NodeExecutionContext context)
    {
        // Direct match
        if (context.InputData.TryGetValue(path, out var value))
        {
            return value?.ToString() ?? string.Empty;
        }

        // Try without "body." prefix if the path starts with it
        if (path.StartsWith("body.", StringComparison.Ordinal))
        {
            var shortKey = path[5..];
            if (context.InputData.TryGetValue(shortKey, out var shortVal))
            {
                return shortVal?.ToString() ?? string.Empty;
            }
        }

        return $"({path} not found)";
    }

    private static string ResolveTemplatePlaceholders(string template, NodeExecutionContext context)
    {
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+)\}", match =>
        {
            var name = match.Groups[1].Value;
            if (context.InputData.TryGetValue(name, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }

            if (context.InputData.TryGetValue($"body.{name}", out var bodyValue))
            {
                return bodyValue?.ToString() ?? string.Empty;
            }

            return match.Value;
        });
    }

    private static string TrySerialize(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? "(object)";
        }
    }

    private static NodeInstance CompletedInstance(
        AINodeDefinition aiDef,
        DateTime startTime,
        NodeExecutionContext nodeContext,
        WorkflowExecutionContext workflowContext)
    {
        return new NodeInstance
        {
            NodeId = aiDef.NodeId,
            Status = NodeExecutionStatus.Completed,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ExecutionContext = nodeContext,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }

}