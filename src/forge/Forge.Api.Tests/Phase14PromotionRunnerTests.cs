// -----------------------------------------------------------------------
// <copyright file="Phase14PromotionRunnerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 14.7 — DB-backed runner tests + the
/// <c>POST /api/admin/promotion/run</c> endpoint.
/// </summary>
public class Phase14PromotionRunnerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task SeedAgent(ProductDbContext db, string id, int level = 0)
    {
        if (await db.Agents.AnyAsync(a => a.Id == id)) return;
        db.Agents.Add(new AgentRow
        {
            Id = id, Name = id, Role = "eng",
            Level = level,
            HourlyUsd = ScoringConstants.HourlyForLevel(level),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDeliverables(ProductDbContext db, string genId, int count, int score, string verdict = "pass")
    {
        for (var i = 0; i < count; i++)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"wi-{genId}-{i}",
                CrewId = "crew-x",
                GeneratorAgentId = genId,
                EvaluatorAgentId = "evalA",
                Iter = 1,
                Score = score,
                Verdict = verdict,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-i),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Promotes_Agent_With_20_Passes_And_Updates_Hourly()
    {
        using var f = new ForgeWebApplicationFactory();
        // Boot the host so the DB is created.
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        await SeedAgent(db, "evalA");
        await SeedAgent(db, "promote-me", level: 3);
        await SeedDeliverables(db, "promote-me", 20, 95);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(CancellationToken.None);

        Assert.True(result.Promoted >= 1);

        var refreshed = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == "promote-me");
        Assert.Equal(4, refreshed.Level);
        Assert.Equal(ScoringConstants.HourlyForLevel(4), refreshed.HourlyUsd);
        Assert.NotNull(refreshed.LevelUpdatedAt);

        var hist = await db.AgentLevelHistory.Where(h => h.AgentId == "promote-me").SingleAsync();
        Assert.Equal(3, hist.OldLevel);
        Assert.Equal(4, hist.NewLevel);
        Assert.Equal(PromotionPolicy.ReasonPromote, hist.Reason);
    }

    [Fact]
    public async Task Demotes_Agent_With_20_Iterates_Below_75()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        await SeedAgent(db, "evalA");
        await SeedAgent(db, "demote-me", level: 5);
        await SeedDeliverables(db, "demote-me", 20, 70, verdict: "iterate");

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(CancellationToken.None);

        Assert.True(result.Demoted >= 1);

        var refreshed = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == "demote-me");
        Assert.Equal(4, refreshed.Level);

        var hist = await db.AgentLevelHistory.Where(h => h.AgentId == "demote-me").SingleAsync();
        Assert.Equal(PromotionPolicy.ReasonDemote, hist.Reason);
    }

    [Fact]
    public async Task FinalIter_Filter_Counts_Only_Last_Attempt_Per_WorkItem()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        await SeedAgent(db, "evalA");
        await SeedAgent(db, "iter-agent", level: 3);

        // 20 work items, each with 3 iterations: scores 50, 70, 95.
        // Average of FINAL iters = 95 → should promote. If we (wrongly)
        // averaged all rows the avg would be ~72 and the agent would demote.
        for (var i = 0; i < 20; i++)
        {
            for (var iter = 1; iter <= 3; iter++)
            {
                var score = iter switch { 1 => 50, 2 => 70, _ => 95 };
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = $"wi-iter-{i}",
                    CrewId = "crew-x",
                    GeneratorAgentId = "iter-agent",
                    EvaluatorAgentId = "evalA",
                    Iter = iter,
                    Score = score,
                    Verdict = iter == 3 ? "pass" : "iterate",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-i).AddMinutes(iter),
                });
            }
        }
        await db.SaveChangesAsync();

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(CancellationToken.None);

        var refreshed = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == "iter-agent");
        Assert.Equal(4, refreshed.Level);

        var d = result.Decisions.First(x => x.AgentId == "iter-agent");
        Assert.Equal(PromotionPolicy.ReasonPromote, d.Reason);
        Assert.Equal(95m, d.RollingAvg);
    }

    [Fact]
    public async Task Cooldown_Blocks_Repromotion_Within_30_Days()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        await SeedAgent(db, "evalA");
        await SeedAgent(db, "cooldown-agent", level: 4);
        // Recent promotion 10 days ago.
        db.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = "cooldown-agent", OldLevel = 3, NewLevel = 4,
            Reason = PromotionPolicy.ReasonPromote,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            WindowAvg = 95,
        });
        await db.SaveChangesAsync();
        await SeedDeliverables(db, "cooldown-agent", 20, 95);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(CancellationToken.None);

        var refreshed = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == "cooldown-agent");
        Assert.Equal(4, refreshed.Level); // not promoted again
    }

    [Fact]
    public async Task Endpoint_Run_Returns_Decisions_Including_Noops()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(db, "evalA");
            await SeedAgent(db, "active1", level: 3);
            await SeedDeliverables(db, "active1", 20, 95);
        }

        var resp = await c.PostAsJsonAsync("/api/admin/promotion/run", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("agentsScanned").GetInt32() >= 1);
        Assert.True(body.RootElement.GetProperty("promoted").GetInt32() >= 1);
    }

    [Fact]
    public async Task Level_History_Endpoint_Returns_Rows_For_Agent()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(db, "hist-agent");
            db.AgentLevelHistory.Add(new AgentLevelHistoryRow
            {
                AgentId = "hist-agent", OldLevel = 2, NewLevel = 3,
                Reason = PromotionPolicy.ReasonPromote,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var resp = await c.GetAsync("/api/agents/hist-agent/level-history");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("promote", arr[0].GetProperty("reason").GetString());
    }
}
