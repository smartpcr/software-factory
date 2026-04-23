// -----------------------------------------------------------------------
// <copyright file="SetterNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes
{
    using ExecutionEngine.Contexts;
    using ExecutionEngine.Core;
    using ExecutionEngine.Enums;
    using ExecutionEngine.Nodes.Definitions;

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
            if (setterDef.VariableMappings != null)
            {
                foreach (var (globalKey, inputExpr) in setterDef.VariableMappings)
                {
                    var value = BindingResolver.Resolve(inputExpr, nodeContext, workflowContext);
                    if (value == null) continue;

                    var op = "set";
                    if (setterDef.VariableOps != null && setterDef.VariableOps.TryGetValue(globalKey, out var opVal))
                    {
                        op = opVal;
                    }

                    if (op == "increment")
                    {
                        var addend = Convert.ToDouble(value);
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
                    else if (op == "expr")
                    {
                        // Expression mode: the inputExpr is a C# template with {placeholders}
                        var resolved = BindingResolver.ResolveTemplate(inputExpr, null, nodeContext, workflowContext);
                        workflowContext.Variables[globalKey] = resolved;
                    }
                    else
                    {
                        workflowContext.Variables[globalKey] = value;
                    }

                    nodeContext.OutputData[globalKey] = workflowContext.Variables[globalKey];
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
    }
}