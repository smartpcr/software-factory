// -----------------------------------------------------------------------
// <copyright file="AINodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for an AI skill/subagent node.
/// </summary>
public class AINodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.AI;

    /// <summary>
    /// Gets or sets the AI execution mode.
    /// </summary>
    public AINodeMode Mode { get; set; } = AINodeMode.Skill;

    /// <summary>
    /// Gets or sets the AI backend (Copilot or Claude).
    /// </summary>
    public AIBackend Backend { get; set; } = AIBackend.Copilot;

    /// <summary>
    /// Gets or sets the skill name (required when Mode is Skill).
    /// </summary>
    public string? SkillName { get; set; }

    /// <summary>
    /// Gets or sets the sub-agent name (required when Mode is SubAgent).
    /// </summary>
    public string? SubAgentName { get; set; }

    /// <summary>
    /// Gets or sets the AI model to use.
    /// </summary>
    public AIModelId Model { get; set; } = AIModelId.ClaudeOpus46_1M;

    /// <summary>
    /// Gets or sets the user prompt (required when Mode is RawPrompt).
    /// </summary>
    public string? UserPrompt { get; set; }

    /// <summary>
    /// Gets or sets the system prompt (optional).
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Gets or sets the input mapping expression.
    /// </summary>
    public string InputMapping { get; set; } = "input";

    /// <summary>
    /// Gets or sets bindings that map skill/agent input parameter names to input data paths.
    /// Key = parameter name (e.g. "incidentId"), Value = input expression (e.g. "body.incidentId").
    /// </summary>
    public Dictionary<string, string> InputBindings { get; set; } = [];

    /// <summary>
    /// Gets or sets the output key name.
    /// </summary>
    public string OutputKey { get; set; } = "result";

    /// <summary>
    /// Gets or sets the confidence level for AI generation (0–100).
    /// </summary>
    public int Confidence { get; set; } = 70;

    /// <summary>
    /// Gets or sets the maximum tokens for AI generation.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the execution timeout in minutes.
    /// If no streaming events are received within this period, the execution is terminated.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets the max retries on timeout errors only. 0 = no retry (default).
    /// Each retry gets a fresh timeout budget.
    /// </summary>
    public int TimeoutRetries { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to fail the node when max timeout retries are exceeded.
    /// When false (default), the node fails. When true, it completes with partial results.
    /// </summary>
    public bool ContinueOnTimeoutExhausted { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum number of agentic turns (tool call rounds).
    /// Limits how many continuation rounds the AI can make. Default is 30.
    /// Set to 0 for unlimited (not recommended — can burn tokens on idle exploration).
    /// </summary>
    public int MaxTurns { get; set; } = 30;

    /// <summary>
    /// Gets or sets the working directory for skill/agent execution.
    /// Supports binding expressions (e.g. <c>{incidentFolder}</c>).
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.Mode == AINodeMode.Skill && string.IsNullOrWhiteSpace(this.SkillName))
        {
            yield return new ValidationResult("SkillName is required in Skill mode.", new[] { nameof(this.SkillName) });
        }

        if (this.Mode == AINodeMode.SubAgent && string.IsNullOrWhiteSpace(this.SubAgentName))
        {
            yield return new ValidationResult("SubAgentName is required in SubAgent mode.", new[] { nameof(this.SubAgentName) });
        }

        if (this.Mode == AINodeMode.RawPrompt && string.IsNullOrWhiteSpace(this.UserPrompt))
        {
            yield return new ValidationResult("UserPrompt is required in RawPrompt mode.", new[] { nameof(this.UserPrompt) });
        }
    }
}