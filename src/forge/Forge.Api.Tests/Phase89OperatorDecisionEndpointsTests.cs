// -----------------------------------------------------------------------
// <copyright file="Phase89OperatorDecisionEndpointsTests.cs" company="Microsoft Corp.">
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
/// Phase 89 — per-agent PO override endpoints
/// (<c>POST /api/agents/{id}/promote|demote|hold</c>) that wire up the
/// Performance view's <c>DecisionCard</c> buttons. The nightly
/// <c>PromotionJob</c> remains authoritative; these endpoints are explicit
/// human overrides that share the same cooldown clock and audit trail.
/// </summary>
public class Phase89OperatorDecisionEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string ActorHeader = "po@forge.local";

    private static async Task<AgentRow> SeedAgent(ControlDbContext control, ProductDbContext db, string id, int level = 0)
    {
        if (await control.Agents.AnyAsync(a => a.Id == id))
        {
            return await control.Agents.FirstAsync(a => a.Id == id);
        }
        var row = new AgentRow
        {
            Id = id, Name = id, Role = "eng",
            Level = level,
            HourlyUsd = ScoringConstants.HourlyForLevel(level),
        };
        control.Agents.Add(row);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        return row;
    }

    private static HttpClient MakeClient(ForgeWebApplicationFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", ActorHeader);
        return c;
    }

    // ── Promote ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_Bumps_Level_Writes_History_With_Actor_And_Recomputes_Hourly()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "promo-me", level: 3);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/promo-me/promote",
            new { expectedFromLevel = 3, windowAvg = 94.5 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("promo-me", body.GetProperty("agentId").GetString());
        Assert.Equal(3, body.GetProperty("oldLevel").GetInt32());
        Assert.Equal(4, body.GetProperty("newLevel").GetInt32());
        Assert.Equal("operator-promote", body.GetProperty("reason").GetString());
        Assert.Equal(ActorHeader, body.GetProperty("actor").GetString());
        Assert.Equal((double)ScoringConstants.HourlyForLevel(4), body.GetProperty("hourlyUsd").GetDouble());

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "promo-me");
            Assert.Equal(4, refreshed.Level);
            Assert.Equal(ScoringConstants.HourlyForLevel(4), refreshed.HourlyUsd);
            Assert.NotNull(refreshed.LevelUpdatedAt);

            var hist = await control.AgentLevelHistory.AsNoTracking()
                .Where(h => h.AgentId == "promo-me").SingleAsync();
            Assert.Equal(3, hist.OldLevel);
            Assert.Equal(4, hist.NewLevel);
            Assert.Equal(PromotionPolicy.ReasonOperatorPromote, hist.Reason);
            Assert.Equal(ActorHeader, hist.Actor);
            Assert.Equal(94.5m, hist.WindowAvg);
        }
    }

    [Fact]
    public async Task Promote_At_Max_Level_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "capped", level: ScoringConstants.MaxLevel);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/capped/promote",
            new { expectedFromLevel = ScoringConstants.MaxLevel, windowAvg = 99.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("agent.level.at_cap", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Promote_With_Stale_Expected_From_Level_Returns_409()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "stale", level: 5); // current is L5
        }

        // UI thought agent was at L3 (e.g. a stale tab) — expect 409.
        var resp = await c.PostAsJsonAsync(
            "/api/agents/stale/promote",
            new { expectedFromLevel = 3, windowAvg = 94.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("agent.level.stale", problem.GetProperty("title").GetString());

        // Agent must remain unchanged.
        using var verifyScope = f.Services.CreateScope();
        var control2 = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "stale");
        Assert.Equal(5, refreshed.Level);
        Assert.False(await control2.AgentLevelHistory.AnyAsync(h => h.AgentId == "stale"));
    }

    [Fact]
    public async Task Promote_Unknown_Agent_Returns_404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        // Boot the host so DB is created.
        using (f.CreateClient()) { /* boot */ }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/ghost/promote",
            new { expectedFromLevel = 0, windowAvg = (double?)null },
            JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Demote ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Demote_Drops_Level_And_Writes_Operator_Demote_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "drop-me", level: 4);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/drop-me/demote",
            new { expectedFromLevel = 4, windowAvg = 60.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = f.Services.CreateScope();
        var control2 = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "drop-me");
        Assert.Equal(3, refreshed.Level);
        Assert.Equal(ScoringConstants.HourlyForLevel(3), refreshed.HourlyUsd);

        var hist = await control2.AgentLevelHistory.AsNoTracking()
            .Where(h => h.AgentId == "drop-me").SingleAsync();
        Assert.Equal(4, hist.OldLevel);
        Assert.Equal(3, hist.NewLevel);
        Assert.Equal(PromotionPolicy.ReasonOperatorDemote, hist.Reason);
        Assert.Equal(ActorHeader, hist.Actor);
    }

    [Fact]
    public async Task Demote_At_Min_Level_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "floored", level: ScoringConstants.MinLevel);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/floored/demote",
            new { expectedFromLevel = ScoringConstants.MinLevel, windowAvg = 30.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("agent.level.at_floor", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Demote_Current_Crew_Lead_Below_Lead_Floor_Returns_409_Lead_Level_Too_Low()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // Agent at the lead floor (L7) — demoting would put them at L6.
            await SeedAgent(control, db, "lead-of-x", level: ScoringConstants.LeadMinLevel);
            control.Crews.Add(new CrewRow
            {
                Id = "crew-x", Name = "Crew X", Summary = "x",
                Lead = "lead-of-x",
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/lead-of-x/demote",
            new { expectedFromLevel = ScoringConstants.LeadMinLevel, windowAvg = 65.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("crew.lead.level_too_low", problem.GetProperty("title").GetString());

        // No mutation occurred.
        using var verifyScope = f.Services.CreateScope();
        var control2 = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "lead-of-x");
        Assert.Equal(ScoringConstants.LeadMinLevel, refreshed.Level);
        Assert.False(await control2.AgentLevelHistory.AnyAsync(h => h.AgentId == "lead-of-x"));
    }

    [Fact]
    public async Task Demote_Non_Lead_Below_Lead_Floor_Succeeds()
    {
        // A non-lead at L7 dropping to L6 is allowed — only crew leads
        // are gated by the floor.
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "staff-only", level: ScoringConstants.LeadMinLevel);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/staff-only/demote",
            new { expectedFromLevel = ScoringConstants.LeadMinLevel, windowAvg = 70.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Hold ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_Writes_Audit_Row_Without_Mutating_Level_Or_Hourly()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "park-me", level: 5);
        }

        var resp = await c.PostAsJsonAsync(
            "/api/agents/park-me/hold",
            new { expectedFromLevel = 5, windowAvg = 90.0 },
            JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(5, body.GetProperty("oldLevel").GetInt32());
        Assert.Equal(5, body.GetProperty("newLevel").GetInt32()); // unchanged
        Assert.Equal("operator-hold", body.GetProperty("reason").GetString());
        Assert.Equal(ActorHeader, body.GetProperty("actor").GetString());

        using var verifyScope = f.Services.CreateScope();
        var control2 = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "park-me");
        Assert.Equal(5, refreshed.Level);
        Assert.Equal(ScoringConstants.HourlyForLevel(5), refreshed.HourlyUsd);

        var hist = await control2.AgentLevelHistory.AsNoTracking()
            .Where(h => h.AgentId == "park-me").SingleAsync();
        Assert.Equal(5, hist.OldLevel);
        Assert.Equal(5, hist.NewLevel);
        Assert.Equal(PromotionPolicy.ReasonOperatorHold, hist.Reason);
        Assert.Equal(ActorHeader, hist.Actor);
    }

    // ── Cooldown sharing ──────────────────────────────────────────────────

    [Fact]
    public async Task Operator_Promote_Counts_Toward_Cooldown_In_Nightly_Run()
    {
        // Manual override at T-10 days should block an algorithm
        // promotion the same way a nightly-job promotion would.
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            await SeedAgent(control, db, "evalA");
            await SeedAgent(control, db, "fresh-promo", level: 4);
            // Manual operator-promote 10 days ago — well within the 30-day
            // standard-tier cooldown.
            control.AgentLevelHistory.Add(new AgentLevelHistoryRow
            {
                AgentId = "fresh-promo",
                OldLevel = 3, NewLevel = 4,
                Reason = PromotionPolicy.ReasonOperatorPromote,
                Actor = ActorHeader,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                WindowAvg = 95m,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // Seed 20 passing deliverables so the runner WOULD promote
            // if the cooldown were absent.
            for (var i = 0; i < 20; i++)
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = $"wi-fresh-{i}",
                    CrewId = "crew-x",
                    GeneratorAgentId = "fresh-promo",
                    EvaluatorAgentId = "evalA",
                    Iter = 1,
                    Score = 95,
                    Verdict = "pass",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-i),
                });
            }
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        // Hit the nightly-job endpoint.
        var resp = await c.PostAsJsonAsync("/api/admin/promotion/run", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Agent must remain at L4 — cooldown should have blocked the
        // re-promotion even though the prior bump came from operator-promote.
        using var verifyScope = f.Services.CreateScope();
        var control2 = verifyScope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "fresh-promo");
        Assert.Equal(4, refreshed.Level);
    }

    // ── Validation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_Without_Body_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = MakeClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "no-body", level: 2);
        }

        // Send `null` body — the minimal-API binding will see [FromBody]
        // as null and the handler refuses with 400.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/agents/no-body/promote")
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
