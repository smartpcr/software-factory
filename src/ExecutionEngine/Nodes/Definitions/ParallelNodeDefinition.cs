// -----------------------------------------------------------------------
// <copyright file="ParallelNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a fan-out parallel branches node.
/// </summary>
public class ParallelNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Parallel;

    /// <summary>
    /// Gets or sets the join mode (all or any).
    /// </summary>
    public string JoinMode { get; set; } = "all";

    /// <summary>
    /// Gets or sets the timeout in milliseconds.
    /// </summary>
    public int Timeout { get; set; } = 30000;

    /// <summary>
    /// Gets or sets the list of branch names.
    /// </summary>
    public List<string> Branches { get; set; } = ["branch_1", "branch_2"];

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.Branches == null || this.Branches.Count < 2)
        {
            yield return new ValidationResult("At least two branches are required.", new[] { nameof(this.Branches) });
        }
    }
}