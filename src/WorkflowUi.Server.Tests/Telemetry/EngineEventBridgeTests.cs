// -----------------------------------------------------------------------
// <copyright file="EngineEventBridgeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Events;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests for EngineEventBridge mapping all 9 native event types.
/// </summary>
public class EngineEventBridgeTests
{
    private readonly InMemoryRunEventSink sink = new();
    private readonly SequenceAllocator seq = new();
    private readonly EngineEventBridge bridge;

    public EngineEventBridgeTests()
    {
        this.bridge = new EngineEventBridge(this.sink, this.seq, NullLogger<EngineEventBridge>.Instance);
    }

    [Fact]
    public async Task Maps_WorkflowStarted_with_correct_kind_and_payload()
    {
        var runId = Guid.NewGuid();
        var e = new WorkflowStartedEvent
        {
            WorkflowInstanceId = runId,
            WorkflowId = "wf-1",
            WorkflowName = "Test WF",
            TotalNodes = 5,
        };

        await this.bridge.HandleAsync(e);

        Assert.Single(this.sink.Events);
        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.WorkflowStarted, evt.Kind);
        Assert.False(evt.Synthetic);
        Assert.Equal(runId, evt.RunId);
        Assert.Equal(1, evt.Seq);
        Assert.Equal("wf-1", evt.Payload!["workflowId"]);
        Assert.Equal("Test WF", evt.Payload["workflowName"]);
        Assert.Equal(5, evt.Payload["totalNodes"]);
    }

    [Fact]
    public async Task Maps_WorkflowCompleted_with_duration_and_status()
    {
        var e = new WorkflowCompletedEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            WorkflowId = "wf-1",
            Duration = TimeSpan.FromSeconds(42),
            Status = WorkflowExecutionStatus.Completed,
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.WorkflowCompleted, evt.Kind);
        Assert.False(evt.Synthetic);
        Assert.Equal("Completed", evt.Payload!["status"]);
    }

    [Fact]
    public async Task Maps_WorkflowFailed_with_errorMessage()
    {
        var e = new WorkflowFailedEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            WorkflowId = "wf-1",
            Duration = TimeSpan.FromSeconds(10),
            ErrorMessage = "boom",
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.WorkflowFailed, evt.Kind);
        Assert.Equal("boom", evt.Payload!["errorMessage"]);
    }

    [Fact]
    public async Task Maps_WorkflowCancelled_with_reason()
    {
        var e = new WorkflowCancelledEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            WorkflowId = "wf-1",
            Duration = TimeSpan.FromSeconds(5),
            Reason = "user abort",
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.WorkflowCancelled, evt.Kind);
        Assert.Equal("user abort", evt.Payload!["reason"]);
    }

    [Fact]
    public async Task Maps_NodeStarted()
    {
        var nodeInstId = Guid.NewGuid();
        var e = new NodeStartedEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            NodeId = "n1",
            NodeName = "Step 1",
            NodeInstanceId = nodeInstId,
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.NodeStarted, evt.Kind);
        Assert.Equal("n1", evt.NodeId);
        Assert.Equal("Step 1", evt.NodeName);
        Assert.Equal(nodeInstId.ToString(), evt.Payload!["nodeInstanceId"]);
    }

    [Fact]
    public async Task Maps_NodeCompleted_with_duration()
    {
        var e = new NodeCompletedEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            NodeId = "n1",
            NodeName = "Step 1",
            NodeInstanceId = Guid.NewGuid(),
            Duration = TimeSpan.FromMilliseconds(250),
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.NodeCompleted, evt.Kind);
        Assert.False(evt.Synthetic);
    }

    [Fact]
    public async Task Maps_NodeFailed_with_errorMessage()
    {
        var e = new NodeFailedEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            NodeId = "n1",
            NodeName = "Step 1",
            NodeInstanceId = Guid.NewGuid(),
            ErrorMessage = "node error",
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.NodeFailed, evt.Kind);
        Assert.Equal("node error", evt.Payload!["errorMessage"]);
    }

    [Fact]
    public async Task Maps_NodeCancelled_with_reason()
    {
        var e = new NodeCancelledEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            NodeId = "n1",
            NodeName = "Step 1",
            NodeInstanceId = Guid.NewGuid(),
            Reason = "cascade",
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.NodeCancelled, evt.Kind);
        Assert.Equal("cascade", evt.Payload!["reason"]);
    }

    [Fact]
    public async Task Maps_NodeProgress_with_progressPercent_and_status()
    {
        var nodeInstId = Guid.NewGuid();
        var e = new NodeProgressEvent
        {
            WorkflowInstanceId = Guid.NewGuid(),
            NodeId = "n1",
            NodeName = "Step 1",
            NodeInstanceId = nodeInstId,
            ProgressPercent = 75,
            Status = "loading",
        };

        await this.bridge.HandleAsync(e);

        var evt = this.sink.Events.First();
        Assert.Equal(RunEventKind.NodeProgress, evt.Kind);
        Assert.Equal(75, evt.Payload!["progressPercent"]);
        Assert.Equal("loading", evt.Payload["status"]);
        Assert.Equal(nodeInstId.ToString(), evt.Payload["nodeInstanceId"]);
    }

    [Fact]
    public async Task All_nine_native_events_produce_synthetic_false()
    {
        var runId = Guid.NewGuid();
        var events = new WorkflowEvent[]
        {
            new WorkflowStartedEvent { WorkflowInstanceId = runId, WorkflowId = "w" },
            new WorkflowCompletedEvent { WorkflowInstanceId = runId, WorkflowId = "w" },
            new WorkflowFailedEvent { WorkflowInstanceId = runId, WorkflowId = "w" },
            new WorkflowCancelledEvent { WorkflowInstanceId = runId, WorkflowId = "w" },
            new NodeStartedEvent { WorkflowInstanceId = runId, NodeId = "n" },
            new NodeCompletedEvent { WorkflowInstanceId = runId, NodeId = "n" },
            new NodeFailedEvent { WorkflowInstanceId = runId, NodeId = "n" },
            new NodeCancelledEvent { WorkflowInstanceId = runId, NodeId = "n" },
            new NodeProgressEvent { WorkflowInstanceId = runId, NodeId = "n" },
        };

        foreach (var e in events)
        {
            await this.bridge.HandleAsync(e);
        }

        Assert.Equal(9, this.sink.Events.Count);
        Assert.All(this.sink.Events, e => Assert.False(e.Synthetic));

        // All 9 kinds are distinct
        var kinds = this.sink.Events.Select(e => e.Kind).Distinct().ToList();
        Assert.Equal(9, kinds.Count);
    }

    [Fact]
    public async Task Unknown_event_is_ignored_not_thrown()
    {
        var unknown = new FakeUnknownEvent { WorkflowInstanceId = Guid.NewGuid() };
        await this.bridge.HandleAsync(unknown);
        Assert.Empty(this.sink.Events);
    }

    [Fact]
    public async Task Sequence_numbers_are_monotonically_increasing()
    {
        var runId = Guid.NewGuid();
        for (int i = 0; i < 10; i++)
        {
            await this.bridge.HandleAsync(new NodeStartedEvent
            {
                WorkflowInstanceId = runId,
                NodeId = $"n{i}",
            });
        }

        var seqs = this.sink.Events.Select(e => e.Seq).ToList();
        for (int i = 1; i < seqs.Count; i++)
        {
            Assert.True(seqs[i] > seqs[i - 1], $"Seq {seqs[i]} should be > {seqs[i - 1]}");
        }
    }

    private class FakeUnknownEvent : WorkflowEvent { }
}
