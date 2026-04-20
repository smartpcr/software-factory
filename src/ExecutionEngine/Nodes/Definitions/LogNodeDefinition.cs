// -----------------------------------------------------------------------
// <copyright file="LogNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a log message output node.
/// Message supports template syntax with placeholders: {paramName}
/// Placeholders are resolved from input data at runtime.
/// </summary>
public class LogNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Log;

    /// <summary>
    /// Gets or sets the log level.
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>
    /// Gets or sets the message template with optional {placeholder} bindings.
    /// Example: "User {userId} performed {action} on {resource}"
    /// </summary>
    [Required]
    public string MessageTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parameter bindings that map template placeholders to input expressions.
    /// Key = placeholder name (without braces), Value = input expression (e.g. "input.userId").
    /// If a placeholder has no explicit binding, it is resolved from input data by matching key name.
    /// </summary>
    public Dictionary<string, string> ParameterBindings { get; set; } = [];

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(this.MessageTemplate))
        {
            yield return new ValidationResult(
                "Message template is required.",
                new[] { nameof(this.MessageTemplate) });
        }
    }

    /// <summary>
    /// Extracts placeholder names from the message template.
    /// </summary>
    public static IReadOnlyList<string> ExtractPlaceholders(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return [];
        }

        return Regex.Matches(template, @"\{(\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }
}