// -----------------------------------------------------------------------
// <copyright file="Phase91AutoPromotionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 91 — nightly auto-promotion scheduler + the avg/throughput gate.
///
/// <para>The scheduler runs <see cref="IPromotionRunner"/> with
/// <see cref="PromotionRunOptions.NightlyAuto"/>: promotion-only, and an agent
/// is auto-promoted only when the tier verdict is already
/// <see cref="PromotionPolicy.ReasonPromote"/> (avg ≥ tier threshold, zero
/// rejects, cooldown clear — Promo-ETA "ready") AND its trailing-window average
/// is strictly &gt; <see cref="ScoringConstants.AutoPromoteMinAvg"/> (90) AND
/// its throughput is ≥ <see cref="ScoringConstants.AutoPromoteMinThroughput"/>
/// (5). Manual / admin runs ignore the gate and still demote.</para>
/// </summary>
public class Phase91AutoPromotionTests
{
    private static async Task SeedAgent(ControlDbContext control, ProductDbContext db, string id, int level)
    {
        if (await control.Agents.AnyAsync(a => a.Id == id)) return;
        control.Agents.Add(new AgentRow
        {
            Id = id, Name = id, Role = "eng",
            Level = level,
            HourlyUsd = ScoringConstants.HourlyForLevel(level),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    private static async Task SeedDeliverables(
        ControlDbContext control, ProductDbContext db, string genId, int count, int score, string verdict = "pass")
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
                DimsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-i),
            });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    // ── Gate: avg must be strictly > 90 ────────────────────────────────

    [Fact]
    public async Task NightlyAuto_Gate_Blocks_Promote_When_Avg_Not_Over_90()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedAgent(control, db, "evalA", 0);
        // L1 = easy tier (window 3, avg ≥ 88). Avg 89 → tier verdict = promote,
        // but the nightly gate (avg > 90) holds it back.
        await SeedAgent(control, db, "borderline", level: 1);
        await SeedDeliverables(control, db, "borderline", count: 5, score: 89);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(PromotionRunOptions.NightlyAuto, CancellationToken.None);

        var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "borderline");
        Assert.Equal(1, refreshed.Level); // not promoted

        var d = result.Decisions.First(x => x.AgentId == "borderline");
        Assert.Equal(PromotionPolicy.ReasonPromoteGated, d.Reason);

        // No history row written (no level change occurred).
        Assert.False(await control.AgentLevelHistory.AnyAsync(h => h.AgentId == "borderline"));
    }

    // ── Gate: throughput must be ≥ 5 ───────────────────────────────────

    [Fact]
    public async Task NightlyAuto_Gate_Blocks_Promote_When_Throughput_Below_5()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedAgent(control, db, "evalA", 0);
        // Avg 95 (> 90) but only 4 deliverables (< 5). Easy tier promotes on a
        // 3-row window, so the tier verdict is still promote — the throughput
        // gate is what holds it.
        await SeedAgent(control, db, "thin-evidence", level: 1);
        await SeedDeliverables(control, db, "thin-evidence", count: 4, score: 95);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(PromotionRunOptions.NightlyAuto, CancellationToken.None);

        var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "thin-evidence");
        Assert.Equal(1, refreshed.Level); // not promoted

        var d = result.Decisions.First(x => x.AgentId == "thin-evidence");
        Assert.Equal(PromotionPolicy.ReasonPromoteGated, d.Reason);
        Assert.Equal(4, d.WindowSize);
    }

    // ── Gate passes → auto-promote ─────────────────────────────────────

    [Fact]
    public async Task NightlyAuto_Promotes_When_Avg_Over_90_And_Throughput_5plus()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedAgent(control, db, "evalA", 0);
        await SeedAgent(control, db, "rockstar", level: 1);
        await SeedDeliverables(control, db, "rockstar", count: 5, score: 95);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(PromotionRunOptions.NightlyAuto, CancellationToken.None);

        Assert.True(result.Promoted >= 1);

        var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "rockstar");
        Assert.Equal(2, refreshed.Level);
        Assert.Equal(ScoringConstants.HourlyForLevel(2), refreshed.HourlyUsd);

        var hist = await control.AgentLevelHistory.Where(h => h.AgentId == "rockstar").SingleAsync();
        Assert.Equal(PromotionPolicy.ReasonPromote, hist.Reason);
        Assert.Equal(1, hist.OldLevel);
        Assert.Equal(2, hist.NewLevel);
    }

    // ── Manual run ignores the gate (so the operator path is unchanged) ─

    [Fact]
    public async Task Manual_Run_Ignores_Auto_Gate_And_Promotes_Borderline_Agent()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedAgent(control, db, "evalA", 0);
        // Same avg-89 / 5-deliverable agent the nightly gate blocks above.
        await SeedAgent(control, db, "borderline2", level: 1);
        await SeedDeliverables(control, db, "borderline2", count: 5, score: 89);

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        // Default RunAsync == Manual options (no gate).
        var result = await runner.RunAsync(CancellationToken.None);

        Assert.True(result.Promoted >= 1);
        var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "borderline2");
        Assert.Equal(2, refreshed.Level); // promoted — gate is nightly-only
    }

    // ── Nightly auto is promotion-only — it never demotes ──────────────

    [Fact]
    public async Task NightlyAuto_Does_Not_Demote()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        await SeedAgent(control, db, "evalA", 0);
        await SeedAgent(control, db, "slumping", level: 5);
        await SeedDeliverables(control, db, "slumping", count: 10, score: 70, verdict: "iterate");

        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();
        var result = await runner.RunAsync(PromotionRunOptions.NightlyAuto, CancellationToken.None);

        Assert.Equal(0, result.Demoted);
        var refreshed = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "slumping");
        Assert.Equal(5, refreshed.Level); // unchanged

        var d = result.Decisions.First(x => x.AgentId == "slumping");
        Assert.Equal(PromotionPolicy.ReasonDemoteSkipped, d.Reason);

        // The manual path still demotes the same agent.
        var manual = await runner.RunAsync(PromotionRunOptions.Manual, CancellationToken.None);
        Assert.True(manual.Demoted >= 1);
        var afterManual = await control.Agents.AsNoTracking().FirstAsync(a => a.Id == "slumping");
        Assert.Equal(4, afterManual.Level);
    }

    // ── End-to-end through the scheduler entry point ───────────────────

    [Fact]
    public async Task Scheduler_RunOnce_Promotes_Qualifying_Agent()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedAgent(control, db, "evalA", 0);
            await SeedAgent(control, db, "scheduler-star", level: 3);
            await SeedDeliverables(control, db, "scheduler-star", count: 6, score: 96);
        }

        // The scheduler is registered as a singleton even though its hosted
        // loop is disabled under FORGE_TEST_GUARDRAIL — drive one tick directly.
        var scheduler = f.Services.GetRequiredService<PromotionScheduler>();
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Promoted >= 1);

        using var verify = f.Services.CreateScope();
        var control2 = verify.ServiceProvider.GetRequiredService<ControlDbContext>();
        var refreshed = await control2.Agents.AsNoTracking().FirstAsync(a => a.Id == "scheduler-star");
        Assert.Equal(4, refreshed.Level);
    }

    // ── Delay math fires at 02:00 local, next occurrence ───────────────

    [Theory]
    [InlineData(2026, 6, 9, 8)]   // 01:00 PDT → fires today at 02:00 PDT
    [InlineData(2026, 6, 9, 19)]  // 12:00 PDT → fires tomorrow at 02:00 PDT
    [InlineData(2026, 1, 15, 5)]  // standard time (PST) sanity
    public void ComputeNextRunDelay_Fires_At_Local_2am(int y, int mo, int d, int utcHour)
    {
        var tz = PromotionScheduler.ResolveTimeZone(ScoringConstants.DefaultTimezone);
        var nowUtc = new DateTimeOffset(y, mo, d, utcHour, 0, 0, TimeSpan.Zero);

        var delay = PromotionScheduler.ComputeNextRunDelay(nowUtc, tz, runHourLocal: 2);

        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromHours(24));

        // Whatever timezone resolved on this host, the fire instant must be
        // 02:00 wall-clock local.
        var fireLocal = TimeZoneInfo.ConvertTimeFromUtc((nowUtc + delay).UtcDateTime, tz);
        Assert.Equal(2, fireLocal.Hour);
        Assert.Equal(0, fireLocal.Minute);

        // And it must be the *next* occurrence (today if 02:00 hasn't passed,
        // else tomorrow) — never more than a day out.
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, tz);
        var expectedDay = nowLocal.Hour < 2 ? nowLocal.Date : nowLocal.Date.AddDays(1);
        Assert.Equal(expectedDay, fireLocal.Date);
    }
}
