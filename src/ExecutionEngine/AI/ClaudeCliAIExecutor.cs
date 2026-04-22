// -----------------------------------------------------------------------
// <copyright file="ClaudeCliAIExecutor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

using System.Diagnostics;
using System.Text;
using ExecutionEngine.Enums;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI executor that invokes the Anthropic Claude CLI (<c>claude --print</c>).
/// </summary>
public class ClaudeCliAIExecutor : IAIExecutor
{
    private readonly ILogger<ClaudeCliAIExecutor> logger;

    public ClaudeCliAIExecutor(ILogger<ClaudeCliAIExecutor> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public AIBackend Backend => AIBackend.Claude;

    /// <inheritdoc/>
    public async Task<AIExecutionResult> ExecuteAsync(AIExecutionRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var prompt = request.Prompt;
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            prompt = $"{request.SystemPrompt}\n\n---\n\n{prompt}";
        }

        var args = new StringBuilder("--print");
        args.Append($" --model {request.Model}");
        if (request.MaxTokens > 0)
        {
            args.Append($" --max-tokens {request.MaxTokens}");
        }

        this.logger.LogInformation("Claude CLI: model={Model}, prompt length={Length}", request.Model, prompt.Length);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.Timeout);

            var psi = new ProcessStartInfo
            {
                FileName = CliLocator.Claude,
                Arguments = args.ToString(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(request.WorkingDirectory))
            {
                psi.WorkingDirectory = request.WorkingDirectory;
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            // Register cancellation to kill the process tree
            using var ctReg = cts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { /* process may have already exited */ }
            });

            await process.WaitForExitAsync(cts.Token);

            var output = await outputTask;
            var stderr = await errorTask;

            sw.Stop();

            if (process.ExitCode != 0)
            {
                this.logger.LogError("Claude CLI failed (exit={ExitCode}): {Stderr}", process.ExitCode, stderr);
                return new AIExecutionResult
                {
                    Success = false,
                    Error = $"Claude CLI exited with code {process.ExitCode}: {stderr}",
                    Duration = sw.Elapsed,
                    ModelUsed = request.Model,
                };
            }

            return new AIExecutionResult
            {
                Success = true,
                Content = output.Trim(),
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new AIExecutionResult
            {
                Success = false,
                Error = "AI execution timed out or was cancelled.",
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            this.logger.LogError(ex, "Claude CLI execution failed");
            return new AIExecutionResult
            {
                Success = false,
                Error = $"Claude CLI error: {ex.Message}",
                Duration = sw.Elapsed,
                ModelUsed = request.Model,
            };
        }
    }
}