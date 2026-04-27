// -----------------------------------------------------------------------
// <copyright file="ActionNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a generic action/task node.
/// </summary>
public class ActionNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Action;

    /// <summary>
    /// Gets or sets the type of action to perform.
    /// </summary>
    public string ActionType { get; set; } = "function";

    /// <summary>
    /// Gets or sets the handler for the action.
    /// </summary>
    public string Handler { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        yield break;
    }
}