// -----------------------------------------------------------------------
// <copyright file="CopilotAIExecutor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

using System.Diagnostics;
using System.Text;
using ExecutionEngine.Enums;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI executor that uses the GitHub.Copilot.SDK for streaming session-based execution.
/// Supports real-time delta streaming via <see cref="AIExecutionRequest.OnOutputLine"/>.
/// </summary>
public class CopilotAIExecutor : IAIExecutor
{
    private readonly ILogger<CopilotAIExecutor> logger;

    public CopilotAIExecutor(ILogger<CopilotAIExecutor> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public AIBackend Backend => AIBackend.Copilot;

    /// <inheritdoc/>
    public async Task<AIExecutionResult> ExecuteAsync(AIExecutionRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var contentBuilder = new StringBuilder();
        var messageBlocks = new List<string>();
        var currentBlock = new StringBuilder();
        int inputTokens = 0, outputTokens = 0;

        this.logger.LogInformation(
            "Copilot SDK: model={Model}, prompt length={Length}",
            request.Model, request.Prompt.Length);

        CopilotSession? session = null;
        IDisposable? subscription = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.Timeout);

            await using var client = new CopilotClient();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create session with system prompt and model
            var sessionConfig = new SessionConfig
            {
                Model = request.Model,
                OnPermissionRequest = async (req, invocation) =>
                    new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved },
            };

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                sessionConfig.SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = request.SystemPrompt,
                };
            }

            session = await client.CreateSessionAsync(sessionConfig, cts.Token);

            var toolCallCount = 0;

            // Subscribe to streaming events
            subscription = session.On(evt =>
            {
                try
                {
                    switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var chunk = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            currentBlock.Append(chunk);
                            contentBuilder.Append(chunk);

                            // Stream each line to the UI as it completes
                            var text = currentBlock.ToString();
                            var lastNewline = text.LastIndexOf('\n');
                            if (lastNewline >= 0)
                            {
                                var completedLines = text[..lastNewline];
                                foreach (var line in completedLines.Split('\n'))
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                    {
                                        request.OnOutputLine?.Invoke(line);
                                    }
                                }

                                currentBlock.Clear();
                                currentBlock.Append(text[(lastNewline + 1)..]);
                            }
                        }

                        break;

                    case ToolExecutionStartEvent toolStart:
                        toolCallCount++;
                        this.logger.LogInformation("Tool start #{Count}: {Tool}", toolCallCount, toolStart.Data.ToolName);
                        request.OnOutputLine?.Invoke($"🔧 {toolStart.Data.ToolName}");
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        var status = toolComplete.Data.Success ? "✓" : "✗";
                        this.logger.LogInformation("Tool complete: {Status}", status);
                        request.OnOutputLine?.Invoke($"   {status} done");
                        break;

                    case AssistantIntentEvent intent:
                        if (!string.IsNullOrEmpty(intent.Data.Intent))
                        {
                            request.OnOutputLine?.Invoke($"💭 {intent.Data.Intent}");
                        }

                        break;

                    case AssistantMessageEvent msg:
                        if (currentBlock.Length == 0 && !string.IsNullOrEmpty(msg.Data.Content))
                        {
                            currentBlock.Append(msg.Data.Content);
                            contentBuilder.Append(msg.Data.Content);
                        }

                        var blockText = currentBlock.ToString().Trim();
                        if (!string.IsNullOrEmpty(blockText))
                        {
                            messageBlocks.Add(blockText);
                            request.OnOutputLine?.Invoke(blockText.Split('\n').Last());
                        }

                        currentBlock.Clear();
                        break;

                    case AssistantUsageEvent usage:
                        inputTokens += (int)(usage.Data.InputTokens ?? 0);
                        outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                        break;

                    case SessionIdleEvent:
                        var remaining = currentBlock.ToString().Trim();
                        if (!string.IsNullOrEmpty(remaining))
                        {
                            messageBlocks.Add(remaining);
                            request.OnOutputLine?.Invoke(remaining);
                        }

                        done.TrySetResult();
                        break;

                    case SessionErrorEvent:
                        done.TrySetResult();
                        break;

                    default:
                        this.logger.LogDebug("SDK event: {Type}", evt.GetType().Name);
                        break;
                }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Error in SDK event handler");
                }
            });

            // Send the prompt
            await session.SendAsync(
                new MessageOptions { Prompt = request.Prompt },
                cts.Token);

            // Wait for completion or timeout
            var completed = await Task.WhenAny(
                done.Task,
                Task.Delay(request.Timeout, cts.Token));

            sw.Stop();

            if (completed != done.Task)
            {
                this.logger.LogWarning("Copilot SDK execution timed out after {Timeout}", request.Timeout);
                return new AIExecutionResult
                {
                    Success = false,
                    Error = $"Execution timed out after {request.Timeout.TotalSeconds}s",
                    Content = contentBuilder.ToString().Trim(),
                    Duration = sw.Elapsed,
                    ModelUsed = request.Model,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                };
            }

            var fullContent = contentBuilder.ToString().Trim();
            this.logger.LogInformation(
                "Copilot SDK completed: {Length} chars, {InputTokens} in / {OutputTokens} out",
                fullContent.Length, inputTokens, outputTokens);

            return new AIExecutionResult
            {
                Success = true,
                Content = fullContent,
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new AIExecutionResult
            {
                Success = false,
                Error = "AI execution timed out or was cancelled.",
                Content = contentBuilder.ToString().Trim(),
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            this.logger.LogError(ex, "Copilot SDK execution failed");
            return new AIExecutionResult
            {
                Success = false,
                Error = $"Copilot SDK error: {ex.Message}",
                Content = contentBuilder.ToString().Trim(),
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
        }
        finally
        {
            subscription?.Dispose();
            if (session != null)
            {
                try { await session.DisposeAsync(); }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}