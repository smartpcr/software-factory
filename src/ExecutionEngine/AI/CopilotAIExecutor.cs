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

    /// <summary>Model fallback chain — when a model is unavailable, try the next one.</summary>
    private static readonly Dictionary<string, string[]> ModelFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4.7"] = ["claude-opus-4.6", "claude-sonnet-4.6"],
        ["claude-opus-4.6"] = ["claude-opus-4.7", "claude-sonnet-4.6"],
        ["claude-opus-4.5"] = ["claude-opus-4.6", "claude-sonnet-4.5"],
        ["claude-sonnet-4.6"] = ["claude-sonnet-4.5", "claude-sonnet-4"],
        ["claude-sonnet-4.5"] = ["claude-sonnet-4.6", "claude-sonnet-4"],
    };

    /// <inheritdoc/>
    public async Task<AIExecutionResult> ExecuteAsync(AIExecutionRequest request, CancellationToken cancellationToken)
    {
        var result = await this.ExecuteWithModelAsync(request, request.Model, cancellationToken);
        if (!result.Success && result.Error != null && result.Error.Contains("is not available", StringComparison.OrdinalIgnoreCase))
        {
            // Model unavailable — try fallbacks
            if (ModelFallbacks.TryGetValue(request.Model, out var fallbacks))
            {
                foreach (var fallback in fallbacks)
                {
                    this.logger.LogWarning("Model {Model} unavailable, trying fallback {Fallback}", request.Model, fallback);
                    request.OnOutputLine?.Invoke($"⚠ Model {request.Model} unavailable, trying {fallback}…");
                    result = await this.ExecuteWithModelAsync(request, fallback, cancellationToken);
                    if (result.Success || result.Error == null || !result.Error.Contains("is not available", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
        }

        return result;
    }

    private async Task<AIExecutionResult> ExecuteWithModelAsync(AIExecutionRequest request, string model, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var contentBuilder = new StringBuilder();
        var messageBlocks = new List<string>();
        var currentBlock = new StringBuilder();
        int inputTokens = 0, outputTokens = 0;

        // Idle timeout: fixed 5 min — detects truly stuck/hanging sessions.
        // Independent from the node's hard timeout (request.Timeout).
        var idleTimeout = TimeSpan.FromMinutes(5);
        var lastEventTime = DateTime.UtcNow;

        // Repeat-failure detector: bail if the same tool keeps failing with the same error.
        // This catches "stuck agent" cases like a 'create' tool whose schema requires
        // file_text but the model omits it — the model retries identically, wasting wallclock.
        const int MaxIdenticalToolFailures = 3;
        var lastToolName = string.Empty;
        var lastFailureFingerprint = string.Empty;
        var consecutiveFailureCount = 0;
        var stuckReason = string.Empty;

        this.logger.LogInformation(
            "Copilot SDK: model={Model}, prompt length={Length}, timeout={Timeout}",
            model, request.Prompt.Length, request.Timeout);

        CopilotSession? session = null;
        IDisposable? subscription = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.Timeout);

            var clientOptions = new CopilotClientOptions
            {
                CliArgs = ["--yolo"],
                CliPath = CliLocator.Copilot,
            };
            if (!string.IsNullOrEmpty(request.WorkingDirectory))
            {
                clientOptions.Cwd = request.WorkingDirectory;
            }

            await using var client = new CopilotClient(clientOptions);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create session with system prompt and model
            var sessionConfig = new SessionConfig
            {
                Model = model,
                OnPermissionRequest = PermissionHandler.ApproveAll,
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
                    // Update idle timer on every event
                    lastEventTime = DateTime.UtcNow;

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
                        var toolName = toolStart.Data.ToolName;
                        lastToolName = toolName;
                        this.logger.LogInformation("Tool start #{Count}: {Tool}", toolCallCount, toolName);

                        // Show tool name + arguments summary in execution log
                        var argsStr = "";
                        if (toolStart.Data.Arguments != null)
                        {
                            try
                            {
                                var argsJson = System.Text.Json.JsonSerializer.Serialize(toolStart.Data.Arguments);
                                // Extract key argument for common tools
                                argsStr = toolName switch
                                {
                                    "view" => ExtractArg(argsJson, "path"),
                                    "edit" => ExtractArg(argsJson, "path"),
                                    "create" => ExtractArg(argsJson, "path"),
                                    "grep" => ExtractArg(argsJson, "pattern"),
                                    "glob" => ExtractArg(argsJson, "pattern"),
                                    "powershell" => ExtractArg(argsJson, "command", 120),
                                    "task" => ExtractArg(argsJson, "prompt", 80),
                                    _ => argsJson.Length > 100 ? argsJson[..97] + "…" : argsJson,
                                };
                            }
                            catch { /* ignore serialization errors */ }
                        }

                        request.OnOutputLine?.Invoke(string.IsNullOrEmpty(argsStr)
                            ? $"🔧 {toolName}"
                            : $"🔧 {toolName} → {argsStr}");
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        var status = toolComplete.Data.Success ? "✓" : "✗";
                        this.logger.LogInformation("Tool complete: {Status}", status);
                        if (!toolComplete.Data.Success && toolComplete.Data.Error != null)
                        {
                            // Show the actual error from the tool
                            string errJson;
                            try
                            {
                                errJson = System.Text.Json.JsonSerializer.Serialize(toolComplete.Data.Error);
                            }
                            catch
                            {
                                errJson = toolComplete.Data.Error.ToString() ?? "(error)";
                            }

                            var errPreview = errJson.Length > 200 ? errJson[..197] + "…" : errJson;
                            request.OnOutputLine?.Invoke($"   {status} {errPreview}");

                            // Update repeat-failure detector. Fingerprint is tool name + error
                            // string — if the model is making the same broken call, both stay
                            // constant; if it adapts, the fingerprint changes and we reset.
                            // Skip polling/async tools that are expected to timeout while waiting.
                            var isPollingTool = lastToolName is "read_agent" or "read_powershell" or "write_agent";
                            if (!isPollingTool)
                            {
                                var fingerprint = $"{lastToolName}|{errJson}";
                                if (fingerprint == lastFailureFingerprint)
                                {
                                    consecutiveFailureCount++;
                                    if (consecutiveFailureCount >= MaxIdenticalToolFailures && string.IsNullOrEmpty(stuckReason))
                                    {
                                        stuckReason = $"Tool '{lastToolName}' failed {consecutiveFailureCount} times with the same error: {errPreview}";
                                        request.OnOutputLine?.Invoke($"⚠ Stuck: {stuckReason}");
                                        this.logger.LogWarning("Repeat-failure detector tripped: {Reason}", stuckReason);
                                    }
                                }
                                else
                                {
                                    lastFailureFingerprint = fingerprint;
                                    consecutiveFailureCount = 1;
                                }
                            }
                        }
                        else
                        {
                            // Show result summary for successful tools
                            var resultSummary = "";
                            if (toolComplete.Data.Result != null)
                            {
                                try
                                {
                                    var resultJson = System.Text.Json.JsonSerializer.Serialize(toolComplete.Data.Result);
                                    resultSummary = resultJson.Length > 200 ? resultJson[..197] + "…" : resultJson;
                                }
                                catch
                                {
                                    var resultStr = toolComplete.Data.Result.ToString() ?? "";
                                    resultSummary = resultStr.Length > 200 ? resultStr[..197] + "…" : resultStr;
                                }
                            }

                            request.OnOutputLine?.Invoke(string.IsNullOrEmpty(resultSummary)
                                ? $"   {status} done"
                                : $"   {status} {resultSummary}");
                            // Any successful tool call clears the streak and recovery.
                            lastFailureFingerprint = string.Empty;
                            consecutiveFailureCount = 0;
                            stuckReason = string.Empty;
                        }

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

            // Wait for completion, hard timeout, or idle timeout.
            // Hard timeout = total wall-clock budget (request.Timeout, e.g. 15 min).
            // Idle timeout = no streaming events received for `idleTimeout` (e.g. 5 min).
            // Both must be enforced explicitly here — Task.WhenAny does not propagate
            // cancellation from the linked cts to throw out of the loop, so we poll.
            var hardDeadline = DateTime.UtcNow + request.Timeout;

            while (true)
            {
                var now = DateTime.UtcNow;

                // Repeat-failure detector — agent is stuck retrying the same broken tool call
                if (!string.IsNullOrEmpty(stuckReason))
                {
                    sw.Stop();
                    this.logger.LogWarning("Bailing out due to repeat tool failure: {Reason}", stuckReason);
                    return new AIExecutionResult
                    {
                        Success = false,
                        Error = $"Stuck on repeated tool failure: {stuckReason}",
                        Content = contentBuilder.ToString().Trim(),
                        Duration = sw.Elapsed,
                        ModelUsed = model,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                    };
                }

                // Hard timeout — overall budget exhausted
                if (now >= hardDeadline)
                {
                    sw.Stop();
                    request.OnOutputLine?.Invoke($"⚠ Hard timeout after {request.Timeout.TotalMinutes:0}m — terminating");
                    this.logger.LogWarning("Hard timeout: exceeded {Timeout}", request.Timeout);
                    return new AIExecutionResult
                    {
                        Success = false,
                        Error = $"Hard timeout: exceeded {request.Timeout.TotalMinutes:0}m budget",
                        Content = contentBuilder.ToString().Trim(),
                        Duration = sw.Elapsed,
                        ModelUsed = model,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                    };
                }

                // Idle timeout — no streaming events for too long
                var timeSinceLastEvent = now - lastEventTime;
                var remainingIdle = idleTimeout - timeSinceLastEvent;
                if (remainingIdle <= TimeSpan.Zero)
                {
                    sw.Stop();
                    request.OnOutputLine?.Invoke($"⚠ No activity for {idleTimeout.TotalMinutes:0}m — terminating");
                    this.logger.LogWarning("Idle timeout: no events for {IdleTimeout}", idleTimeout);
                    return new AIExecutionResult
                    {
                        Success = false,
                        Error = $"Idle timeout: no streaming events for {idleTimeout.TotalMinutes:0} minutes",
                        Content = contentBuilder.ToString().Trim(),
                        Duration = sw.Elapsed,
                        ModelUsed = model,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                    };
                }

                // Wait for either next event/completion or until the soonest deadline.
                var hardRemaining = hardDeadline - now;
                var wait = TimeSpan.FromSeconds(10);
                if (remainingIdle < wait) wait = remainingIdle;
                if (hardRemaining < wait) wait = hardRemaining;
                if (wait < TimeSpan.FromMilliseconds(100)) wait = TimeSpan.FromMilliseconds(100);

                await Task.WhenAny(done.Task, Task.Delay(wait, cts.Token));

                if (done.Task.IsCompleted) break;
            }

            sw.Stop();

            var fullContent = contentBuilder.ToString().Trim();
            this.logger.LogInformation(
                "Copilot SDK completed: {Length} chars, {InputTokens} in / {OutputTokens} out",
                fullContent.Length, inputTokens, outputTokens);

            return new AIExecutionResult
            {
                Success = true,
                Content = fullContent,
                Duration = sw.Elapsed,
                ModelUsed = model,
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
                ModelUsed = model,
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
                ModelUsed = model,
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

    /// <summary>Extract a specific argument value from a JSON args string.</summary>
    private static string ExtractArg(string argsJson, string key, int maxLen = 200)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty(key, out var val))
            {
                var s = val.ToString();
                return s.Length > maxLen ? s[..(maxLen - 1)] + "…" : s;
            }
        }
        catch { /* ignore */ }

        return argsJson.Length > 80 ? argsJson[..77] + "…" : argsJson;
    }
}
