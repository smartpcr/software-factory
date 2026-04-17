// -----------------------------------------------------------------------
// <copyright file="RunHistoryBufferTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using WorkflowUi.Server.Hubs;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests for RunHistoryBuffer ring buffer semantics.
/// </summary>
public class RunHistoryBufferTests
{
    [Fact]
    public void Append_and_replay_roundtrips()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();

        for (int i = 1; i <= 10; i++)
        {
            buffer.Append(new RunEvent { RunId = runId, Seq = i, Kind = RunEventKind.NodeStarted });
        }

        var replay = buffer.Replay(runId, 1);
        Assert.False(replay.NotFound);
        Assert.False(replay.Truncated);
        Assert.Equal(10, replay.Events.Count);
        Assert.Equal(1, replay.Events.First().Seq);
        Assert.Equal(10, replay.Events.Last().Seq);
    }

    [Fact]
    public void Replay_ascending_seq_order()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();

        for (int i = 1; i <= 50; i++)
        {
            buffer.Append(new RunEvent { RunId = runId, Seq = i });
        }

        var replay = buffer.Replay(runId, 25);
        Assert.Equal(26, replay.Events.Count);
        for (int i = 1; i < replay.Events.Count; i++)
        {
            Assert.True(replay.Events[i].Seq > replay.Events[i - 1].Seq);
        }
    }

    [Fact]
    public void Capacity_is_exactly_1000_per_run()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();

        for (int i = 1; i <= 1001; i++)
        {
            buffer.Append(new RunEvent { RunId = runId, Seq = i });
        }

        var replay = buffer.Replay(runId, 1);
        Assert.True(replay.Truncated);
        Assert.Equal(1000, replay.Events.Count);
        Assert.Equal(2, replay.Events.First().Seq); // oldest was dropped
        Assert.Equal(1001, replay.Events.Last().Seq);
    }

    [Fact]
    public void Replay_with_fromSeq_below_oldest_returns_truncated()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();

        for (int i = 1; i <= 1050; i++)
        {
            buffer.Append(new RunEvent { RunId = runId, Seq = i });
        }

        var replay = buffer.Replay(runId, 10);
        Assert.True(replay.Truncated);
        Assert.Equal(51, replay.OldestAvailableSeq);
    }

    [Fact]
    public void Replay_unknown_runId_returns_NotFound()
    {
        var buffer = new RunHistoryBuffer();
        var replay = buffer.Replay(Guid.NewGuid(), 1);
        Assert.True(replay.NotFound);
    }

    [Fact]
    public void Evict_removes_run()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();
        buffer.Append(new RunEvent { RunId = runId, Seq = 1 });

        buffer.Evict(runId);

        var replay = buffer.Replay(runId, 1);
        Assert.True(replay.NotFound);
    }

    [Fact]
    public async Task Concurrent_append_and_replay_without_loss()
    {
        var buffer = new RunHistoryBuffer();
        var runId = Guid.NewGuid();
        const int appendCount = 500;
        var results = new ConcurrentBag<long>();

        var appendTasks = Enumerable.Range(0, 10).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < appendCount / 10; i++)
            {
                var seq = (t * (appendCount / 10)) + i + 1;
                buffer.Append(new RunEvent { RunId = runId, Seq = seq });
            }
        }));

        var replayTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var replay = buffer.Replay(runId, 1);
            foreach (var e in replay.Events)
            {
                results.Add(e.Seq);
            }
        }));

        await Task.WhenAll(appendTasks);
        await Task.WhenAll(replayTasks);

        // Final replay should have all events
        var finalReplay = buffer.Replay(runId, 1);
        Assert.Equal(appendCount, finalReplay.Events.Count);
    }
}
