// -----------------------------------------------------------------------
// <copyright file="EdgeActivationDeriverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.Collections.Generic;
using ExecutionEngine.Enums;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests for EdgeActivationDeriver synthetic edge derivation.
/// </summary>
public class EdgeActivationDeriverTests
{
    private readonly SequenceAllocator seq = new();
    private readonly EdgeActivationDeriver deriver;

    public EdgeActivationDeriverTests()
    {
        this.deriver = new EdgeActivationDeriver(this.seq);
    }

    [Fact]
    public void NodeCompleted_produces_EdgeActivated_for_Complete_edges()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "n1", TargetNodeId = "n3", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeCompleted,
            NodeId = "n1",
            TimestampUtc = DateTime.UtcNow,
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(RunEventKind.EdgeActivated, r.Kind);
            Assert.True(r.Synthetic);
            Assert.Equal("n1", r.Payload!["sourceNodeId"]);
        });
    }

    [Fact]
    public void Disabled_edges_are_filtered_out()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = false },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeCompleted,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Empty(results);
    }

    [Fact]
    public void NodeProgress_produces_zero_edges()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Progress, IsEnabled = true },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeProgress,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Empty(results);
    }

    [Fact]
    public void NodeFailed_produces_edges_for_Fail_trigger()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "err-handler", TriggerMessageType = MessageType.Fail, IsEnabled = true },
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeFailed,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Single(results);
        Assert.Equal("err-handler", results[0].Payload!["targetNodeId"]);
    }

    [Fact]
    public void Ordering_stable_by_Priority_desc_then_TargetNodeId_asc()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "z-node", TriggerMessageType = MessageType.Complete, IsEnabled = true, Priority = 1 },
            new() { SourceNodeId = "n1", TargetNodeId = "a-node", TriggerMessageType = MessageType.Complete, IsEnabled = true, Priority = 5 },
            new() { SourceNodeId = "n1", TargetNodeId = "m-node", TriggerMessageType = MessageType.Complete, IsEnabled = true, Priority = 5 },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeCompleted,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Equal(3, results.Count);
        Assert.Equal("a-node", results[0].Payload!["targetNodeId"]);
        Assert.Equal("m-node", results[1].Payload!["targetNodeId"]);
        Assert.Equal("z-node", results[2].Payload!["targetNodeId"]);
    }

    [Fact]
    public void Edges_with_condition_are_still_emitted()
    {
        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = true, Condition = "x > 5" },
        };

        var nodeEvent = new RunEvent
        {
            RunId = Guid.NewGuid(),
            Kind = RunEventKind.NodeCompleted,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Single(results);
        Assert.Equal("x > 5", results[0].Payload!["condition"]);
    }

    [Fact]
    public void Synthetic_events_have_unique_seq_continuing_run_sequence()
    {
        // Prime the sequence with a native event
        var runId = Guid.NewGuid();
        this.seq.Next(runId); // seq=1

        var connections = new List<NodeConnection>
        {
            new() { SourceNodeId = "n1", TargetNodeId = "n2", TriggerMessageType = MessageType.Complete, IsEnabled = true },
            new() { SourceNodeId = "n1", TargetNodeId = "n3", TriggerMessageType = MessageType.Complete, IsEnabled = true },
        };

        var nodeEvent = new RunEvent
        {
            RunId = runId,
            Kind = RunEventKind.NodeCompleted,
            NodeId = "n1",
        };

        var results = this.deriver.Derive(nodeEvent, connections);
        Assert.Equal(2, results[0].Seq);
        Assert.Equal(3, results[1].Seq);
    }
}
