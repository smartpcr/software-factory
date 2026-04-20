// -----------------------------------------------------------------------
// <copyright file="NodeTypesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using ExecutionEngine.Enums;

/// <summary>
/// Minimal-API endpoints that expose the ExecutionEngine node type catalog
/// so the UI can build its palette and property grid dynamically.
/// </summary>
public static class NodeTypesEndpoints
{
    public static WebApplication MapNodeTypesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/node-types").WithTags("NodeTypes");

        group.MapGet("/", () =>
        {
            var types = BuildNodeTypeCatalog();
            return Results.Ok(types);
        })
        .WithName("ListNodeTypes")
        .AllowAnonymous();

        return app;
    }

    private static List<NodeTypeDescriptor> BuildNodeTypeCatalog()
    {
        return
        [
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.Noop),
                Label = "Noop",
                Category = "Utility",
                Description = "No-op node that performs no operation.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = false },
                Properties = [],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.CSharpScript),
                Label = "C# Script",
                Category = "Code",
                Description = "Execute a C# script using Roslyn.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Properties =
                [
                    new PropertyDescriptor { Name = "scriptPath", Label = "Script Path", Type = "string", Placeholder = "path/to/script.csx" },
                    new PropertyDescriptor { Name = "scriptContent", Label = "Script Content", Type = "code", Placeholder = "// C# code here" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.CSharpTask),
                Label = "C# Task",
                Category = "Code",
                Description = "Execute an inline C# script or compiled executor.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Properties =
                [
                    new PropertyDescriptor { Name = "scriptContent", Label = "Script Content", Type = "code", Placeholder = "// C# code here" },
                    new PropertyDescriptor { Name = "executorTypeName", Label = "Executor Type", Type = "string", Placeholder = "Namespace.ClassName" },
                    new PropertyDescriptor { Name = "executorAssemblyPath", Label = "Assembly Path", Type = "string", Placeholder = "path/to/assembly.dll" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.CSharp),
                Label = "C# Assembly",
                Category = "Code",
                Description = "Load and execute a compiled C# INode from an assembly.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Properties =
                [
                    new PropertyDescriptor { Name = "assemblyPath", Label = "Assembly Path", Type = "string", Required = true, Placeholder = "path/to/assembly.dll" },
                    new PropertyDescriptor { Name = "typeName", Label = "Type Name", Type = "string", Required = true, Placeholder = "Namespace.ClassName" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.PowerShell),
                Label = "PowerShell Script",
                Category = "Code",
                Description = "Execute a PowerShell .ps1 script.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Properties =
                [
                    new PropertyDescriptor { Name = "scriptPath", Label = "Script Path", Type = "string", Required = true, Placeholder = "path/to/script.ps1" },
                    new PropertyDescriptor { Name = "requiredModules", Label = "Required Modules", Type = "string[]", Placeholder = "Module1, Module2" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.PowerShellTask),
                Label = "PowerShell Task",
                Category = "Code",
                Description = "Execute an inline or file-based PowerShell script.",
                Color = "var(--n-action)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Properties =
                [
                    new PropertyDescriptor { Name = "scriptPath", Label = "Script Path", Type = "string", Placeholder = "path/to/script.ps1" },
                    new PropertyDescriptor { Name = "scriptContent", Label = "Script Content", Type = "code", Placeholder = "# PowerShell code here" },
                    new PropertyDescriptor { Name = "requiredModules", Label = "Required Modules", Type = "string[]", Placeholder = "Module1, Module2" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.IfElse),
                Label = "If / Else",
                Category = "Flow Control",
                Description = "Conditional branching based on a C# boolean expression.",
                Color = "var(--n-if)",
                Ports = new PortDescriptor { In = true, Out = false, Error = true },
                OutputPorts = ["TrueBranch", "FalseBranch"],
                Properties =
                [
                    new PropertyDescriptor { Name = "condition", Label = "Condition", Type = "expression", Required = true, Placeholder = "input.value > 10" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.ForEach),
                Label = "ForEach",
                Category = "Flow Control",
                Description = "Iterate over a collection, emitting one message per item.",
                Color = "var(--n-loop)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                OutputPorts = ["LoopBody"],
                PortLabels = new Dictionary<string, string>
                {
                    ["in"] = "input",
                    ["out"] = "output (onComplete)",
                    ["LoopBody"] = "iterate (onNext)",
                },
                Events = ["OnStart", "OnProgress", "OnNext"],
                Properties =
                [
                    new PropertyDescriptor { Name = "collectionExpression", Label = "Collection Expression", Type = "expression", Required = true, Placeholder = "input.items" },
                    new PropertyDescriptor { Name = "itemVariableName", Label = "Item Variable Name", Type = "string", Default = "item" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.While),
                Label = "While",
                Category = "Flow Control",
                Description = "Repeat while a condition is true.",
                Color = "var(--n-loop)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                OutputPorts = ["LoopBody"],
                PortLabels = new Dictionary<string, string>
                {
                    ["in"] = "input",
                    ["out"] = "output (onComplete)",
                    ["LoopBody"] = "iterate (onNext)",
                },
                Events = ["OnStart", "OnProgress", "OnNext"],
                Properties =
                [
                    new PropertyDescriptor { Name = "conditionExpression", Label = "Condition Expression", Type = "expression", Required = true, Placeholder = "counter < 10" },
                    new PropertyDescriptor { Name = "maxIterations", Label = "Max Iterations", Type = "number", Default = "1000" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.Switch),
                Label = "Switch",
                Category = "Flow Control",
                Description = "Multi-way branching based on expression value.",
                Color = "var(--n-switch)",
                Ports = new PortDescriptor { In = true, Out = false, Error = true },
                OutputPorts = ["Default"],
                DynamicPorts = true,
                Properties =
                [
                    new PropertyDescriptor { Name = "expression", Label = "Expression", Type = "expression", Required = true, Placeholder = "input.type" },
                    new PropertyDescriptor { Name = "cases", Label = "Cases", Type = "map", Placeholder = "key → value" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.Subflow),
                Label = "Subflow",
                Category = "Containers",
                Description = "Execute another workflow as a child process.",
                Color = "var(--n-subflow)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Events = ["OnStart", "OnProgress"],
                Properties =
                [
                    new PropertyDescriptor { Name = "workflowFilePath", Label = "Workflow File", Type = "string", Placeholder = "path/to/workflow.yaml" },
                    new PropertyDescriptor { Name = "inputMappings", Label = "Input Mappings", Type = "map", Placeholder = "local → child" },
                    new PropertyDescriptor { Name = "outputMappings", Label = "Output Mappings", Type = "map", Placeholder = "child → local" },
                    new PropertyDescriptor { Name = "timeout", Label = "Timeout", Type = "duration", Default = "00:05:00" },
                    new PropertyDescriptor { Name = "skipValidation", Label = "Skip Validation", Type = "boolean", Default = "false" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.Timer),
                Label = "Timer",
                Category = "Trigger",
                Description = "Trigger on a cron schedule.",
                Color = "var(--n-start)",
                Ports = new PortDescriptor { In = false, Out = true, Error = false },
                Properties =
                [
                    new PropertyDescriptor { Name = "schedule", Label = "Cron Schedule", Type = "string", Required = true, Placeholder = "0 */5 * * *" },
                ],
            },
            new NodeTypeDescriptor
            {
                RuntimeType = nameof(RuntimeType.Container),
                Label = "Container",
                Category = "Containers",
                Description = "Group nodes into a logical unit with sequential or parallel execution.",
                Color = "var(--n-container)",
                Ports = new PortDescriptor { In = true, Out = true, Error = true },
                Events = ["OnStart", "OnProgress"],
                Properties =
                [
                    new PropertyDescriptor
                    {
                        Name = "executionMode", Label = "Execution Mode", Type = "enum",
                        Options = [nameof(ExecutionMode.Sequential), nameof(ExecutionMode.Parallel)],
                        Default = nameof(ExecutionMode.Sequential),
                    },
                ],
            },
        ];
    }
}

// ---- DTOs ----

/// <summary>
/// Describes a node type for the UI palette and property grid.
/// </summary>
public record NodeTypeDescriptor
{
    public string RuntimeType { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public PortDescriptor Ports { get; init; } = new();
    public string[] OutputPorts { get; init; } = [];
    public bool DynamicPorts { get; init; }
    public Dictionary<string, string>? PortLabels { get; init; }
    public string[] Events { get; init; } = [];
    public PropertyDescriptor[] Properties { get; init; } = [];
}

/// <summary>
/// Describes the standard input/output/error ports.
/// </summary>
public record PortDescriptor
{
    public bool In { get; init; }
    public bool Out { get; init; }
    public bool Error { get; init; }
}

/// <summary>
/// Describes a configurable property on a node type.
/// </summary>
public record PropertyDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public string? Default { get; init; }
    public string? Placeholder { get; init; }
    public string[]? Options { get; init; }
}
