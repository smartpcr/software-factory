// -----------------------------------------------------------------------
// <copyright file="EndNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a workflow terminator node.
/// </summary>
public class EndNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.End;

    /// <summary>
    /// Gets or sets the termination status.
    /// </summary>
    public string Status { get; set; } = "success";

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        yield break;
    }
}