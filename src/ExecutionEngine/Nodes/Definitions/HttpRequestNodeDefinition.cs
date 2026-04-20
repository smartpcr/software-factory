// -----------------------------------------------------------------------
// <copyright file="HttpRequestNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for an HTTP API call node.
/// </summary>
public class HttpRequestNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.HttpRequest;

    /// <summary>
    /// Gets or sets the HTTP method.
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Gets or sets the URL to call.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the headers as a JSON string.
    /// </summary>
    public string Headers { get; set; } = "{}";

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(this.Url))
        {
            yield return new ValidationResult("URL is required.", new[] { nameof(this.Url) });
        }
    }
}