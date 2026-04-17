// -----------------------------------------------------------------------
// <copyright file="TelemetryHubSinkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Telemetry;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Hubs;
using WorkflowUi.Server.Persistence;
using WorkflowUi.Server.Telemetry;
using Xunit;

/// <summary>
/// Tests that <see cref="TelemetryHubSink"/> persists events to JSONL via <see cref="RunHistoryWriter"/>
/// and appends to the <see cref="RunHistoryBuffer"/>.
/// </summary>
public class TelemetryHubSinkTests : IDisposable
{
    private readonly string tempDir;
    private readonly RunHistoryBuffer buffer;
    private readonly RunHistoryWriter writer;
    private readonly RunHistoryReader reader;
    private readonly TelemetryHubSink sink;

    public TelemetryHubSinkTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "sink-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);

        var options = Options.Create(new PersistenceOptions { RunsRoot = this.tempDir });
        this.buffer = new RunHistoryBuffer();
        this.writer = new RunHistoryWriter(options, NullLogger<RunHistoryWriter>.Instance);
        this.reader = new RunHistoryReader(options);

        var hubContext = new FakeHubContext();
        this.sink = new TelemetryHubSink(
            hubContext,
            this.buffer,
            this.writer,
            NullLogger<TelemetryHubSink>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_Persists_To_JSONL()
    {
        var runId = Guid.NewGuid();
        var evt = MakeEvent(runId, 1);

        await this.sink.PublishAsync(evt);

        var persisted = this.reader.Read(runId);
        Assert.Single(persisted);
        Assert.Equal(1, persisted[0].Seq);
        Assert.Equal(runId, persisted[0].RunId);
    }

    [Fact]
    public async Task PublishAsync_Appends_To_Buffer()
    {
        var runId = Guid.NewGuid();
        var evt = MakeEvent(runId, 1);

        await this.sink.PublishAsync(evt);

        var replay = this.buffer.Replay(runId, 0);
        Assert.Single(replay.Events);
    }

    [Fact]
    public async Task PublishAsync_Multiple_Events_Preserves_Order()
    {
        var runId = Guid.NewGuid();
        for (int i = 1; i <= 5; i++)
        {
            await this.sink.PublishAsync(MakeEvent(runId, i));
        }

        var persisted = this.reader.Read(runId);
        Assert.Equal(5, persisted.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, persisted[i].Seq);
        }
    }

    private static RunEvent MakeEvent(Guid runId, long seq)
    {
        return new RunEvent
        {
            RunId = runId,
            Seq = seq,
            Kind = RunEventKind.NodeStarted,
            Synthetic = false,
            TimestampUtc = DateTime.UtcNow,
            NodeId = "test-node",
        };
    }

    /// <summary>
    /// Minimal fake hub context that does not send SignalR messages.
    /// </summary>
    private class FakeHubContext : IHubContext<TelemetryHub, ITelemetryClient>
    {
        public IHubClients<ITelemetryClient> Clients { get; } = new FakeHubClients();
        public IGroupManager Groups { get; } = null!;
    }

    private class FakeHubClients : IHubClients<ITelemetryClient>
    {
        private readonly FakeTelemetryClient client = new();

        public ITelemetryClient All => this.client;

        public ITelemetryClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => this.client;

        public ITelemetryClient Client(string connectionId) => this.client;

        public ITelemetryClient Clients(IReadOnlyList<string> connectionIds) => this.client;

        public ITelemetryClient Group(string groupName) => this.client;

        public ITelemetryClient Groups(IReadOnlyList<string> groupNames) => this.client;

        public ITelemetryClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this.client;

        public ITelemetryClient User(string userId) => this.client;

        public ITelemetryClient Users(IReadOnlyList<string> userIds) => this.client;
    }

    private class FakeTelemetryClient : ITelemetryClient
    {
        public Task ReceiveRunEvent(RunEvent runEvent) => Task.CompletedTask;
    }
}
