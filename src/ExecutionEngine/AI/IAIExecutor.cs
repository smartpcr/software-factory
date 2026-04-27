// -----------------------------------------------------------------------
// <copyright file="IAIExecutor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

using ExecutionEngine.Enums;

/// <summary>
/// Executes an AI prompt against a backend (Copilot SDK or Claude CLI).
/// </summary>
public interface IAIExecutor
{
    /// <summary>
    /// Gets the backend this executor targets.
    /// </summary>
    AIBackend Backend { get; }

    /// <summary>
    /// Executes an AI request and returns the result.
    /// </summary>
    Task<AIExecutionResult> ExecuteAsync(AIExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Request payload for AI execution.
/// </summary>
public class AIExecutionRequest
{
    /// <summary>Gets or sets the user/task prompt.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the system prompt (agent/skill instructions).</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Gets or sets the model identifier (wire-format, e.g. "claude-opus-4.6").</summary>
    public string Model { get; set; } = "claude-opus-4.6";

    /// <summary>Gets or sets the maximum tokens for the response.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Gets or sets the temperature (0.0–2.0).</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Gets or sets an optional working directory for the agent.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets a callback invoked for each line of output during execution (for streaming to UI).</summary>
    public Action<string>? OnOutputLine { get; set; }

    /// <summary>Gets or sets the timeout for this request.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the max agentic turns (tool call rounds). 0 = unlimited.</summary>
    public int MaxTurns { get; set; } = 30;
}

/// <summary>
/// Result from AI execution.
/// </summary>
public class AIExecutionResult
{
    /// <summary>Gets or sets whether the execution succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the response content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets any error message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the model that was used.</summary>
    public string? ModelUsed { get; set; }

    /// <summary>Gets or sets the execution duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Gets or sets the number of input tokens used.</summary>
    public int InputTokens { get; set; }

    /// <summary>Gets or sets the number of output tokens generated.</summary>
    public int OutputTokens { get; set; }
}