// -----------------------------------------------------------------------
// <copyright file="AssertNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes
{
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using ExecutionEngine.AI;
    using ExecutionEngine.Contexts;
    using ExecutionEngine.Core;
    using ExecutionEngine.Enums;
    using ExecutionEngine.Nodes.Definitions;
    using Microsoft.CodeAnalysis.CSharp.Scripting;
    using Microsoft.CodeAnalysis.Scripting;
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// Evaluates a condition expression and fails the node if the condition is not met.
    /// Uses Roslyn C# scripting for full expression support (Directory.Exists, String methods, etc.).
    /// Falls back to simple comparison when Roslyn is unavailable.
    /// </summary>
    public class AssertNode : ExecutableNodeBase
    {
        private static readonly ScriptOptions DefaultScriptOptions = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(System.IO.File).Assembly,
                typeof(System.IO.Directory).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Regex).Assembly,
                typeof(List<>).Assembly)
            .AddImports(
                "System",
                "System.IO",
                "System.Linq",
                "System.Collections.Generic",
                "System.Text",
                "System.Text.RegularExpressions");

        public override void Initialize(NodeDefinition definition)
        {
            if (definition is not AssertNodeDefinition)
            {
                throw new ArgumentException(
                    $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(AssertNodeDefinition).FullName}.");
            }

            this.Definition = definition;
        }

        public override async Task<NodeInstance> ExecuteAsync(
            WorkflowExecutionContext workflowContext,
            NodeExecutionContext nodeContext,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            var assertDef = (AssertNodeDefinition)this.Definition!;
            var aiLogger = workflowContext.ServiceProvider?.GetService<IAINodeLogger>();

            // Resolve the condition expression with C#-aware quoting.
            // Placeholders inside method calls like Directory.Exists({path}) need string quotes.
            var resolvedCondition = ResolveCSharpTemplate(
                assertDef.Condition, nodeContext, workflowContext);

            nodeContext.OutputData["condition"] = assertDef.Condition;
            nodeContext.OutputData["resolvedCondition"] = resolvedCondition;

            // Check for unresolved placeholders — if any {placeholder} remains, the field is missing
            var unresolvedMatch = System.Text.RegularExpressions.Regex.Match(
                resolvedCondition, @"\{(\w[\w.]*)\}");
            if (unresolvedMatch.Success)
            {
                var missingField = unresolvedMatch.Groups[1].Value;
                var message = !string.IsNullOrEmpty(assertDef.Message)
                    ? BindingResolver.ResolveTemplate(assertDef.Message, null, nodeContext, workflowContext)
                    : $"Assertion failed: field '{{{missingField}}}' is not available (not in input data or global variables)";

                aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId, $"❌ {message}");
                nodeContext.OutputData["passed"] = false;
                nodeContext.OutputData["errorMessage"] = message;

                return new NodeInstance
                {
                    NodeId = this.Definition!.NodeId,
                    Status = NodeExecutionStatus.Failed,
                    ErrorMessage = message,
                    EndTime = DateTime.UtcNow,
                    ExecutionContext = nodeContext,
                    WorkflowInstanceId = workflowContext.InstanceId,
                    StartTime = startTime,
                };
            }

            // Evaluate the condition using Roslyn C# scripting
            bool passed;
            string? evalError = null;
            try
            {
                var result = await CSharpScript.EvaluateAsync<object>(
                    resolvedCondition,
                    DefaultScriptOptions,
                    cancellationToken: cancellationToken);

                passed = IsTruthy(result);
            }
            catch (CompilationErrorException ex)
            {
                // Roslyn couldn't compile — fall back to simple evaluator only for basic comparisons
                evalError = ex.Message;
                // If expression looks like a method call or complex expression, don't fall back
                if (resolvedCondition.Contains('(') || resolvedCondition.Contains('.'))
                {
                    passed = false;
                }
                else
                {
                    passed = EvaluateSimple(resolvedCondition);
                }
            }
            catch (Exception ex)
            {
                // Runtime error in the expression (e.g. path not found) — treat as failure
                evalError = ex.Message;
                passed = false;
            }

            nodeContext.OutputData["passed"] = passed;
            if (evalError != null)
            {
                nodeContext.OutputData["evalError"] = evalError;
            }

            if (passed)
            {
                aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId, $"✅ Assert passed: {resolvedCondition}");
            }
            else
            {
                var message = !string.IsNullOrEmpty(assertDef.Message)
                    ? BindingResolver.ResolveTemplate(assertDef.Message, null, nodeContext, workflowContext)
                    : evalError != null
                        ? $"Assertion failed: {resolvedCondition} ({evalError})"
                        : $"Assertion failed: {resolvedCondition}";

                aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId, $"❌ {message}");
                nodeContext.OutputData["errorMessage"] = message;
            }

            return new NodeInstance
            {
                NodeId = this.Definition!.NodeId,
                Status = passed ? NodeExecutionStatus.Completed : NodeExecutionStatus.Failed,
                ErrorMessage = passed ? null : (nodeContext.OutputData["errorMessage"]?.ToString() ?? "Assertion failed"),
                EndTime = DateTime.UtcNow,
                ExecutionContext = nodeContext,
                WorkflowInstanceId = workflowContext.InstanceId,
                StartTime = startTime,
            };
        }

        /// <summary>Converts a script result to boolean.</summary>
        private static bool IsTruthy(object? result)
        {
            return result switch
            {
                bool b => b,
                int i => i != 0,
                long l => l != 0,
                double d => d != 0,
                string s => !string.IsNullOrEmpty(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
                null => false,
                _ => true,
            };
        }

        /// <summary>Simple fallback evaluator for when Roslyn can't compile the expression.</summary>
        private static bool EvaluateSimple(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return false;
            }

            var trimmed = condition.Trim();

            // Try comparison operators
            string[] operators = ["!=", ">=", "<=", "==", ">", "<"];
            foreach (var op in operators)
            {
                var idx = trimmed.IndexOf(op, StringComparison.Ordinal);
                if (idx > 0)
                {
                    var left = trimmed[..idx].Trim().Trim('"', '\'');
                    var right = trimmed[(idx + op.Length)..].Trim().Trim('"', '\'');

                    if (double.TryParse(left, out var ln) && double.TryParse(right, out var rn))
                    {
                        return op switch
                        {
                            "==" => Math.Abs(ln - rn) < 0.0001,
                            "!=" => Math.Abs(ln - rn) >= 0.0001,
                            ">" => ln > rn,
                            "<" => ln < rn,
                            ">=" => ln >= rn,
                            "<=" => ln <= rn,
                            _ => false,
                        };
                    }

                    var cmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                    return op switch
                    {
                        "==" => cmp == 0,
                        "!=" => cmp != 0,
                        _ => false,
                    };
                }
            }

            // Bare truthy check
            return !string.IsNullOrEmpty(trimmed)
                && !trimmed.Equals("0", StringComparison.Ordinal)
                && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Equals("null", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves {placeholder} values in a C# expression, auto-quoting string values
        /// so they become valid C# string literals.
        /// E.g. <c>Directory.Exists({incidentFolder})</c> → <c>Directory.Exists("E:\\icms\\...")</c>
        /// but <c>{count} > 0</c> → <c>5 > 0</c> (numbers stay unquoted).
        /// </summary>
        private static string ResolveCSharpTemplate(
            string template,
            NodeExecutionContext nodeContext,
            WorkflowExecutionContext workflowContext)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            var regex = new Regex(@"\{([\w.]+)\}");
            return regex.Replace(template, match =>
            {
                var name = match.Groups[1].Value;
                var value = BindingResolver.Resolve(name, nodeContext, workflowContext);
                if (value == null)
                {
                    // Also try bare key resolution
                    value = ResolveBareKeyFallback(name, nodeContext, workflowContext);
                }

                if (value == null)
                {
                    return match.Value; // keep unresolved
                }

                var str = value.ToString() ?? "";

                // Numbers and booleans stay unquoted for valid C# expressions
                if (value is int or long or double or float or decimal)
                {
                    return str;
                }

                if (value is bool b)
                {
                    return b ? "true" : "false";
                }

                // Strings get quoted with backslash escaping
                return "\"" + str.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            });
        }

        private static object? ResolveBareKeyFallback(
            string key,
            NodeExecutionContext nodeContext,
            WorkflowExecutionContext workflowContext)
        {
            if (nodeContext.InputData.TryGetValue(key, out var inputVal)) return inputVal;
            if (nodeContext.InputData.TryGetValue($"body.{key}", out var bodyVal)) return bodyVal;
            if (workflowContext.Variables.TryGetValue(key, out var globalVal)) return globalVal;
            return null;
        }
    }
}
