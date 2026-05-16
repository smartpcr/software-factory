// -----------------------------------------------------------------------
// <copyright file="Phase72CacheTokensTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-16 — Phase 72 regression suite for the prompt-cache +
/// reasoning + conversation token plumbing. Validates:
/// <list type="number">
///   <item>The migration adds CacheReadTokens / CacheWriteTokens /
///   ReasoningTokens / ConversationTokens columns idempotently.</item>
///   <item><see cref="AgentUsageRow"/> persists the new fields.</item>
///   <item><see cref="ConversationInsightQueries.GetIterKpisAsync"/>
///   projects them into the per-iter rollup.</item>
/// </list>
/// </summary>
public class Phase72CacheTokensTests
{
    private const string WiId = "ws-phase72-cache-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    [Fact]
    public async Task Migration_Adds_All_Four_Columns_To_AgentUsage()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        // pragma_table_info exposes column names for the live schema.
        var expected = new[] { "CacheReadTokens", "CacheWriteTokens", "ReasoningTokens", "ConversationTokens" };
        foreach (var col in expected)
        {
            await using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM pragma_table_info('agent_usage') WHERE name = @name";
            probe.Parameters.AddWithValue("@name", col);
            var result = await probe.ExecuteScalarAsync();
            Assert.NotNull(result);
        }
    }

    [Fact]
    public async Task AgentUsageRow_Persists_All_Four_Nullable_Token_Columns()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-1",
            CrewId = "crew-x",
            TaskId = WiId,
            Model = "claude-sonnet-4-6",
            InputTokens = 10_000,
            OutputTokens = 2_000,
            CostUsd = 0.42m,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Status = "finished",
            CostCategory = "generator",
            CacheReadTokens = 40_000,
            CacheWriteTokens = 1_200,
            ReasoningTokens = 800,
            ConversationTokens = 55_000,
        });
        await db.SaveChangesAsync();

        using var verify = f.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await verifyDb.AgentUsage.AsNoTracking().FirstAsync(u => u.TaskId == WiId);

        Assert.Equal(40_000L, row.CacheReadTokens);
        Assert.Equal(1_200L, row.CacheWriteTokens);
        Assert.Equal(800L, row.ReasoningTokens);
        Assert.Equal(55_000L, row.ConversationTokens);
    }

    [Fact]
    public async Task IterKpis_Projects_Cache_Read_Write_Reasoning_Conversation()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var iterStarted = DateTimeOffset.UtcNow.AddMinutes(-10);
        var iterCreated = iterStarted.AddMinutes(2);

        // Seed one deliverable so the iter-kpis window has a row to anchor.
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId,
            CrewId = "crew-x",
            GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval",
            Iter = 1,
            Score = 88,
            Verdict = "iterate",
            DimsJson = "{}",
            CreatedAt = iterCreated,
        });
        // One generator + one evaluator usage row inside the window.
        // ConversationTokens latest-wins → evaluator's 60K should be picked.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-gen", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 5_000, OutputTokens = 1_000, CostUsd = 0.10m,
            StartedAt = iterStarted.AddSeconds(10), EndedAt = iterStarted.AddSeconds(60),
            Status = "finished", CostCategory = "generator",
            CacheReadTokens = 30_000, CacheWriteTokens = 800, ReasoningTokens = 200,
            ConversationTokens = 45_000,
        });
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-eval", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 3_000, OutputTokens = 500, CostUsd = 0.04m,
            StartedAt = iterStarted.AddSeconds(70), EndedAt = iterStarted.AddSeconds(100),
            Status = "finished", CostCategory = "evaluator",
            CacheReadTokens = 10_000, CacheWriteTokens = 400, ReasoningTokens = 100,
            ConversationTokens = 60_000,
        });
        await db.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(db, WiId, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var iters = doc.RootElement.GetProperty("iters").EnumerateArray().ToArray();
        Assert.Single(iters);
        var iter = iters[0];

        Assert.Equal(1, iter.GetProperty("iter").GetInt32());
        Assert.Equal(88, iter.GetProperty("score").GetInt32());
        Assert.Equal(8_000L, iter.GetProperty("inputTokens").GetInt64()); // 5K + 3K
        Assert.Equal(1_500L, iter.GetProperty("outputTokens").GetInt64());
        // Cache read / write / reasoning are summed across generator + evaluator.
        Assert.Equal(40_000L, iter.GetProperty("cacheReadTokens").GetInt64());
        Assert.Equal(1_200L, iter.GetProperty("cacheWriteTokens").GetInt64());
        Assert.Equal(300L, iter.GetProperty("reasoningTokens").GetInt64());
        // ConversationTokens latest-wins: evaluator (StartedAt later) → 60K.
        Assert.Equal(60_000L, iter.GetProperty("conversationTokens").GetInt64());
    }

    [Fact]
    public async Task IterKpis_Returns_Null_Cache_Fields_When_No_Usage_Reported()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Deliverable only, no agent_usage with cache columns set —
        // simulates a pre-Phase-72 row or a CLI runner.
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = WiId,
            CrewId = "crew-x",
            GeneratorAgentId = "ag-gen",
            EvaluatorAgentId = "ag-eval",
            Iter = 1,
            Score = 60,
            Verdict = "iterate",
            DimsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "ag-gen", CrewId = "crew-x", TaskId = WiId, Model = "x",
            InputTokens = 5_000, OutputTokens = 1_000, CostUsd = 0.10m,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30), EndedAt = DateTimeOffset.UtcNow,
            Status = "finished", CostCategory = "generator",
            // No cache / reasoning / conversation columns set → null in DB.
        });
        await db.SaveChangesAsync();

        var payload = await ConversationInsightQueries.GetIterKpisAsync(db, WiId, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var iter = doc.RootElement.GetProperty("iters").EnumerateArray().First();

        // Without any cache-token telemetry, all four cache/reasoning/
        // conversation fields are null so the UI renders "no cache data".
        Assert.Equal(System.Text.Json.JsonValueKind.Null, iter.GetProperty("cacheReadTokens").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, iter.GetProperty("cacheWriteTokens").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, iter.GetProperty("reasoningTokens").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, iter.GetProperty("conversationTokens").ValueKind);
    }
}
