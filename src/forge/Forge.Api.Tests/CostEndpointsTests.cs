// -----------------------------------------------------------------------
// <copyright file="CostEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 11 — <c>GET /api/cost/summary</c> aggregates the agent_usage ledger
/// by day × epic × agent. Covers the three contracts the UI relies on:
/// <list type="bullet">
///   <item>Today / week / cap totals match the underlying rows.</item>
///   <item>Daily series spans the requested window and bucket-by-epic
///         resolves <c>TaskId</c> through the cards table.</item>
///   <item>Per-agent burn separates 24h from 7d and reports tokens.</item>
/// </list>
/// </summary>
public class CostEndpointsTests
{
    [Fact]
    public async Task Returns_Default_7Day_Window_With_Aggregated_Totals()
    {
        using var factory = new ForgeWebApplicationFactory();
        var now = DateTimeOffset.UtcNow;
        var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        // The test factory boots an empty DB (only crews + agents are
        // seeded), so insert a card explicitly to exercise the TaskId →
        // epic resolution.
        const string sampleCardId = "card-cost-test-1";
        const string sampleCardEpic = "EPIC-024";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Cards.Add(new CardRow
            {
                Id = sampleCardId,
                Sort = 1,
                Epic = sampleCardEpic,
                Col = "build",
                Type = "task",
                Title = "cost test",
                Pts = 1,
                Priority = "P2",
            });

            // Three rows in the 7d window: two for today (one bound to
            // the card so it lands in that epic, one unassigned), one
            // four days ago.
            db.AgentUsage.AddRange(
                Row("E4", "crew-eng", 1.20m, sampleCardId, startOfToday.AddHours(2), input: 1000, output: 500),
                Row("E4", "crew-eng", 0.30m, taskId: string.Empty, startedAt: startOfToday.AddHours(3), input: 200, output: 100),
                Row("R3", "crew-rev", 0.40m, sampleCardId, startOfToday.AddDays(-4), input: 400, output: 100));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/cost/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(7, root.GetProperty("days").GetArrayLength());
        Assert.Equal(6, root.GetProperty("todayIndex").GetInt32());

        var todayUsd = root.GetProperty("todayUsd").GetDecimal();
        Assert.True(todayUsd >= 1.50m, $"todayUsd={todayUsd} expected >= 1.50 (1.20 + 0.30 from new rows; seed may add more)");

        var week = root.GetProperty("week7dUsd").GetDecimal();
        Assert.True(week >= 1.90m, $"week7dUsd={week} expected >= 1.90 (sum of new rows; seed may add more)");

        // Epic bucket: the card-bound row should land in the card's epic.
        var todayBucket = root.GetProperty("days")[6];
        var byEpic = todayBucket.GetProperty("byEpic");
        Assert.True(byEpic.TryGetProperty(sampleCardEpic, out var epicSlice),
            $"today's byEpic missing '{sampleCardEpic}'; got {byEpic}");
        Assert.True(epicSlice.GetDecimal() >= 1.20m);
        Assert.True(byEpic.TryGetProperty("unassigned", out var unassigned)
            && unassigned.GetDecimal() >= 0.30m, "unassigned bucket must include rows with empty TaskId");
    }

    [Fact]
    public async Task Reports_Per_Agent_Burn_With_Tokens_And_Spark()
    {
        using var factory = new ForgeWebApplicationFactory();
        var now = DateTimeOffset.UtcNow;
        var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.AgentUsage.AddRange(
                Row("E4", "crew-eng", 0.50m, taskId: string.Empty, startedAt: startOfToday.AddHours(1), input: 1000, output: 500),
                Row("E4", "crew-eng", 0.20m, taskId: string.Empty, startedAt: startOfToday.AddDays(-3), input: 400, output: 100));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/cost/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var agents = doc.RootElement.GetProperty("agents");
        var e4 = agents.EnumerateArray().FirstOrDefault(a => a.GetProperty("id").GetString() == "E4");
        Assert.NotEqual(JsonValueKind.Undefined, e4.ValueKind);

        Assert.True(e4.GetProperty("cost7dUsd").GetDecimal() >= 0.70m);
        Assert.True(e4.GetProperty("cost24hUsd").GetDecimal() >= 0.50m);
        Assert.True(e4.GetProperty("tokens").GetInt64() >= 2000); // 1500 + 500
        Assert.Equal(7, e4.GetProperty("spark").GetArrayLength());
    }

    private static AgentUsageRow Row(
        string agentId,
        string crewId,
        decimal cost,
        string taskId,
        DateTimeOffset startedAt,
        long input = 100,
        long output = 100) => new()
    {
        AgentId = agentId,
        CrewId = crewId,
        TaskId = taskId,
        Model = "local-stub",
        InputTokens = input,
        OutputTokens = output,
        CostUsd = cost,
        StartedAt = startedAt,
        EndedAt = startedAt,
        Status = "finished",
    };
}
