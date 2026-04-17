// -----------------------------------------------------------------------
// <copyright file="DtoParityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.ContractTests.Reflection;
using Xunit;

/// <summary>
/// Reflection-based parity tests for all NodeDefinition subclasses.
/// </summary>
[Trait("Category", "DtoParity")]
public class DtoParityTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private static PropertyInfo[] GetDeclaredProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.Name != "RuntimeType" || !p.GetGetMethod()!.IsAbstract)
            .ToArray();
    }

    private static string[] GetPropertyNames(Type type)
    {
        return GetDeclaredProperties(type).Select(p => p.Name).OrderBy(n => n).ToArray();
    }

    [Fact]
    public void NodeDefinition_Has11PublicProperties()
    {
        var expected = new[]
        {
            "CircuitBreakerPolicy", "CompensationNodeId", "Configuration",
            "Description", "FallbackNodeId", "JoinType",
            "MaxConcurrentExecutions", "NodeId", "NodeName",
            "Priority", "RetryPolicy",
        };
        var actual = typeof(NodeDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => !(p.Name == "RuntimeType" && p.GetGetMethod()!.IsAbstract))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoopNodeDefinition_HasZeroExtraProperties()
    {
        var props = GetPropertyNames(typeof(NoopNodeDefinition));
        // Only RuntimeType override, which is filtered out
        Assert.Empty(props.Where(n => n != "RuntimeType"));
    }

    [Fact]
    public void CSharpNodeDefinition_Has_AssemblyPath_And_TypeName()
    {
        var props = GetPropertyNames(typeof(CSharpNodeDefinition));
        Assert.Contains("AssemblyPath", props);
        Assert.Contains("TypeName", props);
    }

    [Fact]
    public void CSharpScriptNodeDefinition_Has_ScriptPath_And_ScriptContent_Both_NonNullable()
    {
        var props = GetDeclaredProperties(typeof(CSharpScriptNodeDefinition));
        var scriptPath = props.Single(p => p.Name == "ScriptPath");
        var scriptContent = props.Single(p => p.Name == "ScriptContent");
        Assert.False(NullabilityProbe.IsNullable(scriptPath));
        Assert.False(NullabilityProbe.IsNullable(scriptContent));
    }

    [Fact]
    public void CSharpTaskNodeDefinition_Has_3NullableProperties()
    {
        var props = GetDeclaredProperties(typeof(CSharpTaskNodeDefinition))
            .Where(p => p.Name != "RuntimeType")
            .ToArray();
        Assert.Equal(3, props.Length);
        Assert.All(props, p => Assert.True(NullabilityProbe.IsNullable(p)));
    }

    [Fact]
    public void PowerShellScriptNodeDefinition_ScriptPath_IsNonNullable()
    {
        var prop = typeof(PowerShellScriptNodeDefinition)
            .GetProperty("ScriptPath", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.False(NullabilityProbe.IsNullable(prop));
    }

    [Fact]
    public void PowerShellTaskNodeDefinition_ScriptPath_IsNullable()
    {
        var prop = typeof(PowerShellTaskNodeDefinition)
            .GetProperty("ScriptPath", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.True(NullabilityProbe.IsNullable(prop));
    }

    [Fact]
    public void IfElseNodeDefinition_OnlyCondition()
    {
        var props = GetPropertyNames(typeof(IfElseNodeDefinition));
        var nonRuntime = props.Where(n => n != "RuntimeType").ToArray();
        Assert.Single(nonRuntime);
        Assert.Equal("Condition", nonRuntime[0]);
        // Explicitly forbid TrueBranch/FalseBranch leakage
        Assert.DoesNotContain("TrueBranch", props);
        Assert.DoesNotContain("FalseBranch", props);
    }

    [Fact]
    public void SwitchNodeDefinition_HasExpressionAndCases()
    {
        var props = GetPropertyNames(typeof(SwitchNodeDefinition));
        Assert.Contains("Expression", props);
        Assert.Contains("Cases", props);
    }

    [Fact]
    public void ForEachNodeDefinition_HasCollectionExpressionAndItemVariableName()
    {
        var props = GetPropertyNames(typeof(ForEachNodeDefinition));
        Assert.Contains("CollectionExpression", props);
        Assert.Contains("ItemVariableName", props);
        // Verify default
        var instance = new ForEachNodeDefinition();
        Assert.Equal("item", instance.ItemVariableName);
    }

    [Fact]
    public void WhileNodeDefinition_MaxIterations_DefaultIs1000()
    {
        var instance = new WhileNodeDefinition();
        Assert.Equal(1000, instance.MaxIterations);
    }

    [Fact]
    public void SubflowNodeDefinition_HasExactly6Properties_AndTimeoutDefaultIs5Minutes()
    {
        var props = GetPropertyNames(typeof(SubflowNodeDefinition));
        var nonRuntime = props.Where(n => n != "RuntimeType").ToArray();
        Assert.Equal(6, nonRuntime.Length);
        var instance = new SubflowNodeDefinition();
        Assert.Equal(TimeSpan.FromMinutes(5), instance.Timeout);
    }

    [Fact]
    public void TimerNodeDefinition_OnlySchedule()
    {
        var props = GetPropertyNames(typeof(TimerNodeDefinition));
        var nonRuntime = props.Where(n => n != "RuntimeType").ToArray();
        Assert.Single(nonRuntime);
        Assert.Equal("Schedule", nonRuntime[0]);
    }

    [Fact]
    public void ContainerNodeDefinition_HasExactly3Properties_AndExecutionModeDefaultIsSequential()
    {
        var props = GetPropertyNames(typeof(ContainerNodeDefinition));
        var nonRuntime = props.Where(n => n != "RuntimeType").ToArray();
        Assert.Equal(3, nonRuntime.Length);
        var instance = new ContainerNodeDefinition();
        Assert.Equal(ExecutionMode.Sequential, instance.ExecutionMode);
    }

    [Fact]
    public void AllRuntimeTypes_HaveAMatchingDefinitionClass()
    {
        var assembly = typeof(NodeDefinition).Assembly;
        var definitionTypes = assembly.GetExportedTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(NodeDefinition)))
            .ToList();

        foreach (var runtimeType in Enum.GetValues<RuntimeType>())
        {
            var matchingDef = definitionTypes.FirstOrDefault(t =>
            {
                var instance = (NodeDefinition)Activator.CreateInstance(t)!;
                return instance.RuntimeType == runtimeType;
            });
            Assert.True(matchingDef != null, $"No NodeDefinition subclass found for RuntimeType.{runtimeType}");
        }
    }

    [Fact]
    public void FixtureFile_ByteStable_Across2Runs()
    {
        var run1 = EmitDtoFixtureJson();
        var run2 = EmitDtoFixtureJson();
        Assert.Equal(run1, run2);
    }

    [Fact]
    public void FixtureFile_MatchesCommittedGolden()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Workflow.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var goldenPath = Path.Combine(dir!, "src", "WorkflowUI", "src", "__fixtures__", "engine-dto-shape.json");
        Assert.True(File.Exists(goldenPath), $"Committed fixture not found at {goldenPath}");

        var committed = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        var emitted = EmitDtoFixtureJson();

        if (Environment.GetEnvironmentVariable("WORKFLOWUI_WRITE_FIXTURES") == "1")
        {
            File.WriteAllText(goldenPath, emitted);
            return;
        }

        Assert.Equal(committed, emitted);
    }

    /// <summary>
    /// Generates the engine-dto-shape.json fixture content.
    /// </summary>
    public static string EmitDtoFixtureJson()
    {
        var assembly = typeof(NodeDefinition).Assembly;
        var engineVersion = assembly.GetName().Version?.ToString() ?? "unknown";

        var shapesDict = new Dictionary<string, object>
        {
            ["$engineAssemblyVersion"] = engineVersion,
        };

        // Base NodeDefinition
        AddShape(shapesDict, typeof(NodeDefinition), null);

        // All concrete subclasses
        var concreteTypes = assembly.GetExportedTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(NodeDefinition)))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var type in concreteTypes)
        {
            AddShape(shapesDict, type, "NodeDefinition");
        }

        // Policies
        AddShape(shapesDict, typeof(ExecutionEngine.Policies.RetryPolicy), null);
        AddShape(shapesDict, typeof(ExecutionEngine.Policies.CircuitBreakerPolicy), null);

        // NodeConnection
        AddShape(shapesDict, typeof(NodeConnection), null);

        var json = JsonSerializer.Serialize(shapesDict, JsonOpts);
        return json.Replace("\r\n", "\n");
    }

    private static void AddShape(Dictionary<string, object> dict, Type type, string? baseOf)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => !(p.Name == "RuntimeType" && p.GetGetMethod()!.IsAbstract))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new
            {
                name = p.Name,
                type = p.PropertyType.Name,
                nullable = NullabilityProbe.IsNullable(p),
            })
            .ToList();

        var shape = new Dictionary<string, object>();
        if (baseOf != null)
        {
            shape["baseOf"] = baseOf;
        }

        shape["properties"] = props;
        dict[type.Name] = shape;
    }
}
