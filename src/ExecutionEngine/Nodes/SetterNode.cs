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

            // Read variable mappings and set global variables from input data
            var setterDef = (SetterNodeDefinition)this.Definition!;
            if (setterDef.VariableMappings != null)
            {
                foreach (var (globalKey, inputExpr) in setterDef.VariableMappings)
                {
                    var value = BindingResolver.Resolve(inputExpr, nodeContext, workflowContext);
                    if (value != null)
                    {
                        workflowContext.Variables[globalKey] = value;
                        nodeContext.OutputData[globalKey] = value;
                    }
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