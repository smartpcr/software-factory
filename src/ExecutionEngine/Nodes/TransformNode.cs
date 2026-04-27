// -----------------------------------------------------------------------
// <copyright file="TransformNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes;

using ExecutionEngine.Contexts;
using ExecutionEngine.Core;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

/// <summary>
/// Data transformation node. Evaluates a C# expression with TransformHelpers available.
/// </summary>
public class TransformNode : ExecutableNodeBase
{
    /// <inheritdoc/>
    public override void Initialize(NodeDefinition definition)
    {
        if (definition is not TransformNodeDefinition)
        {
            throw new ArgumentException(
                $"Invalid node definition type: {definition.GetType().FullName}. Expected {typeof(TransformNodeDefinition).FullName}.");
        }

        this.Definition = definition;
    }

    /// <inheritdoc/>
    public override async Task<NodeInstance> ExecuteAsync(
        WorkflowExecutionContext workflowContext,
        NodeExecutionContext nodeContext,
        CancellationToken cancellationToken)
    {
        var transformDef = (TransformNodeDefinition)this.Definition!;
        var startTime = DateTime.UtcNow;

        this.RaiseOnStart(new NodeStartEventArgs
        {
            NodeId = this.NodeId,
            NodeInstanceId = Guid.NewGuid(),
            Timestamp = startTime,
        });

        var state = CreateExecutionState(workflowContext, nodeContext);

        // Pre-process {placeholder} syntax
        var processedExpr = ExecutionState.PreProcessExpression(transformDef.Expression);

        // Build script with TransformHelpers imported as static
        var scriptOptions = ScriptOptions.Default
            .AddReferences(typeof(ExecutionState).Assembly)
            .AddReferences(typeof(System.Text.Json.JsonElement).Assembly)
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text.Json",
                "ExecutionEngine.Core");

        var script = CSharpScript.Create<object>(
            processedExpr,
            scriptOptions,
            globalsType: typeof(ExecutionState));

        var diagnostics = script.Compile(cancellationToken);
        if (diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
        {
            var errors = string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Transform expression compilation failed:{Environment.NewLine}{errors}");
        }

        var scriptState = await script.RunAsync(state, cancellationToken);
        var result = scriptState.ReturnValue;

        var outputKey = transformDef.OutputKey;
        if (string.IsNullOrEmpty(outputKey)) outputKey = "result";
        nodeContext.OutputData[outputKey] = result!;
        nodeContext.OutputData["mode"] = transformDef.Mode;

        return new NodeInstance
        {
            NodeId = this.Definition!.NodeId,
            Status = NodeExecutionStatus.Completed,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ExecutionContext = nodeContext,
            WorkflowInstanceId = workflowContext.InstanceId,
        };
    }
}