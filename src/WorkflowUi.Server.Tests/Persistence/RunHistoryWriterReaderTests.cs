// -----------------------------------------------------------------------
// <copyright file="RunHistoryWriterReaderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Persistence;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Persistence;
using WorkflowUi.Server.Telemetry;
using Xunit;

public class RunHistoryWriterReaderTests : IDisposable
{
    private readonly string tempDir;
    private readonly RunHistoryWriter writer;
    private readonly RunHistoryReader reader;

    public RunHistoryWriterReaderTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);

        var options = Options.Create(new PersistenceOptions { RunsRoot = this.tempDir });
        this.writer = new RunHistoryWriter(options, NullLogger<RunHistoryWriter>.Instance);
        this.reader = new RunHistoryReader(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Append_Creates_JSONL_File()
    {
        var runId = Guid.NewGuid();
        var evt = MakeEvent(runId, 1);

        await this.writer.AppendAsync(evt);

        var filePath = Path.Combine(this.tempDir, runId.ToString(), "events.jsonl");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task Write_Then_Read_RoundTrips()
    {
        var runId = Guid.NewGuid();
        var evt1 = MakeEvent(runId, 1, RunEventKind.WorkflowStarted);
        var evt2 = MakeEvent(runId, 2, RunEventKind.NodeStarted, "node-a");
        var evt3 = MakeEvent(runId, 3, RunEventKind.NodeCompleted, "node-a");

        await this.writer.AppendAsync(evt1);
        await this.writer.AppendAsync(evt2);
        await this.writer.AppendAsync(evt3);

        var events = this.reader.Read(runId);

        Assert.Equal(3, events.Count);
        Assert.Equal(1, events[0].Seq);
        Assert.Equal(RunEventKind.WorkflowStarted, events[0].Kind);
        Assert.Equal(2, events[1].Seq);
        Assert.Equal("node-a", events[1].NodeId);
        Assert.Equal(3, events[2].Seq);
    }

    [Fact]
    public void Read_NonExistentRun_ReturnsEmpty()
    {
        var events = this.reader.Read(Guid.NewGuid());
        Assert.Empty(events);
    }

    [Fact]
    public async Task Multiple_Runs_Isolated()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();

        await this.writer.AppendAsync(MakeEvent(run1, 1));
        await this.writer.AppendAsync(MakeEvent(run1, 2));
        await this.writer.AppendAsync(MakeEvent(run2, 1));

        Assert.Equal(2, this.reader.Read(run1).Count);
        Assert.Single(this.reader.Read(run2));
    }

    [Fact]
    public async Task Ordering_Preserved()
    {
        var runId = Guid.NewGuid();
        for (int i = 1; i <= 10; i++)
        {
            await this.writer.AppendAsync(MakeEvent(runId, i));
        }

        var events = this.reader.Read(runId);
        Assert.Equal(10, events.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i + 1, events[i].Seq);
        }
    }

    private static RunEvent MakeEvent(
        Guid runId,
        long seq,
        RunEventKind kind = RunEventKind.WorkflowStarted,
        string? nodeId = null)
    {
        return new RunEvent
        {
            RunId = runId,
            Seq = seq,
            Kind = kind,
            Synthetic = false,
            TimestampUtc = DateTime.UtcNow,
            NodeId = nodeId,
        };
    }
}
