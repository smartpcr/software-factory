// -----------------------------------------------------------------------
// <copyright file="ExecutionState.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Core;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ExecutionEngine.Contexts;

/// <summary>
/// Shared execution state contract accessible by both C# and PowerShell scripts.
/// Provides unified access to workflow context, node context, and helper functions.
/// </summary>
public class ExecutionState
{
    private static readonly Regex PlaceholderPattern = new(@"\{([\w.]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Gets or sets the workflow execution context.
    /// </summary>
    public WorkflowExecutionContext WorkflowContext { get; set; } = null!;

    /// <summary>
    /// Gets or sets the node execution context.
    /// </summary>
    public NodeExecutionContext NodeContext { get; set; } = null!;

    /// <summary>
    /// Gets or sets the workflow-level global variables.
    /// </summary>
    public ConcurrentDictionary<string, object> GlobalVariables { get; set; } = new();

    /// <summary>
    /// Gets or sets the input data from previous node.
    /// </summary>
    public Dictionary<string, object> Input { get; set; } = [];

    /// <summary>
    /// Gets or sets the local variables for this node.
    /// </summary>
    public ConcurrentDictionary<string, object> Local { get; set; } = new();

    /// <summary>
    /// Gets or sets the output data for downstream nodes.
    /// </summary>
    public Dictionary<string, object> Output { get; set; } = [];

    /// <summary>
    /// Helper function to set output value.
    /// </summary>
    public Action<string, object> SetOutput { get; set; } = null!;

    /// <summary>
    /// Helper function to get input value.
    /// </summary>
    public Func<string, object?> GetInput { get; set; } = null!;

    /// <summary>
    /// Helper function to get global variable value.
    /// </summary>
    public Func<string, object?> GetGlobal { get; set; } = null!;

    /// <summary>
    /// Helper function to set global variable value.
    /// </summary>
    public Action<string, object> SetGlobal { get; set; } = null!;

    /// <summary>
    /// Resolve a variable by name using the standard fallback chain:
    /// Input → body-prefixed Input → GlobalVariables.
    /// Usable in Roslyn expressions as <c>(dynamic)Get("varName")</c>.
    /// String values that look numeric are auto-converted to support comparison operators.
    /// </summary>
    public object? Get(string key)
    {
        object? raw = null;
        if (this.Input.TryGetValue(key, out var inputVal))
        {
            raw = inputVal;
        }
        else if (this.Input.TryGetValue($"body.{key}", out var bodyVal))
        {
            raw = bodyVal;
        }
        else if (this.GlobalVariables.TryGetValue(key, out var globalVal))
        {
            raw = globalVal;
        }

        // Auto-convert string values that are numeric so comparison operators work
        if (raw is string s)
        {
            if (long.TryParse(s, out var l)) return l;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            if (bool.TryParse(s, out var b)) return b;
        }

        return raw;
    }

    /// <summary>
    /// Pre-process a condition/expression string: replace <c>{varName}</c> placeholders
    /// with <c>(dynamic)Get("varName")</c> so Roslyn can compile the expression.
    /// Pass-through if no placeholders are found (already valid C#).
    /// </summary>
    public static string PreProcessExpression(string expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return expression;
        }

        return PlaceholderPattern.Replace(expression, match =>
        {
            var name = match.Groups[1].Value;
            return $"((dynamic)Get(\"{name}\"))";
        });
    }
}