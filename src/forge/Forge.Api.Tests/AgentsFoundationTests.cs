// -----------------------------------------------------------------------
// <copyright file="AgentsFoundationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 11 Batch A — foundation smoke tests:
///   * 11.2 GET /api/models returns the seeded catalog.
///   * 11.4 agent_usage table exists with the expected columns + indexes.
///   * 11.1 IAgentRunner is registered (NoopAgentRunner placeholder).
/// </summary>
public class AgentsFoundationTests
{
    [Fact]
    public async Task GetModels_Returns_Seeded_Catalog()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        var ids = root.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("claude-opus-4-7", ids);
        Assert.Contains("claude-sonnet-4-6", ids);
        Assert.Contains("claude-haiku-4-5-20251001", ids);
        Assert.Contains("local-stub", ids);
    }

    [Fact]
    public async Task GetModel_ById_Returns_Pricing()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/models/claude-opus-4-7");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("claude-opus-4-7", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("inputPricePerMTok").GetDecimal() > 0);
        Assert.True(doc.RootElement.GetProperty("outputPricePerMTok").GetDecimal() > 0);
    }

    [Fact]
    public async Task GetModel_Unknown_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/models/no-such-model");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AgentUsage_Table_Exists_With_Indexes()
    {
        using var factory = new ForgeWebApplicationFactory();
        // Force the host to spin up so EnsureCreated + Phase11Migration run.
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        // Table exists.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='agent_usage';";
            var name = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("agent_usage", name);
        }

        // Both indexes are present.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='agent_usage' ORDER BY name;";
            var names = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            Assert.Contains(names, n => n.Contains("CrewId", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, n => n.Contains("TaskId", StringComparison.OrdinalIgnoreCase));
        }

        // EF round-trip writes + reads cleanly.
        var row = new AgentUsageRow
        {
            AgentId = "E4",
            CrewId = "engineering",
            TaskId = "234",
            Model = "claude-opus-4-7",
            InputTokens = 1234,
            OutputTokens = 567,
            CostUsd = 0.0610m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            Status = "finished",
        };
        control.AgentUsage.Add(row);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var refetched = await control.AgentUsage.AsNoTracking().FirstAsync(r => r.Id == row.Id);
        Assert.Equal("engineering", refetched.CrewId);
        Assert.Equal("234", refetched.TaskId);
        Assert.Equal(0.0610m, refetched.CostUsd);
    }

    [Fact]
    public void IAgentRunner_Resolves_From_Di()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
        Assert.NotNull(runner);
        // Phase 11.5b — AgentRunnerDispatcher is the single IAgentRunner;
        // it routes per-call across the four (backend × mode) cells.
        Assert.IsType<AgentRunnerDispatcher>(runner);
    }
}
