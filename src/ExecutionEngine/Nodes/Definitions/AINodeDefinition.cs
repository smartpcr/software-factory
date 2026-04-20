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
    /// Gets or sets the AI mode (skill or subagent).
    /// </summary>
    public string Mode { get; set; } = "skill";

    /// <summary>
    /// Gets or sets the skill identifier (required when Mode is "skill").
    /// </summary>
    public string? SkillId { get; set; }

    /// <summary>
    /// Gets or sets the subagent identifier (required when Mode is "subagent").
    /// </summary>
    public string? SubagentId { get; set; }

    /// <summary>
    /// Gets or sets the AI model to use.
    /// </summary>
    public string Model { get; set; } = "claude-opus-4-7";

    /// <summary>
    /// Gets or sets the system prompt.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Gets or sets the input mapping expression.
    /// </summary>
    public string InputMapping { get; set; } = "input";

    /// <summary>
    /// Gets or sets the output key name.
    /// </summary>
    public string OutputKey { get; set; } = "result";

    /// <summary>
    /// Gets or sets the temperature for AI generation.
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Gets or sets the maximum tokens for AI generation.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.Mode == "skill" && string.IsNullOrWhiteSpace(this.SkillId))
        {
            yield return new ValidationResult("SkillId is required in skill mode.", new[] { nameof(this.SkillId) });
        }

        if (this.Mode == "subagent" && string.IsNullOrWhiteSpace(this.SubagentId))
        {
            yield return new ValidationResult("SubagentId is required in subagent mode.", new[] { nameof(this.SubagentId) });
        }
    }
}