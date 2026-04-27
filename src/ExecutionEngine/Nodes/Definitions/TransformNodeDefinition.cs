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
/// Supports multiple modes: Aggregate, Extract, ParseJson, ParseCsv, ReadFile, Expression.
/// </summary>
public class TransformNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Transform;

    /// <summary>
    /// Gets or sets the transform mode.
    /// </summary>
    public string Mode { get; set; } = "Expression";

    /// <summary>
    /// Gets or sets the transformation expression (C# code with TransformHelpers available).
    /// </summary>
    public string Expression { get; set; } = "input";

    /// <summary>
    /// Gets or sets the output key name for the result.
    /// </summary>
    public string OutputKey { get; set; } = "result";

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(this.Expression))
        {
            yield return new ValidationResult("Expression is required.", new[] { nameof(this.Expression) });
        }
    }
}