// -----------------------------------------------------------------------
// <copyright file="TriggerNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for an HTTP webhook trigger node.
/// </summary>
public class TriggerNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Trigger;

    /// <summary>
    /// Gets or sets the HTTP method for the trigger.
    /// </summary>
    public string Method { get; set; } = "POST";

    /// <summary>
    /// Gets or sets the route path for the webhook.
    /// </summary>
    public string Route { get; set; } = "/webhook";

    /// <summary>
    /// Gets or sets optional parameters for the trigger.
    /// </summary>
    public string? Parameters { get; set; }

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(this.Route))
        {
            yield return new ValidationResult("Route is required.", new[] { nameof(this.Route) });
        }
    }
}