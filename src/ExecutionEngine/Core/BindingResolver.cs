// -----------------------------------------------------------------------
// <copyright file="BindingResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Core;

using System.Text.RegularExpressions;
using ExecutionEngine.Contexts;

/// <summary>
/// Centralized binding expression resolver used by all node types.
/// <para>
/// Expression syntax (case-insensitive prefixes):
/// <list type="bullet">
///   <item><c>static:value</c> — literal string</item>
///   <item><c>input.key</c> / <c>output.key</c> — InputData[key] (upstream output = current input)</item>
///   <item><c>global.key</c> — workflow Variables[key]</item>
///   <item><c>error.key</c> — InputData[key] (error-flow fields)</item>
///   <item><c>bareKey</c> — InputData → body-prefixed InputData → Variables (fallback chain)</item>
/// </list>
/// </para>
/// </summary>
public static class BindingResolver
{
    private static readonly Regex PlaceholderRegex = new(@"\{([\w.]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Resolve a single binding expression to a value.
    /// </summary>
    public static object? Resolve(
        string expression,
        NodeExecutionContext nodeContext,
        WorkflowExecutionContext workflowContext)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return null;
        }

        // 1. Literal static value
        if (expression.StartsWith("static:", StringComparison.OrdinalIgnoreCase))
        {
            return expression[7..];
        }

        // 1b. "field:" prefix — strip and resolve the inner expression
        if (expression.StartsWith("field:", StringComparison.OrdinalIgnoreCase))
        {
            var inner = expression[6..];
            if (string.IsNullOrEmpty(inner))
            {
                return null;
            }

            return Resolve(inner, nodeContext, workflowContext);
        }

        // 1c. "dynamic:" prefix — C# template expression with {placeholder} resolution
        if (expression.StartsWith("dynamic:", StringComparison.OrdinalIgnoreCase))
        {
            var template = expression[8..];
            if (string.IsNullOrEmpty(template))
            {
                return null;
            }

            return ResolveTemplate(template, null, nodeContext, workflowContext);
        }

        // 2. Explicit input/output prefix → strip and search InputData
        if (expression.StartsWith("input.", StringComparison.OrdinalIgnoreCase))
        {
            var key = expression[6..];
            return LookupInputData(key, nodeContext);
        }

        if (expression.StartsWith("output.", StringComparison.OrdinalIgnoreCase))
        {
            var key = expression[7..];
            return LookupInputData(key, nodeContext);
        }

        // 3. Global prefix → strip and search Variables
        if (expression.StartsWith("global.", StringComparison.OrdinalIgnoreCase))
        {
            var key = expression[7..];
            if (workflowContext.Variables.TryGetValue(key, out var value))
            {
                return value;
            }

            return null;
        }

        // 4. Error prefix → strip and search InputData
        if (expression.StartsWith("error.", StringComparison.OrdinalIgnoreCase))
        {
            var key = expression[6..];
            return LookupInputData(key, nodeContext);
        }

        // 5. Bare key — deterministic fallback chain:
        //    InputData[key] → InputData["body.key"] → Variables[key]
        return ResolveBareKey(expression, nodeContext, workflowContext);
    }

    /// <summary>
    /// Resolve a single binding expression to its string representation.
    /// </summary>
    public static string ResolveToString(
        string expression,
        NodeExecutionContext nodeContext,
        WorkflowExecutionContext workflowContext)
    {
        var value = Resolve(expression, nodeContext, workflowContext);
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Resolve all <c>{placeholder}</c> tokens in a template string.
    /// Placeholders may contain dots (e.g. <c>{ai.inputTokens}</c>).
    /// Each placeholder is looked up via the optional <paramref name="bindings"/>
    /// dictionary first; if a binding exists, its expression is resolved.
    /// Otherwise the bare placeholder name goes through the standard fallback chain.
    /// </summary>
    public static string ResolveTemplate(
        string template,
        IReadOnlyDictionary<string, string>? bindings,
        NodeExecutionContext nodeContext,
        WorkflowExecutionContext workflowContext)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            var name = match.Groups[1].Value;

            // Explicit binding → resolve the bound expression
            if (bindings != null && bindings.TryGetValue(name, out var expression))
            {
                var resolved = Resolve(expression, nodeContext, workflowContext);
                if (resolved != null)
                {
                    return resolved.ToString() ?? string.Empty;
                }

                // Binding expression didn't resolve — keep placeholder
                return match.Value;
            }

            // No binding — try bare key resolution
            var value = ResolveBareKey(name, nodeContext, workflowContext);
            if (value != null)
            {
                return value.ToString() ?? string.Empty;
            }

            // Unresolved — keep as-is
            return match.Value;
        });
    }

    /// <summary>
    /// Bare key fallback chain: InputData → body-prefixed InputData → Variables.
    /// </summary>
    private static object? ResolveBareKey(
        string key,
        NodeExecutionContext nodeContext,
        WorkflowExecutionContext workflowContext)
    {
        // Direct match in input data (handles literal keys like "body.incidentId", "ai.inputTokens")
        if (nodeContext.InputData.TryGetValue(key, out var inputValue))
        {
            return inputValue;
        }

        // Webhook body fallback: "incidentId" → InputData["body.incidentId"]
        if (nodeContext.InputData.TryGetValue($"body.{key}", out var bodyValue))
        {
            return bodyValue;
        }

        // Global variables fallback
        if (workflowContext.Variables.TryGetValue(key, out var globalValue))
        {
            return globalValue;
        }

        return null;
    }

    /// <summary>
    /// Look up a key in InputData, trying the exact key first, then body-prefixed.
    /// </summary>
    private static object? LookupInputData(string key, NodeExecutionContext nodeContext)
    {
        if (nodeContext.InputData.TryGetValue(key, out var value))
        {
            return value;
        }

        // Also try body-prefixed (webhook fields stored as "body.key")
        if (nodeContext.InputData.TryGetValue($"body.{key}", out var bodyValue))
        {
            return bodyValue;
        }

        return null;
    }
}
