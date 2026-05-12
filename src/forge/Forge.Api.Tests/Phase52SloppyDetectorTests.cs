// -----------------------------------------------------------------------
// <copyright file="Phase52SloppyDetectorTests.cs" company="Microsoft Corp.">
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
/// Phase 52 — pure unit tests for <see cref="SloppyDetector"/>. The score
/// plateau check is deterministic and DB-free (in-memory scalar math); the
/// token cap check requires the real ForgeDbContext to query
/// <c>agent_usage</c>.
/// </summary>
public class Phase52SloppyDetectorTests
{
    // ── Score plateau (Q52.1) — pure scalar math, no DB ─────────────

    [Fact]
    public void ScorePlateau_Below_4_Iters_Never_Fires()
    {
        // Phase 52 spec: needs N >= 4 to evaluate the plateau condition.
        // Fewer than 4 iters → not enough history to assert the peak is
        // outside the trailing-3 window.
        for (var n = 1; n <= 3; n++)
        {
            var scores = Enumerable.Repeat(50, n).ToList();
            var (sloppy, _, _) = SloppyDetector.CheckScorePlateau(scores);
            Assert.False(sloppy, $"Should not fire on {n} iters");
        }
    }

    [Fact]
    public void ScorePlateau_Strictly_Improving_Does_Not_Fire()
    {
        // 60 → 72 → 84 → 90: each iter sets a new best. The peak is at
        // iter 4, which is inside the trailing-3 window {2,3,4}. Trigger
        // should NOT fire.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 60, 72, 84, 90 });
        Assert.False(sloppy);
        Assert.Equal(4, bestIter);
        Assert.Equal(90, bestScore);
    }

    [Fact]
    public void ScorePlateau_OneOff_Regression_After_Sustained_Improvement_Does_Not_Fire()
    {
        // 60 → 72 → 84 → 80: peak at iter 3 (inside the trailing-3 window).
        // Trigger should NOT fire — the regression is recent, not a true
        // plateau against personal best.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 60, 72, 84, 80 });
        Assert.False(sloppy);
        Assert.Equal(3, bestIter);
        Assert.Equal(84, bestScore);
    }

    [Fact]
    public void ScorePlateau_Flat_Plateau_Fires_At_Iter_4()
    {
        // 78 → 78 → 78 → 78: argmax tie-break picks the earliest iter.
        // Best = 78 at iter 1, which is at N-3=1 (BEFORE the trailing-3
        // window {2,3,4}). Trigger fires.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 78, 78, 78, 78 });
        Assert.True(sloppy);
        Assert.Equal(1, bestIter);
        Assert.Equal(78, bestScore);
    }

    [Fact]
    public void ScorePlateau_Peak_At_Iter1_Then_Steady_Decline_Fires()
    {
        // 85 → 80 → 75 → 70: peak at iter 1, trailing-3 are {2,3,4}, none
        // exceed best. Fires.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 85, 80, 75, 70 });
        Assert.True(sloppy);
        Assert.Equal(1, bestIter);
        Assert.Equal(85, bestScore);
    }

    [Fact]
    public void ScorePlateau_Fires_At_Iter5_When_Peak_Inside_Window_At_Iter4()
    {
        // 60 → 72 → 84 → 88 → 88: at iter 5, best=88 at iter 4 (inside
        // trailing-3 window {3,4,5}). Does NOT fire. Need a STRICT new
        // best to displace; equal doesn't displace.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 60, 72, 84, 88, 88 });
        Assert.False(sloppy);
        Assert.Equal(4, bestIter);
        Assert.Equal(88, bestScore);
    }

    [Fact]
    public void ScorePlateau_Fires_When_Peak_Just_Drops_Out_Of_Window()
    {
        // 60 → 72 → 88 → 84 → 80: at iter 5, best=88 at iter 3 — exactly
        // at N-3=2... wait, N=5, N-3=2. Iter 3 is AFTER N-3=2, so iter 3
        // is INSIDE the trailing-3 window {3,4,5}. Does NOT fire.
        var (sloppy1, bestIter1, _) = SloppyDetector.CheckScorePlateau(new[] { 60, 72, 88, 84, 80 });
        Assert.False(sloppy1);
        Assert.Equal(3, bestIter1);

        // 88 → 60 → 72 → 84 → 80: peak at iter 1 (= N-4 = 1 < N-3 = 2),
        // outside the trailing-3 window {3,4,5}. Fires.
        var (sloppy2, bestIter2, bestScore2) = SloppyDetector.CheckScorePlateau(new[] { 88, 60, 72, 84, 80 });
        Assert.True(sloppy2);
        Assert.Equal(1, bestIter2);
        Assert.Equal(88, bestScore2);
    }

    [Fact]
    public void ScorePlateau_Argmax_Picks_Earliest_On_Tie()
    {
        // 90 → 80 → 90 → 80 → 80: two iters tied at 90 (iter 1 and iter 3).
        // argmax should pick the EARLIEST → bestIter=1, fires at iter 5
        // because 1 <= N-3=2.
        var (sloppy, bestIter, bestScore) = SloppyDetector.CheckScorePlateau(new[] { 90, 80, 90, 80, 80 });
        Assert.True(sloppy);
        Assert.Equal(1, bestIter);
        Assert.Equal(90, bestScore);
    }

    // ── Token cap (Q52.2) — requires DB ─────────────────────────────

    [Fact]
    public async Task TokenCap_Fires_At_500K_Plus_One_For_Generator()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Seed an agent_usage row carrying 500_001 generator tokens.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-cap",
            CrewId = "crew-x",
            TaskId = "wi-token-cap",
            Model = "claude-sonnet-4-6",
            InputTokens = 400_000,
            OutputTokens = 100_001,
            CostUsd = 0.10m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var (sloppy, total) = await SloppyDetector.CheckTokenCapAsync(
            db, "wi-token-cap", "agent-gen-cap", budgetWindowStartUtc: null, CancellationToken.None);
        Assert.True(sloppy);
        Assert.Equal(500_001, total);
    }

    [Fact]
    public async Task TokenCap_Does_Not_Fire_When_Only_Evaluator_Burns_Tokens()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Evaluator agent burns 600K (above cap); generator burns 100K
        // (below). The check is scoped to AgentId == generatorAgentId, so
        // the evaluator's tokens are correctly ignored.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-eval-bloat", CrewId = "crew-x", TaskId = "wi-eval-bloat",
            Model = "claude-sonnet-4-6",
            InputTokens = 500_000, OutputTokens = 100_000, CostUsd = 0.50m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CostCategory = "evaluator",
        });
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-cool", CrewId = "crew-x", TaskId = "wi-eval-bloat",
            Model = "claude-sonnet-4-6",
            InputTokens = 80_000, OutputTokens = 20_000, CostUsd = 0.05m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var (sloppy, total) = await SloppyDetector.CheckTokenCapAsync(
            db, "wi-eval-bloat", "agent-gen-cool", budgetWindowStartUtc: null, CancellationToken.None);
        Assert.False(sloppy);
        Assert.Equal(100_000, total);
    }

    [Fact]
    public async Task TokenCap_Excludes_Pre_Window_Usage()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Pre-window row: 600K (would trigger if counted).
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-win", CrewId = "crew-x", TaskId = "wi-window",
            Model = "claude-sonnet-4-6",
            InputTokens = 400_000, OutputTokens = 200_000, CostUsd = 0.60m,
            StartedAt = windowStart.AddMinutes(-30), // 40m ago, before window
            CostCategory = "generator",
        });
        // Post-window row: 100K (does NOT trigger).
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-win", CrewId = "crew-x", TaskId = "wi-window",
            Model = "claude-sonnet-4-6",
            InputTokens = 60_000, OutputTokens = 40_000, CostUsd = 0.10m,
            StartedAt = windowStart.AddMinutes(2), // inside window
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var (sloppy, total) = await SloppyDetector.CheckTokenCapAsync(
            db, "wi-window", "agent-gen-win", windowStart, CancellationToken.None);
        Assert.False(sloppy);
        Assert.Equal(100_000, total);
    }

    [Fact]
    public async Task TokenCap_Honors_Constant_From_ScoringConstants()
    {
        // Defensive: assert the cap value the detector reads matches the
        // ScoringConstants literal. If the spec-consistency test moves the
        // constant, this test fails loudly so the detector's threshold
        // doesn't drift silently.
        Assert.Equal(500_000L, ScoringConstants.SloppyGeneratorTokenCap);

        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Exactly at the cap → NOT sloppy (strict >, not >=).
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-edge", CrewId = "crew-x", TaskId = "wi-edge",
            Model = "claude-sonnet-4-6",
            InputTokens = 300_000, OutputTokens = 200_000, CostUsd = 0.10m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var (sloppyAt, totalAt) = await SloppyDetector.CheckTokenCapAsync(
            db, "wi-edge", "agent-gen-edge", budgetWindowStartUtc: null, CancellationToken.None);
        Assert.False(sloppyAt);
        Assert.Equal(500_000, totalAt);
    }

    [Fact]
    public async Task Combined_TokenCap_Wins_Over_Plateau_On_Tie()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Token cap exceeded AND plateau condition holds — token cap
        // should be the reported reason (Q52.2 tie-break).
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-both", CrewId = "crew-x", TaskId = "wi-both",
            Model = "claude-sonnet-4-6",
            InputTokens = 400_000, OutputTokens = 200_000, CostUsd = 0.50m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var result = await SloppyDetector.CheckAsync(
            db, "wi-both", "agent-gen-both", budgetWindowStartUtc: null,
            scoresInOrder: new[] { 78, 78, 78, 78 }, // would also trigger plateau
            CancellationToken.None);
        Assert.True(result.IsSloppy);
        Assert.Equal(SloppyReasons.TokenCap, result.Reason);
        Assert.Equal(600_000, result.TotalTokens);
    }

    [Fact]
    public async Task Combined_Neither_Trigger_Reports_Not_Sloppy()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Light token usage + strictly-improving scores.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen-fine", CrewId = "crew-x", TaskId = "wi-fine",
            Model = "claude-sonnet-4-6",
            InputTokens = 50_000, OutputTokens = 10_000, CostUsd = 0.05m,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var result = await SloppyDetector.CheckAsync(
            db, "wi-fine", "agent-gen-fine", budgetWindowStartUtc: null,
            scoresInOrder: new[] { 60, 72, 84, 92 },
            CancellationToken.None);
        Assert.False(result.IsSloppy);
        Assert.Null(result.Reason);
        Assert.Equal(4, result.BestIter);
        Assert.Equal(92, result.BestScore);
        Assert.Equal(60_000, result.TotalTokens);
    }
}
