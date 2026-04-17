// -----------------------------------------------------------------------
// <copyright file="EnumParityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExecutionEngine.Enums;
using WorkflowUi.ContractTests.Reflection;
using Xunit;

/// <summary>
/// Reflection-based parity tests for the 7 public engine enums.
/// Any drift between the engine enum surface and the committed fixture
/// will fail these tests red.
/// </summary>
[Trait("Category", "EnumParity")]
public class EnumParityTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    [Fact]
    public void RuntimeType_Has13Members_ExactNames()
    {
        var expected = new[]
        {
            "Noop", "CSharp", "CSharpScript", "CSharpTask",
            "PowerShell", "PowerShellTask",
            "IfElse", "ForEach", "While", "Switch",
            "Subflow", "Timer", "Container",
        };
        var actual = Enum.GetNames<RuntimeType>();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RuntimeType_Ordinals_MatchSourceDeclarationOrder()
    {
        var ordinals = EngineReflection.OrdinalsOf(typeof(RuntimeType));
        Assert.Equal(13, ordinals.Count);
        Assert.Equal(0, ordinals[0]); // Noop
        Assert.Equal(12, ordinals[12]); // Container
    }

    [Fact]
    public void NodeExecutionStatus_Has6Members_ExactNames()
    {
        var expected = new[] { "Pending", "Running", "Completed", "Failed", "Cancelled", "Skipped" };
        var actual = Enum.GetNames<NodeExecutionStatus>();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WorkflowExecutionStatus_Has6Members_ExactNames_IncludingPaused()
    {
        var expected = new[] { "Pending", "Running", "Completed", "Failed", "Cancelled", "Paused" };
        var actual = Enum.GetNames<WorkflowExecutionStatus>();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MessageType_Has5Members_IncludingProgressAndNext()
    {
        var expected = new[] { "Complete", "Fail", "Progress", "Next", "Cancel" };
        var actual = Enum.GetNames<MessageType>();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NodePriority_Has3Members_NoCritical()
    {
        var names = Enum.GetNames<NodePriority>();
        Assert.Equal(3, names.Length);
        Assert.DoesNotContain("Critical", names);
    }

    [Fact]
    public void NodePriority_Ordinals_AreZeroOneTwo()
    {
        Assert.Equal(0, (int)NodePriority.Low);
        Assert.Equal(1, (int)NodePriority.Normal);
        Assert.Equal(2, (int)NodePriority.High);
    }

    [Fact]
    public void JoinType_Ordinals_AreZeroOne()
    {
        Assert.Equal(0, (int)JoinType.Any);
        Assert.Equal(1, (int)JoinType.All);
    }

    [Fact]
    public void ExecutionMode_HasSequentialAndParallel()
    {
        var names = Enum.GetNames<ExecutionMode>();
        Assert.Equal(2, names.Length);
        Assert.Contains("Sequential", names);
        Assert.Contains("Parallel", names);
    }

    [Fact]
    public void FixtureFile_ByteStable_Across2Runs()
    {
        var run1 = EmitEnumFixtureJson();
        var run2 = EmitEnumFixtureJson();
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
        var goldenPath = Path.Combine(dir!, "src", "WorkflowUI", "src", "__fixtures__", "engine-enums.json");
        Assert.True(File.Exists(goldenPath), $"Committed fixture not found at {goldenPath}");

        var committed = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        var emitted = EmitEnumFixtureJson();

        if (Environment.GetEnvironmentVariable("WORKFLOWUI_WRITE_FIXTURES") == "1")
        {
            File.WriteAllText(goldenPath, emitted);
            return;
        }

        Assert.Equal(committed, emitted);
    }

    /// <summary>
    /// Generates the engine-enums.json fixture content as a stable string.
    /// </summary>
    public static string EmitEnumFixtureJson()
    {
        var fixture = new Dictionary<string, object>();
        foreach (var enumType in EngineReflection.AllPublicEnums())
        {
            fixture[enumType.Name] = new
            {
                members = EngineReflection.MembersOf(enumType),
                ordinals = EngineReflection.OrdinalsOf(enumType),
            };
        }

        var json = JsonSerializer.Serialize(fixture, JsonOpts);
        return json.Replace("\r\n", "\n");
    }
}
