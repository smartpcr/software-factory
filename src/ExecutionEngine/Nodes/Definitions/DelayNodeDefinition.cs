// -----------------------------------------------------------------------
// <copyright file="DelayNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a wait/pause node.
/// </summary>
public class DelayNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Delay;

    /// <summary>
    /// Gets or sets the delay duration.
    /// </summary>
    public int Duration { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the unit of the delay duration.
    /// </summary>
    public string Unit { get; set; } = "ms";

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.Duration < 0)
        {
            yield return new ValidationResult("Duration must be non-negative.", new[] { nameof(this.Duration) });
        }
    }
}