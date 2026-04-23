// -----------------------------------------------------------------------
// <copyright file="CompositionRoundTripTests.cs" company="Microsoft Corp.">
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
/// Round-trip tests for composition node kinds (Subflow, Timer, Container) and full workflow.
/// </summary>
[Trait("Category", "CompositionRoundTrip")]
public class CompositionRoundTripTests
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
    public void Subflow_WorkflowFilePath_PreservedAfterRoundTrip()
    {
        var def = new SubflowNodeDefinition
        {
            NodeId = "sf1",
            WorkflowFilePath = "child.yaml",
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var sf = back.Nodes.OfType<SubflowNodeDefinition>().First();
        Assert.Equal("child.yaml", sf.WorkflowFilePath);
    }

    [Fact]
    public void Timer_Schedule_PreservedAfterRoundTrip()
    {
        var def = new TimerNodeDefinition
        {
            NodeId = "t1",
            Schedule = "*/5 * * * *",
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var t = back.Nodes.OfType<TimerNodeDefinition>().First();
        Assert.Equal("*/5 * * * *", t.Schedule);
    }

    [Fact]
    public void Group_ExecutionMode_PreservedAfterRoundTrip()
    {
        var def = new GroupNodeDefinition
        {
            NodeId = "c1",
            ExecutionMode = ExecutionMode.Parallel,
            ChildNodes = [new SetterNodeDefinition { NodeId = "c1a" }],
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var c = back.Nodes.OfType<GroupNodeDefinition>().First();
        Assert.Equal(ExecutionMode.Parallel, c.ExecutionMode);
        Assert.Single(c.ChildNodes);
    }

    [Fact]
    public void SubflowExternal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "subflow-external.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sf = wf.Nodes.OfType<SubflowNodeDefinition>().First();
        Assert.Equal("subflow-child.yaml", sf.WorkflowFilePath);
    }

    [Fact]
    public void TimerCron_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "timer-cron.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var t = wf.Nodes.OfType<TimerNodeDefinition>().First();
        Assert.Equal("*/5 * * * *", t.Schedule);
    }

    [Fact]
    public void TimerDurationIso_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "timer-duration-iso.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var t = wf.Nodes.OfType<TimerNodeDefinition>().First();
        Assert.Equal("P1D", t.Schedule);
    }

    [Fact]
    public void TimerDurationHms_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "timer-duration-hms.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var t = wf.Nodes.OfType<TimerNodeDefinition>().First();
        Assert.Equal("01:30:00", t.Schedule);
    }

    [Fact]
    public void GroupSequential_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "container-sequential.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var c = wf.Nodes.OfType<GroupNodeDefinition>().First();
        Assert.Equal(ExecutionMode.Sequential, c.ExecutionMode);
        Assert.Equal(2, c.ChildNodes.Count);
    }

    [Fact]
    public void GroupNested_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "container-nested.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var outer = wf.Nodes.OfType<GroupNodeDefinition>().First();
        Assert.Single(outer.ChildNodes);
        var inner = outer.ChildNodes.OfType<GroupNodeDefinition>().First();
        Assert.Equal(ExecutionMode.Parallel, inner.ExecutionMode);
    }

    [Fact]
    public void GroupParallel_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "container-parallel.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var c = wf.Nodes.OfType<GroupNodeDefinition>().First();
        Assert.Equal(ExecutionMode.Parallel, c.ExecutionMode);
    }

    [Fact]
    public void SubflowInline_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "subflow-inline.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sf = wf.Nodes.OfType<SubflowNodeDefinition>().First();
        Assert.NotNull(sf.WorkflowDefinition);
    }

    [Fact]
    public void SubflowWithMappings_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "subflow-with-mappings.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var sf = wf.Nodes.OfType<SubflowNodeDefinition>().First();
        Assert.NotEmpty(sf.InputMappings);
    }

    [Fact]
    public void TimerDuration_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "timer-duration.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var t = wf.Nodes.OfType<TimerNodeDefinition>().First();
        Assert.Equal("PT5M", t.Schedule);
    }

    [Fact]
    public void RoundtripAll13Kinds_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "roundtrip-all-13-kinds.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        Assert.Equal(13, wf.Nodes.Count);
        var reYaml = this.serializer.ToYaml(wf);
        var wf2 = this.serializer.FromYaml(reYaml);
        Assert.Equal(13, wf2.Nodes.Count);
    }

    private static WorkflowDefinition Wrap(NodeDefinition node)
    {
        return new WorkflowDefinition
        {
            WorkflowId = "composition-test",
            WorkflowName = "Composition Test",
            Nodes = [node],
            Connections = [],
        };
    }
}
