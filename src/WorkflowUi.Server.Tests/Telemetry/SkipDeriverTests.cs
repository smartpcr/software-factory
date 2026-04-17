// -----------------------------------------------------------------------
// <copyright file="SkipDeriverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests for SkipDeriver synthetic NodeSkipped derivation.
/// </summary>
public class SkipDeriverTests
{
    private readonly SequenceAllocator seq = new();
    private readonly SkipDeriver deriver;

    public SkipDeriverTests()
    {
        this.deriver = new SkipDeriver(this.seq);
    }

    [Fact]
    public void IfElse_true_branch_emits_one_skip_for_false_branch()
    {
        var nodes = new List<NodeDefinition>
        {
            new IfElseNodeDefinition { NodeId = "if1", NodeName = "If", Condition = "true" },
            new NoopNodeDefinition { NodeId = "true-target", NodeName = "True Target" },
            new NoopNodeDefinition { NodeId = "false-target", NodeName = "False Target" },
        };

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "if1", TargetNodeId = "true-target", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "if1", TargetNodeId = "false-target", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var edgeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = "if1",
                ["targetNodeId"] = "true-target",
                ["triggerMessageType"] = "Complete",
            },
        };

        var skips = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Single(skips);
        Assert.Equal("false-target", skips[0].NodeId);
        Assert.Equal(RunEventKind.NodeSkipped, skips[0].Kind);
        Assert.True(skips[0].Synthetic);
        Assert.Equal("BranchNotTaken", skips[0].Payload!["reason"]);
    }

    [Fact]
    public void Switch_with_3_cases_and_1_match_emits_2_skips()
    {
        var nodes = new List<NodeDefinition>
        {
            new SwitchNodeDefinition { NodeId = "sw1", NodeName = "Switch", Expression = "x" },
            new NoopNodeDefinition { NodeId = "case-a", NodeName = "Case A" },
            new NoopNodeDefinition { NodeId = "case-b", NodeName = "Case B" },
            new NoopNodeDefinition { NodeId = "case-c", NodeName = "Case C" },
        };

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "sw1", TargetNodeId = "case-a", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "sw1", TargetNodeId = "case-b", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "sw1", TargetNodeId = "case-c", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var edgeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = "sw1",
                ["targetNodeId"] = "case-a",
                ["triggerMessageType"] = "Complete",
            },
        };

        var skips = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Equal(2, skips.Count);
        var skippedIds = skips.Select(s => s.NodeId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "case-b", "case-c" }, skippedIds);
        Assert.All(skips, s =>
        {
            Assert.Equal("SwitchCaseNoMatch", s.Payload!["reason"]);
            Assert.True(s.Synthetic);
        });
    }

    [Fact]
    public void Repeat_IfElse_emits_at_most_one_skip_per_nodeId()
    {
        var runId = Guid.NewGuid();
        var nodes = new List<NodeDefinition>
        {
            new IfElseNodeDefinition { NodeId = "if1", Condition = "true" },
            new NoopNodeDefinition { NodeId = "true-target" },
            new NoopNodeDefinition { NodeId = "false-target" },
        };

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "if1", TargetNodeId = "true-target", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "if1", TargetNodeId = "false-target", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var edgeEvent = new RunEvent
        {
            RunId = runId,
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = "if1",
                ["targetNodeId"] = "true-target",
                ["triggerMessageType"] = "Complete",
            },
        };

        // First derivation produces 1 skip
        var skips1 = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Single(skips1);

        // Second derivation (retry) produces 0 skips
        var skips2 = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Empty(skips2);
    }

    [Fact]
    public void Noop_source_does_not_trigger_skip_derivation()
    {
        var nodes = new List<NodeDefinition>
        {
            new NoopNodeDefinition { NodeId = "noop1" },
            new NoopNodeDefinition { NodeId = "n2" },
            new NoopNodeDefinition { NodeId = "n3" },
        };

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "noop1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "noop1", TargetNodeId = "n3", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var edgeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = "noop1",
                ["targetNodeId"] = "n2",
                ["triggerMessageType"] = "Complete",
            },
        };

        var skips = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Empty(skips);
    }

    [Fact]
    public void Zero_non_taken_siblings_emits_nothing()
    {
        var nodes = new List<NodeDefinition>
        {
            new IfElseNodeDefinition { NodeId = "if1", Condition = "true" },
            new NoopNodeDefinition { NodeId = "only-target" },
        };

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "if1", TargetNodeId = "only-target", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var edgeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.EdgeActivated,
            Synthetic = true,
            Payload = new Dictionary<string, object>
            {
                ["sourceNodeId"] = "if1",
                ["targetNodeId"] = "only-target",
                ["triggerMessageType"] = "Complete",
            },
        };

        var skips = this.deriver.Derive(edgeEvent, connections, nodes);
        Assert.Empty(skips);
    }
}
