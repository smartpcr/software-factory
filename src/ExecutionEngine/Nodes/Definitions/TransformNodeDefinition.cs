// -----------------------------------------------------------------------
// <copyright file="TransformNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a data transformation node.
/// </summary>
public class TransformNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Transform;

    /// <summary>
    /// Gets or sets the transformation expression.
    /// </summary>
    public string Expression { get; set; } = "input";

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(this.Expression))
        {
            yield return new ValidationResult("Expression is required.", new[] { nameof(this.Expression) });
        }
    }
}