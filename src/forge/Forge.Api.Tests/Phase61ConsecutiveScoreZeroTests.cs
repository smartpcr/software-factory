// -----------------------------------------------------------------------
// <copyright file="Phase61ConsecutiveScoreZeroTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 61 — consecutive-score-zero sloppy trigger. Re-enables a NARROW
/// path in <see cref="SloppyDetector.CheckAsync"/> that flips
/// <c>IsSloppy=true</c> with reason
/// <see cref="SloppyReasons.ConsecutiveScoreZero"/> when the last
/// <see cref="ScoringConstants.SloppyConsecutiveZeroThreshold"/> scores are
/// all 0. This catches the Phase 58 "synthetic score=0 on terminal
/// generator failure" loop earlier than the legacy 5-iter cap so the loop
/// doesn't keep paying $100+/iter on a model that can't converge.
/// </summary>
public class Phase61ConsecutiveScoreZeroTests
{
    // ── Pure helper: CheckConsecutiveScoreZero ──────────────────────────

    [Fact]
    public void Empty_Score_List_DoesNotFire()
    {
        var fired = SloppyDetector.CheckConsecutiveScoreZero(new List<int>());
        Assert.False(fired);
    }

    [Fact]
    public void Below_Threshold_Length_DoesNotFire()
    {
        // K=3 default. A 2-element [0,0] is below threshold.
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0, 0 }));
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0 }));
    }

    [Fact]
    public void Exactly_K_Trailing_Zeros_Fires()
    {
        // 3 trailing zeros, no preceding scores.
        Assert.True(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0, 0, 0 }));
    }

    [Fact]
    public void K_Trailing_Zeros_After_NonZero_Fires()
    {
        // The architect's actual stall pattern after iter 5 recovery:
        // 78, 71, 0, 0, 82, 0, 0, 0 → K=3 fires at index 7 (iter 8).
        Assert.True(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 71, 0, 0, 82, 0, 0, 0 }));
    }

    [Fact]
    public void Recovery_NonZero_Within_Trailing_Window_DoesNotFire()
    {
        // The architect's iter-5 success guarded against false positive:
        // 78, 71, 0, 0, 82 → trailing 3 = [0, 0, 82]. Last is 82, not 0.
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 71, 0, 0, 82 }));
    }

    [Fact]
    public void Two_Trailing_Zeros_DoesNotFire_With_K_Default()
    {
        // 78, 71, 0, 0 → trailing 3 = [71, 0, 0]. 71 ≠ 0, so does not fire.
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 71, 0, 0 }));
    }

    [Fact]
    public void Threshold_Override_Fires_Earlier()
    {
        // K=2 fires after 2 trailing zeros.
        Assert.True(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 0, 0 }, threshold: 2));
        // But still not on a single trailing zero.
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 71, 0 }, threshold: 2));
    }

    [Fact]
    public void Threshold_Zero_DoesNotFire()
    {
        // Defensive: K=0 → never fire (would otherwise vacuously match an empty trailing window).
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 78, 71, 50 }, threshold: 0));
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0, 0, 0 }, threshold: 0));
    }

    [Fact]
    public void Negative_Threshold_Falls_Back_To_Default()
    {
        // Negative sentinel ⇒ ScoringConstants.SloppyConsecutiveZeroThreshold = 3.
        Assert.True(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0, 0, 0 }, threshold: -1));
        Assert.False(SloppyDetector.CheckConsecutiveScoreZero(new[] { 0, 0 }, threshold: -1));
    }

    [Fact]
    public void ScoringConstants_Threshold_Has_Expected_Value()
    {
        // Locked at 3 per the architecture.md stall analysis. Bumping
        // requires reconfirming the false-positive guard above.
        Assert.Equal(3, ScoringConstants.SloppyConsecutiveZeroThreshold);
    }

    // ── End-to-end: CheckAsync flips IsSloppy ──────────────────────────

    [Fact]
    public async Task CheckAsync_Trailing_Zeros_Returns_Sloppy_True_With_Phase61_Reason()
    {
        await using var db = await CreateDbAsync();
        var scores = new[] { 78, 71, 0, 0, 82, 0, 0, 0 };

        var result = await SloppyDetector.CheckAsync(
            db,
            workItemId: "wi-phase61",
            generatorAgentId: "A2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.True(result.IsSloppy);
        Assert.Equal(SloppyReasons.ConsecutiveScoreZero, result.Reason);
        // Score-plateau bookkeeping still populated as display data.
        Assert.Equal(82, result.BestScore);
        Assert.Equal(5, result.BestIter);
    }

    [Fact]
    public async Task CheckAsync_Recovery_Iteration_Does_Not_Fire()
    {
        // Exact pattern from local:UPDATE-HISTORY-IN-CL through iter 5.
        // Trailing 3 = [0, 0, 82] — must NOT fire so iter-5 success has a
        // chance to land before the trigger catches the post-recovery
        // backslide.
        await using var db = await CreateDbAsync();
        var scores = new[] { 78, 71, 0, 0, 82 };

        var result = await SloppyDetector.CheckAsync(
            db,
            workItemId: "wi-phase61",
            generatorAgentId: "A2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task CheckAsync_Token_Cap_Still_Disabled()
    {
        // Q52.2 token-cap path remains explicitly off — high token totals
        // alone should NOT flip the verdict. We seed a generator usage
        // row well above the 500K cap with non-stalling scores.
        await using var db = await CreateDbAsync();
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "A2",
            CrewId = "crew-arch",
            TaskId = "wi-phase61",
            Model = "test",
            InputTokens = 1_000_000,
            OutputTokens = 500_000,
            CostUsd = 50m,
            StartedAt = System.DateTimeOffset.UtcNow.AddMinutes(-10),
            Status = "finished",
            CostCategory = "generator",
        });
        await db.SaveChangesAsync();

        var result = await SloppyDetector.CheckAsync(
            db,
            workItemId: "wi-phase61",
            generatorAgentId: "A2",
            budgetWindowStartUtc: null,
            scoresInOrder: new[] { 80, 75, 70 },
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        Assert.True(result.TotalTokens >= 1_500_000);
    }

    [Fact]
    public async Task CheckAsync_Score_Plateau_Still_Disabled()
    {
        // Q52.1 plateau remains off — a flat trailing-3 below personal
        // best should NOT fire on its own (only consecutive zeros do).
        await using var db = await CreateDbAsync();
        var scores = new[] { 90, 80, 70, 70 }; // best=90 at iter 1, trailing-3 < best.

        var result = await SloppyDetector.CheckAsync(
            db,
            workItemId: "wi-phase61",
            generatorAgentId: "A2",
            budgetWindowStartUtc: null,
            scoresInOrder: scores,
            CancellationToken.None);

        Assert.False(result.IsSloppy);
        // But the plateau bookkeeping is still reported for the UI.
        Assert.Equal(90, result.BestScore);
        Assert.Equal(1, result.BestIter);
    }

    [Fact]
    public async Task SloppyReason_Constant_Has_Stable_String_Value()
    {
        // Persisted in agent_pair_assignment.SloppyReason — the literal
        // must not drift or operator dashboards will silently fail to
        // group the new sloppy bucket alongside token-cap / score-plateau.
        Assert.Equal("consecutive-score-zero", SloppyReasons.ConsecutiveScoreZero);
        // Sanity-check the unchanged sibling values too.
        Assert.Equal("score-plateau", SloppyReasons.ScorePlateau);
        Assert.Equal("token-cap", SloppyReasons.TokenCap);
        Assert.Equal("iter-cap", SloppyReasons.IterCap);
        await Task.CompletedTask;
    }

    // ── Shared SQLite-in-memory fixture (matches pattern from existing
    // SloppyDetectorTests / Phase52SloppyDetectorTests). ────────────────

    private static async Task<ForgeDbContext> CreateDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ForgeDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
