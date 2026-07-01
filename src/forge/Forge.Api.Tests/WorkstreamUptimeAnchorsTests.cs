// -----------------------------------------------------------------------
// <copyright file="WorkstreamUptimeAnchorsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Globalization;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Covers the swarm-header uptime anchors added to
/// <see cref="ConversationInsightQueries.GetIterKpisAsync"/>: <c>startedAt</c>
/// (when the agent pair began the task — earliest agent_usage row) and
/// <c>currentIterStartedAt</c> (the running iteration's start, or null when
/// not iterating). The critical case is a workstream that is mid-iteration-1
/// with NO deliverable yet — the endpoint must still report timing rather than
/// returning early, so the header shows a live uptime while work is ongoing.
/// </summary>
public class WorkstreamUptimeAnchorsTests
{
    private const string WiId = "ws-uptime-anchors-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    private static (string? StartedAt, string? CurrentIterStartedAt, int IterCount) Read(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var iters = root.GetProperty("iters").GetArrayLength();
        return (Str("startedAt"), Str("currentIterStartedAt"), iters);
    }

    private static DateTimeOffset Parse(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public async Task InFlightIter1_NoDeliverable_ReportsUptimeAnchors()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Pair started at t0 (earliest agent_usage row); iteration 1's
        // task.iteration.started landed 5s later. No deliverable exists yet —
        // this is the "actively running iter 1" case that previously returned
        // early with no timing.
        var pairStart = DateTimeOffset.UtcNow.AddMinutes(-3);
        var iter1Start = pairStart.AddSeconds(5);

        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-gen", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 100, OutputTokens = 10, CostUsd = 0.01m,
            StartedAt = pairStart, EndedAt = null, // still running
            Status = "running", CostCategory = "generator",
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WiId, Kind = "task.iteration.started",
            PayloadJson = "{\"iter\":1}", At = iter1Start,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(control, db, WiId, CancellationToken.None);
        var (startedAt, currentIterStartedAt, iterCount) = Read(payload);

        Assert.Equal(0, iterCount); // no deliverable → no iter rows
        Assert.NotNull(startedAt);
        Assert.NotNull(currentIterStartedAt);
        // startedAt = earliest agent_usage row (pair start).
        Assert.Equal(pairStart, Parse(startedAt!));
        // currentIterStartedAt = the running iteration's start.
        Assert.Equal(iter1Start, Parse(currentIterStartedAt!));
    }

    [Fact]
    public async Task BetweenIters_Scored_HasNoCurrentIter()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var pairStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var iter1Start = pairStart.AddSeconds(5);
        var iter1Scored = iter1Start.AddMinutes(2);

        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-gen", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 100, OutputTokens = 10, CostUsd = 0.01m,
            StartedAt = pairStart, EndedAt = iter1Scored,
            Status = "finished", CostCategory = "generator",
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WiId, Kind = "task.iteration.started",
            PayloadJson = "{\"iter\":1}", At = iter1Start,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WiId, Kind = "task.iteration.scored",
            PayloadJson = "{\"iter\":1}", At = iter1Scored,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId, CrewId = "crew-x", GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval", Iter = 1, Score = 88, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = iter1Scored,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(control, db, WiId, CancellationToken.None);
        var (startedAt, currentIterStartedAt, iterCount) = Read(payload);

        Assert.Equal(1, iterCount);
        Assert.Equal(pairStart, Parse(startedAt!));
        // The last event was a completion (scored) → not currently iterating.
        Assert.Null(currentIterStartedAt);
    }

    [Fact]
    public async Task Iter2Running_AfterIter1Scored_PointsAtIter2Start()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var pairStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var iter1Scored = pairStart.AddMinutes(2);
        var iter2Start = iter1Scored.AddMinutes(1); // newer than the last completion

        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-gen", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 100, OutputTokens = 10, CostUsd = 0.01m,
            StartedAt = pairStart, EndedAt = iter1Scored,
            Status = "finished", CostCategory = "generator",
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId, CrewId = "crew-x", GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval", Iter = 1, Score = 88, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = iter1Scored,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WiId, Kind = "task.iteration.scored",
            PayloadJson = "{\"iter\":1}", At = iter1Scored,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WiId, Kind = "task.iteration.started",
            PayloadJson = "{\"iter\":2}", At = iter2Start,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(control, db, WiId, CancellationToken.None);
        var (startedAt, currentIterStartedAt, _) = Read(payload);

        Assert.Equal(pairStart, Parse(startedAt!));
        // iter 2 started after iter 1's completion → it's the running iteration.
        Assert.NotNull(currentIterStartedAt);
        Assert.Equal(iter2Start, Parse(currentIterStartedAt!));
    }
}
