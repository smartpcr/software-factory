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

        try
        {
            return await this.ExecuteCoreAsync(aiDef, workflowContext, nodeContext, logger, startTime, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI node execution failed: {Message}", ex.Message);

            // Emit error to the execution log so the UI shows it
            var aiLogger = workflowContext.ServiceProvider?.GetService<IAINodeLogger>();
            aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId, $"❌ {ex.GetType().Name}: {ex.Message}");

            nodeContext.OutputData["ai.error"] = ex.Message;
            return new NodeInstance
            {
                NodeId = this.Definition!.NodeId,
                Status = NodeExecutionStatus.Failed,
                ErrorMessage = ex.Message,
                Exception = ex,
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                ExecutionContext = nodeContext,
                WorkflowInstanceId = workflowContext.InstanceId,
            };
        }
    }

    private async Task<NodeInstance> ExecuteCoreAsync(
        AINodeDefinition aiDef,
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        ILogger logger,
        DateTime startTime,
        CancellationToken cancellationToken)
    {

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

                // Append structured output instructions so the agent emits key=value pairs
                if (agentDef.Outputs.Count > 0)
                {
                    var outputInstructions = new StringBuilder();
                    outputInstructions.AppendLine("\n\n## Required Output");
                    outputInstructions.AppendLine("After completing the task, emit each output on its own line in this exact format:");
                    outputInstructions.AppendLine("```");
                    foreach (var output in agentDef.Outputs)
                    {
                        outputInstructions.AppendLine($"[OUTPUT:{output.Name}] <value>");
                    }

                    outputInstructions.AppendLine("```");
                    outputInstructions.AppendLine("Replace <value> with the actual result. Emit ALL output lines even if a value is empty.");
                    systemPrompt += outputInstructions.ToString();
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
            resolvedBindings[binding.Key] = BindingResolver.ResolveToString(binding.Value, nodeContext, workflowContext);
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
                userPrompt = BuildUserPrompt(aiDef, nodeContext, workflowContext);
            }

            // Append explicit UserPrompt if provided
            if (!string.IsNullOrEmpty(aiDef.UserPrompt))
            {
                userPrompt = $"{BindingResolver.ResolveTemplate(aiDef.UserPrompt, null, nodeContext, workflowContext)}\n\n{userPrompt}";
            }
        }
        else
        {
            userPrompt = BuildUserPrompt(aiDef, nodeContext, workflowContext);
        }

        // Map confidence (0-100) → temperature (0.0-2.0): high confidence = low temperature
        var temperature = (100 - Math.Clamp(aiDef.Confidence, 0, 100)) / 50.0;

        logger.LogInformation(
            "AI execution: mode={Mode}, backend={Backend}, model={Model}",
            aiDef.Mode, aiDef.Backend, EnumMemberHelper.ToEnumMemberValue(aiDef.Model));

        // Wire stdout streaming to UI via IAINodeLogger
        var aiLogger = workflowContext.ServiceProvider?.GetService<IAINodeLogger>();

        // Log resolved input bindings
        if (aiLogger != null && resolvedBindings.Count > 0)
        {
            aiLogger.EmitLine(workflowContext.InstanceId, this.NodeId, "📥 Inputs:");
            foreach (var kvp in resolvedBindings)
            {
                var val = kvp.Value.Length > 100 ? kvp.Value[..97] + "…" : kvp.Value;
                aiLogger.EmitLine(workflowContext.InstanceId, this.NodeId, $"  {kvp.Key} = {val}");
            }
        }

        // Resolve working directory — supports same binding syntax as input bindings:
        // "global.incidentFolder", "output.path", "body.folder", "static:/path", bare keys, or {template}
        string? workingDir = null;
        if (!string.IsNullOrEmpty(aiDef.WorkingDirectory))
        {
            workingDir = BindingResolver.ResolveToString(aiDef.WorkingDirectory, nodeContext, workflowContext);
            if (string.IsNullOrEmpty(workingDir))
            {
                // Fallback: try as template with {placeholders}
                workingDir = BindingResolver.ResolveTemplate(aiDef.WorkingDirectory, null, nodeContext, workflowContext);
            }

            if (!string.IsNullOrEmpty(workingDir) && !System.IO.Directory.Exists(workingDir))
            {
                logger.LogWarning("Working directory does not exist: {Path}, using default", workingDir);
                workingDir = null;
            }
        }

        var execRequest = new AIExecutionRequest
        {
            Prompt = userPrompt,
            SystemPrompt = systemPrompt,
            Model = EnumMemberHelper.ToEnumMemberValue(aiDef.Model),
            MaxTokens = aiDef.MaxTokens,
            Temperature = temperature,
            Timeout = TimeSpan.FromMinutes(aiDef.TimeoutMinutes > 0 ? aiDef.TimeoutMinutes : 5),
            MaxTurns = aiDef.MaxTurns,
            WorkingDirectory = workingDir,
            OnOutputLine = aiLogger != null
                ? line => aiLogger.EmitLine(workflowContext.InstanceId, this.NodeId, line)
                : null,
        };

        var maxAttempts = 1 + Math.Max(0, aiDef.TimeoutRetries);
        AIExecutionResult result = null!;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId,
                    $"🔄 Timeout retry {attempt}/{aiDef.TimeoutRetries}");
            }

            result = await executor.ExecuteAsync(execRequest, cancellationToken);

            // Log execution summary
            if (aiLogger != null)
            {
                aiLogger.EmitLine(workflowContext.InstanceId, this.NodeId,
                    $"📊 {(result.Success ? "OK" : "FAIL")} | {result.InputTokens}in/{result.OutputTokens}out | {result.Duration.TotalSeconds:0.0}s | {result.ModelUsed}");
            }

            // Only retry on timeout errors, not other failures
            var isTimeout = !result.Success && result.Error != null &&
                (result.Error.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                 result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase));

            if (result.Success || !isTimeout || attempt >= maxAttempts - 1)
            {
                // Success, non-timeout error, or last attempt — stop retrying
                if (!result.Success && isTimeout && aiDef.ContinueOnTimeoutExhausted)
                {
                    // Max retries exhausted but configured to continue with partial results
                    aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId,
                        $"⚠ Timeout retries exhausted ({maxAttempts}), continuing with partial results");
                    result = new AIExecutionResult
                    {
                        Success = true,
                        Content = result.Content,
                        Duration = result.Duration,
                        ModelUsed = result.ModelUsed,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                    };
                }

                break;
            }
        }

        // Write results to output
        nodeContext.OutputData[aiDef.OutputKey] = result.Content;
        nodeContext.OutputData["ai.success"] = result.Success;
        nodeContext.OutputData["ai.model"] = result.ModelUsed ?? execRequest.Model;
        nodeContext.OutputData["ai.durationMs"] = (int)result.Duration.TotalMilliseconds;
        nodeContext.OutputData["ai.inputTokens"] = result.InputTokens;
        nodeContext.OutputData["ai.outputTokens"] = result.OutputTokens;

        // Extract structured outputs from [OUTPUT:name] markers in the response
        var extractedOutputCount = 0;
        if (agentDef?.Outputs.Count > 0 && !string.IsNullOrEmpty(result.Content))
        {
            // Phase 1: Try marker-based extraction [OUTPUT:name] value
            foreach (var output in agentDef.Outputs)
            {
                var marker = $"[OUTPUT:{output.Name}]";
                var idx = result.Content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var valueStart = idx + marker.Length;
                    var lineEnd = result.Content.IndexOf('\n', valueStart);
                    var value = lineEnd >= 0
                        ? result.Content[valueStart..lineEnd].Trim()
                        : result.Content[valueStart..].Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        // Strip markdown formatting (bold, italic, backticks)
                        value = value.TrimStart('*', '_', '`', ' ').TrimEnd('*', '_', '`', ' ');
                        nodeContext.OutputData[output.Name] = value;
                        extractedOutputCount++;
                    }
                }
            }

            // Phase 2: If markers didn't work, use a lightweight LLM call to extract outputs
            if (extractedOutputCount == 0 && result.Success)
            {
                try
                {
                    aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId,
                        "📤 Extracting outputs via LLM summary...");
                    var extracted = await this.ExtractOutputsWithLlmAsync(
                        result.Content, agentDef.Outputs, workflowContext, cancellationToken);
                    foreach (var kvp in extracted)
                    {
                        nodeContext.OutputData[kvp.Key] = kvp.Value;
                        extractedOutputCount++;
                    }

                    aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId,
                        extractedOutputCount > 0
                            ? $"📤 Extracted {extractedOutputCount} outputs: {string.Join(", ", extracted.Keys)}"
                            : "📤 No outputs found in response");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "LLM output extraction failed");
                    aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId,
                        $"📤 Extraction failed: {ex.Message}");
                }
            }

            // Phase 3: If still nothing, check for tool failures
            if (extractedOutputCount == 0 && result.Success)
            {
                var content = result.Content.ToLowerInvariant();
                var failureIndicators = 0;
                if (content.Contains("permission error")) failureIndicators++;
                if (content.Contains("tool error")) failureIndicators++;
                if (content.Contains("all tools are currently failing")) failureIndicators++;
                if (content.Contains("please try running") && content.Contains("manually")) failureIndicators++;
                if (content.Contains("unhandled permission result kind")) failureIndicators++;

                if (failureIndicators >= 2)
                {
                    result = new AIExecutionResult
                    {
                        Success = false,
                        Error = "AI agent could not execute the skill (tool permission errors). Check working directory and permissions.",
                        Content = result.Content,
                        Duration = result.Duration,
                        ModelUsed = result.ModelUsed,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                    };
                    nodeContext.OutputData["ai.success"] = false;
                    nodeContext.OutputData["ai.error"] = result.Error;
                    logger.LogWarning("AI reported success but no outputs extracted and tool failures detected");
                }
            }
        }

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
            ErrorMessage = result.Success ? null : (result.Error ?? "AI execution failed"),
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ExecutionContext = nodeContext,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }

    private static string BuildUserPrompt(AINodeDefinition aiDef, NodeExecutionContext nodeContext, WorkflowExecutionContext workflowContext)
    {
        // For RawPrompt mode, use the explicit user prompt
        if (aiDef.Mode == AINodeMode.RawPrompt && !string.IsNullOrEmpty(aiDef.UserPrompt))
        {
            return BindingResolver.ResolveTemplate(aiDef.UserPrompt, null, nodeContext, workflowContext);
        }

        // For Skill/SubAgent, build prompt from input bindings and data
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(aiDef.UserPrompt))
        {
            sb.AppendLine(BindingResolver.ResolveTemplate(aiDef.UserPrompt, null, nodeContext, workflowContext));
            sb.AppendLine();
        }

        // Resolve explicit input bindings (skill/agent declared parameters)
        if (aiDef.InputBindings.Count > 0)
        {
            sb.AppendLine("## Parameters");
            foreach (var binding in aiDef.InputBindings)
            {
                var resolved = BindingResolver.ResolveToString(binding.Value, nodeContext, workflowContext);
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

    /// <summary>
    /// Uses a lightweight Sonnet LLM call to extract expected output field values from the agent's response.
    /// </summary>
    private async Task<Dictionary<string, string>> ExtractOutputsWithLlmAsync(
        string agentResponse,
        List<AgentOutputField> outputs,
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>();

        // Include start and end of response (values often appear at the end)
        string truncated;
        if (agentResponse.Length > 12000)
        {
            var head = agentResponse[..6000];
            var tail = agentResponse.Substring(agentResponse.Length - 6000);
            truncated = head + "\n[... middle truncated ...]\n" + tail;
        }
        else
        {
            truncated = agentResponse;
        }

        // Build the extraction prompt
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Extract the following output values from the agent response below.");
        sb.AppendLine("Return ONLY lines in this exact format, one per field. No markdown, no explanation:");
        sb.AppendLine();
        foreach (var output in outputs)
        {
            sb.AppendLine($"{output.Name}: <value>");
        }

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- For file/folder paths, return the full absolute path");
        sb.AppendLine("- If a value is not found in the response, return: <FIELD_NAME>: NOT_FOUND");
        sb.AppendLine("- No quotes around values, no markdown formatting");
        sb.AppendLine();
        sb.AppendLine("--- AGENT RESPONSE ---");
        sb.AppendLine(truncated);

        // Use Sonnet for fast extraction
        var executor = workflowContext.ServiceProvider?.GetServices<IAIExecutor>()
            ?.FirstOrDefault(e => e.Backend == Enums.AIBackend.Copilot);
        if (executor == null) return result;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var extractResult = await executor.ExecuteAsync(new AIExecutionRequest
        {
            Prompt = sb.ToString(),
            Model = "claude-sonnet-4.6",
            MaxTokens = 1024,
            Temperature = 0,
            Timeout = TimeSpan.FromSeconds(30),
        }, cts.Token);

        if (!extractResult.Success || string.IsNullOrWhiteSpace(extractResult.Content)) return result;

        // Parse "fieldName: value" lines
        foreach (var line in extractResult.Content.Split('\n'))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();

            // Skip NOT_FOUND values
            if (string.Equals(value, "NOT_FOUND", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(value)) continue;

            // Match against declared outputs (case-insensitive)
            var matchedOutput = outputs.FirstOrDefault(o =>
                string.Equals(o.Name, key, StringComparison.OrdinalIgnoreCase));
            if (matchedOutput != null)
            {
                result[matchedOutput.Name] = value;
            }
        }

        return result;
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