// -----------------------------------------------------------------------
// <copyright file="NodeTypeCatalog.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Catalog;

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Describes a node type's metadata for UI consumption.
/// </summary>
public record NodeTypeInfo
{
    public string RuntimeType { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool HasInput { get; init; }
    public bool HasOutput { get; init; }
    public bool HasError { get; init; }
    public string[] OutputPorts { get; init; } = [];
    public bool DynamicPorts { get; init; }
    public Dictionary<string, string>? PortLabels { get; init; }
    public string[] Events { get; init; } = [];
    public NodePropertyInfo[] Properties { get; init; } = [];
}

/// <summary>
/// Describes a configurable property on a node type.
/// </summary>
public record NodePropertyInfo
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public string? Default { get; init; }
    public string? Placeholder { get; init; }
    public string[]? Options { get; init; }
}

/// <summary>
/// Builds a catalog of all available node types by reflecting over
/// <see cref="NodeDefinition"/> subclasses in the ExecutionEngine assembly.
/// </summary>
public static class NodeTypeCatalog
{
    private static readonly List<NodeTypeInfo> CachedCatalog = [];
    private static bool built;

    /// <summary>
    /// Returns the complete node type catalog, building it on first call.
    /// </summary>
    public static IReadOnlyList<NodeTypeInfo> GetAll()
    {
        if (!built)
        {
            CachedCatalog.AddRange(Build());
            built = true;
        }

        return CachedCatalog;
    }

    private static List<NodeTypeInfo> Build()
    {
        var result = new List<NodeTypeInfo>();
        var assembly = typeof(NodeDefinition).Assembly;

        var definitionTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(NodeDefinition)))
            .OrderBy(t => t.Name);

        foreach (var defType in definitionTypes)
        {
            var instance = (NodeDefinition?)Activator.CreateInstance(defType);
            if (instance == null)
            {
                continue;
            }

            result.Add(BuildTypeInfo(instance.RuntimeType, defType, instance));
        }

        return result;
    }

    private static NodeTypeInfo BuildTypeInfo(RuntimeType runtimeType, Type defType, NodeDefinition instance)
    {
        var (hasIn, hasOut, hasError) = GetPortInfo(runtimeType);
        var (outputPorts, dynamicPorts) = GetOutputPorts(runtimeType, instance);

        return new NodeTypeInfo
        {
            RuntimeType = runtimeType.ToString(),
            Label = GetLabel(runtimeType),
            Category = GetCategory(runtimeType),
            Description = GetDescription(runtimeType),
            HasInput = hasIn,
            HasOutput = hasOut,
            HasError = hasError,
            OutputPorts = outputPorts,
            DynamicPorts = dynamicPorts,
            PortLabels = GetPortLabels(runtimeType),
            Events = GetEvents(runtimeType),
            Properties = GetProperties(defType),
        };
    }

    private static string GetLabel(RuntimeType rt) => rt switch
    {
        RuntimeType.Start => "Start",
        RuntimeType.Trigger => "Webhook Trigger",
        RuntimeType.Timer => "Timer",
        RuntimeType.Action => "Action",
        RuntimeType.HttpRequest => "HTTP Request",
        RuntimeType.Transform => "Transform",
        RuntimeType.Delay => "Delay",
        RuntimeType.Log => "Log",
        RuntimeType.Noop => "Noop",
        RuntimeType.CSharp => "C# Assembly",
        RuntimeType.CSharpScript => "C# Script",
        RuntimeType.CSharpTask => "C# Task",
        RuntimeType.PowerShell => "PowerShell Script",
        RuntimeType.PowerShellTask => "PowerShell Task",
        RuntimeType.IfElse => "If / Else",
        RuntimeType.Switch => "Switch",
        RuntimeType.Parallel => "Parallel",
        RuntimeType.ForEach => "ForEach",
        RuntimeType.While => "While",
        RuntimeType.End => "End",
        RuntimeType.AI => "AI",
        RuntimeType.Container => "Container",
        RuntimeType.Subflow => "Subflow",
        _ => rt.ToString(),
    };

    private static string GetCategory(RuntimeType rt) => rt switch
    {
        RuntimeType.Start or RuntimeType.End => "Workflow",
        RuntimeType.Trigger or RuntimeType.Timer => "Trigger",
        RuntimeType.Action or RuntimeType.HttpRequest or RuntimeType.Transform
            or RuntimeType.Delay or RuntimeType.Log or RuntimeType.Noop => "Actions",
        RuntimeType.CSharp or RuntimeType.CSharpScript or RuntimeType.CSharpTask
            or RuntimeType.PowerShell or RuntimeType.PowerShellTask => "Code",
        RuntimeType.IfElse or RuntimeType.Switch or RuntimeType.Parallel
            or RuntimeType.ForEach or RuntimeType.While => "Flow Control",
        RuntimeType.AI => "AI",
        RuntimeType.Container or RuntimeType.Subflow => "Containers",
        _ => "Other",
    };

    private static string GetDescription(RuntimeType rt) => rt switch
    {
        RuntimeType.Start => "Workflow entry point — manual, scheduled, or event-triggered.",
        RuntimeType.Trigger => "HTTP webhook entry point.",
        RuntimeType.Timer => "Trigger on a cron schedule.",
        RuntimeType.Action => "Execute a task.",
        RuntimeType.HttpRequest => "Call an external API.",
        RuntimeType.Transform => "Map or transform data.",
        RuntimeType.Delay => "Pause for a duration.",
        RuntimeType.Log => "Output a log message.",
        RuntimeType.Noop => "No-op node that performs no operation.",
        RuntimeType.CSharp => "Load and execute a compiled C# INode from an assembly.",
        RuntimeType.CSharpScript => "Execute a C# script using Roslyn.",
        RuntimeType.CSharpTask => "Execute an inline C# script or compiled executor.",
        RuntimeType.PowerShell => "Execute a PowerShell .ps1 script.",
        RuntimeType.PowerShellTask => "Execute an inline or file-based PowerShell script.",
        RuntimeType.IfElse => "Conditional branching based on a boolean expression.",
        RuntimeType.Switch => "Multi-way branching based on expression value.",
        RuntimeType.Parallel => "Fan-out parallel branches.",
        RuntimeType.ForEach => "Iterate over a collection, emitting one message per item.",
        RuntimeType.While => "Repeat while a condition is true.",
        RuntimeType.End => "Workflow terminator.",
        RuntimeType.AI => "AI skill or subagent.",
        RuntimeType.Container => "Group nodes into a logical unit.",
        RuntimeType.Subflow => "Execute another workflow as a child process.",
        _ => rt.ToString(),
    };

    private static (bool hasIn, bool hasOut, bool hasError) GetPortInfo(RuntimeType rt) => rt switch
    {
        RuntimeType.Start or RuntimeType.Trigger or RuntimeType.Timer => (false, true, false),
        RuntimeType.End => (true, false, false),
        RuntimeType.Delay or RuntimeType.Log or RuntimeType.Noop => (true, true, false),
        RuntimeType.IfElse or RuntimeType.Switch => (true, true, true),
        RuntimeType.Parallel => (true, true, true),
        RuntimeType.ForEach or RuntimeType.While => (true, true, true),
        _ => (true, true, true),
    };

    private static (string[] ports, bool dynamic) GetOutputPorts(RuntimeType rt, NodeDefinition instance)
    {
        return rt switch
        {
            RuntimeType.IfElse => (["TrueBranch", "FalseBranch"], false),
            RuntimeType.Switch => (["Default"], true),
            RuntimeType.ForEach => (["LoopBody"], false),
            RuntimeType.While => (["LoopBody"], false),
            RuntimeType.Parallel when instance is ParallelNodeDefinition pd
                => ([.. pd.Branches], true),
            _ => ([], false),
        };
    }

    private static Dictionary<string, string>? GetPortLabels(RuntimeType rt) => rt switch
    {
        RuntimeType.ForEach or RuntimeType.While => new Dictionary<string, string>
        {
            ["in"] = "input",
            ["out"] = "output (onComplete)",
            ["LoopBody"] = "iterate (onNext)",
        },
        _ => null,
    };

    private static string[] GetEvents(RuntimeType rt) => rt switch
    {
        RuntimeType.ForEach or RuntimeType.While => ["OnStart", "OnProgress", "OnNext"],
        RuntimeType.Container or RuntimeType.Subflow => ["OnStart", "OnProgress"],
        _ => [],
    };

    private static NodePropertyInfo[] GetProperties(Type defType)
    {
        var baseProps = typeof(NodeDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        return defType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !baseProps.Contains(p.Name) && p.CanRead && p.CanWrite)
            .Select(p =>
            {
                var instance = Activator.CreateInstance(defType);
                var defaultValue = instance != null ? p.GetValue(instance) : null;
                var required = p.GetCustomAttribute<RequiredAttribute>() != null;

                return new NodePropertyInfo
                {
                    Name = ToCamelCase(p.Name),
                    Label = SplitPascalCase(p.Name),
                    Type = MapPropertyType(p.PropertyType),
                    Required = required,
                    Default = defaultValue?.ToString(),
                    Options = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null,
                };
            })
            .ToArray();
    }

    private static string MapPropertyType(Type type)
    {
        if (type.IsEnum) return "enum";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float)) return "number";
        if (type == typeof(TimeSpan)) return "duration";
        if (type == typeof(List<string>) || type == typeof(string[])) return "string[]";
        if (type == typeof(Dictionary<string, string>)) return "map";
        return "string";
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string SplitPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(name[i]) : name[i]);
        }

        return sb.ToString();
    }
}
