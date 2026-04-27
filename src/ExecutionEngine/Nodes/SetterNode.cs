// -----------------------------------------------------------------------
// <copyright file="SetterNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes
{
    using ExecutionEngine.AI;
    using ExecutionEngine.Contexts;
    using ExecutionEngine.Core;
    using ExecutionEngine.Enums;
    using ExecutionEngine.Nodes.Definitions;
    using Microsoft.CodeAnalysis.CSharp.Scripting;
    using Microsoft.CodeAnalysis.Scripting;
    using Microsoft.Extensions.DependencyInjection;

    public class SetterNode : ExecutableNodeBase
    {
        public override void Initialize(NodeDefinition definition)
        {
            if (definition is not SetterNodeDefinition)
            {
                throw new ArgumentException($"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(SetterNodeDefinition).FullName}.");
            }

            this.Definition = definition;
        }

        public override async Task<NodeInstance> ExecuteAsync(WorkflowExecutionContext workflowContext, NodeExecutionContext nodeContext, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            // Read variable mappings and set/increment global variables from input data
            var setterDef = (SetterNodeDefinition)this.Definition!;
            var aiLogger = workflowContext.ServiceProvider?.GetService<IAINodeLogger>();
            if (setterDef.VariableMappings != null)
            {
                foreach (var (globalKey, inputExpr) in setterDef.VariableMappings)
                {
                    var op = "set";
                    if (setterDef.VariableOps != null && setterDef.VariableOps.TryGetValue(globalKey, out var opVal))
                    {
                        op = opVal;
                    }

                    if (op == "expr")
                    {
                        // Check if expression needs Roslyn (method calls, arithmetic, comparisons)
                        // vs simple string/path concatenation ({folder} + "\file.md")
                        var hasMethodCalls = inputExpr.Contains('(') && inputExpr.Contains(')');
                        var hasArithmetic = System.Text.RegularExpressions.Regex.IsMatch(
                            inputExpr, @"[+\-*/]\s*\d|[+\-*/]\s*{");
                        var isPathConcat = !hasMethodCalls && !hasArithmetic
                            && inputExpr.Contains('+')
                            && System.Text.RegularExpressions.Regex.IsMatch(inputExpr, @"""[^""]*\\");

                        if (isPathConcat)
                        {
                            // Simple path concatenation: {folder} + "\file.md"
                            var resolved = BindingResolver.ResolveTemplate(inputExpr, null, nodeContext, workflowContext);
                            resolved = EvaluateStringConcatenation(resolved);
                            workflowContext.Variables[globalKey] = resolved;
                        }
                        else
                        {
                            // Complex/arithmetic expression — evaluate with Roslyn
                            try
                            {
                                var state = CreateExecutionState(workflowContext, nodeContext);
                                var processedExpr = ExecutionState.PreProcessExpression(inputExpr);
                                var scriptOptions = ScriptOptions.Default
                                    .AddReferences(typeof(ExecutionState).Assembly, typeof(Math).Assembly)
                                    .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic");
                                var result = await CSharpScript.EvaluateAsync<object>(
                                    processedExpr, scriptOptions, state, typeof(ExecutionState), cancellationToken);
                                workflowContext.Variables[globalKey] = result ?? "";
                            }
                            catch
                            {
                                // Roslyn failed — fallback to template resolution
                                var resolved = BindingResolver.ResolveTemplate(inputExpr, null, nodeContext, workflowContext);
                                resolved = EvaluateStringConcatenation(resolved);
                                workflowContext.Variables[globalKey] = resolved;
                            }
                        }
                    }
                    else
                    {
                        var value = BindingResolver.Resolve(inputExpr, nodeContext, workflowContext);

                        if (op == "increment")
                        {
                            // Default increment by 1 when source is empty/null
                            var addend = value != null ? Convert.ToDouble(value) : 1.0;
                            if (workflowContext.Variables.TryGetValue(globalKey, out var existing))
                            {
                                var current = Convert.ToDouble(existing);
                                workflowContext.Variables[globalKey] = current + addend;
                            }
                            else
                            {
                                workflowContext.Variables[globalKey] = addend;
                            }
                        }
                        else
                        {
                            if (value == null) continue;
                            workflowContext.Variables[globalKey] = value;
                        }
                    }

                    nodeContext.OutputData[globalKey] = workflowContext.Variables[globalKey];

                    // Emit log so the UI shows each variable assignment
                    var displayVal = FormatValueForLog(workflowContext.Variables[globalKey]);
                    aiLogger?.EmitLine(workflowContext.InstanceId, this.NodeId, $"  {globalKey} {(op == "increment" ? "+=" : "=")} {displayVal}");
                }
            }

            await Task.Delay(10, cancellationToken);
            return new NodeInstance
            {
                NodeId = this.Definition!.NodeId,
                Status = NodeExecutionStatus.Completed,
                EndTime = DateTime.UtcNow,
                ExecutionContext = nodeContext,
                WorkflowInstanceId = workflowContext.InstanceId,
                StartTime = startTime,
            };
        }

        private static string FormatValueForLog(object value)
        {
            if (value is string s)
            {
                return s.Length > 120 ? $"\"{s[..117]}…\"" : $"\"{s}\"";
            }

            return value?.ToString() ?? "null";
        }

        /// <summary>
        /// Evaluates simple string concatenation patterns left over after template resolution.
        /// Handles: <c>someValue + "literal" + "literal"</c> → <c>someValueliteralliteral</c>.
        /// Also handles single-quoted strings and trims whitespace around the + operator.
        /// </summary>
        private static string EvaluateStringConcatenation(string input)
        {
            if (!input.Contains('+'))
            {
                return input;
            }

            // Split on + and concatenate, stripping quotes from string literals
            var parts = input.Split('+');
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();

                // Strip surrounding double quotes: "value" → value
                if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
                {
                    sb.Append(trimmed[1..^1]);
                }
                // Strip surrounding single quotes: 'value' → value
                else if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
                {
                    sb.Append(trimmed[1..^1]);
                }
                else
                {
                    // Unquoted part (resolved placeholder value) — use as-is
                    sb.Append(trimmed);
                }
            }

            return sb.ToString();
        }
    }
}