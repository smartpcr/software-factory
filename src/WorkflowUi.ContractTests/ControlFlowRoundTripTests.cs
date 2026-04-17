// -----------------------------------------------------------------------
// <copyright file="ControlFlowRoundTripTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using Xunit;

/// <summary>
/// Round-trip tests for control-flow node kinds.
/// </summary>
[Trait("Category", "ControlFlowRoundTrip")]
public class ControlFlowRoundTripTests
{
    private readonly WorkflowSerializer serializer = new();

    private static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Workflow.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? throw new InvalidOperationException("Cannot find repo root.");
        }
    }

    private static string FixturesDir => Path.Combine(RepoRoot, "src", "WorkflowUi.ContractTests", "Fixtures");

    [Fact]
    public void IfElse_Condition_PreservedAfterRoundTrip()
    {
        var def = new IfElseNodeDefinition
        {
            NodeId = "ie1",
            Condition = "item.count > 0 && item.status == 'ready'",
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var ie = back.Nodes.OfType<IfElseNodeDefinition>().First();
        Assert.Equal("item.count > 0 && item.status == 'ready'", ie.Condition);
    }

    [Fact]
    public void Switch_Expression_And_Cases_PreservedAfterRoundTrip()
    {
        var def = new SwitchNodeDefinition
        {
            NodeId = "sw1",
            Expression = "status",
            Cases = new Dictionary<string, string>
            {
                ["ready"] = "Continue processing",
                ["waiting"] = "Hold for review",
                ["error"] = "Fail fast",
            },
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var sw = back.Nodes.OfType<SwitchNodeDefinition>().First();
        Assert.Equal("status", sw.Expression);
        Assert.Equal(3, sw.Cases.Count);
    }

    [Fact]
    public void ForEach_CollectionExpression_And_ItemVar_PreservedAfterRoundTrip()
    {
        var def = new ForEachNodeDefinition
        {
            NodeId = "fe1",
            CollectionExpression = "data.items",
            ItemVariableName = "record",
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var fe = back.Nodes.OfType<ForEachNodeDefinition>().First();
        Assert.Equal("data.items", fe.CollectionExpression);
        Assert.Equal("record", fe.ItemVariableName);
    }

    [Fact]
    public void While_ConditionExpression_And_MaxIterations_PreservedAfterRoundTrip()
    {
        var def = new WhileNodeDefinition
        {
            NodeId = "wh1",
            ConditionExpression = "count < 100",
            MaxIterations = 5000,
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var wh = back.Nodes.OfType<WhileNodeDefinition>().First();
        Assert.Equal("count < 100", wh.ConditionExpression);
        Assert.Equal(5000, wh.MaxIterations);
    }

    [Fact]
    public void IfElseMinimal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "ifelse-minimal.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var ie = wf.Nodes.OfType<IfElseNodeDefinition>().First();
        Assert.Equal("x > 0", ie.Condition);
        Assert.Equal(2, wf.Connections.Count);
    }

    [Fact]
    public void SwitchMinimal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "switch-3-cases.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sw = wf.Nodes.OfType<SwitchNodeDefinition>().First();
        Assert.Equal("status", sw.Expression);
        Assert.Equal(3, sw.Cases.Count);
    }

    [Fact]
    public void ForEachMinimal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "foreach-minimal.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var fe = wf.Nodes.OfType<ForEachNodeDefinition>().First();
        Assert.Equal("data.items", fe.CollectionExpression);
        Assert.Equal("item", fe.ItemVariableName);
        Assert.Contains(wf.Connections, c => c.TriggerMessageType == MessageType.Next);
        Assert.Contains(wf.Connections, c => c.TriggerMessageType == MessageType.Complete);
    }

    [Fact]
    public void WhileMinimal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "while-minimal.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var wh = wf.Nodes.OfType<WhileNodeDefinition>().First();
        Assert.Equal("count < 100", wh.ConditionExpression);
        Assert.Equal(5000, wh.MaxIterations);
    }

    [Fact]
    public void SwitchDefaultCase_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "switch-default-case.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sw = wf.Nodes.OfType<SwitchNodeDefinition>().First();
        Assert.Equal("status", sw.Expression);
        Assert.Contains(wf.Connections, c => c.Condition == "*");
    }

    [Fact]
    public void SwitchEmptyCases_Fixture_Loads()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "switch-empty-cases.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sw = wf.Nodes.OfType<SwitchNodeDefinition>().First();
        Assert.Equal("status", sw.Expression);
    }

    [Fact]
    public void ForEachCustomVar_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "foreach-custom-var.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var fe = wf.Nodes.OfType<ForEachNodeDefinition>().First();
        Assert.Equal("data.records", fe.CollectionExpression);
        Assert.Equal("record", fe.ItemVariableName);
    }

    [Fact]
    public void WhileHighMaxIter_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "while-high-max-iter.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var wh = wf.Nodes.OfType<WhileNodeDefinition>().First();
        Assert.Equal("count < 500000", wh.ConditionExpression);
        Assert.Equal(999999, wh.MaxIterations);
    }

    [Fact]
    public void IfElseEmptyCondition_Fixture_LoadsWithEmptyCondition()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "ifelse-empty-condition.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var ie = wf.Nodes.OfType<IfElseNodeDefinition>().First();
        Assert.True(string.IsNullOrEmpty(ie.Condition));
    }

    [Fact]
    public void ForEachEmptyVar_Fixture_LoadsWithEmptyItemVar()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "foreach-empty-var.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var fe = wf.Nodes.OfType<ForEachNodeDefinition>().First();
        Assert.True(string.IsNullOrEmpty(fe.ItemVariableName));
        Assert.Equal("data.items", fe.CollectionExpression);
    }

    [Fact]
    public void ForEachEmptyCollection_Fixture_LoadsWithEmptyCollection()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "foreach-empty-collection.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var fe = wf.Nodes.OfType<ForEachNodeDefinition>().First();
        Assert.True(string.IsNullOrEmpty(fe.CollectionExpression));
        Assert.Equal("item", fe.ItemVariableName);
    }

    private WorkflowDefinition Wrap(NodeDefinition node)
    {
        return new WorkflowDefinition
        {
            WorkflowId = "control-flow-test",
            WorkflowName = "Control Flow Test",
            Nodes = [node],
            Connections = [],
        };
    }
}
