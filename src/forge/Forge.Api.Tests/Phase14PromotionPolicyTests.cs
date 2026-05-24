// -----------------------------------------------------------------------
// <copyright file="Phase14PromotionPolicyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 14.7 — pure-function tests for <see cref="PromotionPolicy"/>.
/// Drives <c>Evaluate</c> with synthetic deliverable rows so the rules
/// (avg ≥ 92, no rejects, 30-day cooldown, demote &lt; 75, level floor/cap)
/// are pinned independent of the runner / DB.
/// </summary>
public class Phase14PromotionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DeliverableRow> Window(int n, int eachScore, string verdict = "pass")
    {
        var rows = new List<DeliverableRow>(n);
        for (var i = 0; i < n; i++)
        {
            rows.Add(new DeliverableRow
            {
                Id = i + 1, Iter = 1, Score = eachScore, Verdict = verdict,
                CreatedAt = Now.AddDays(-i),
            });
        }
        return rows;
    }

    [Theory]
    [InlineData(0, 2)]  // easy tier (window 3) — 2 rows is below
    [InlineData(2, 2)]  // easy tier boundary
    [InlineData(3, 4)]  // standard tier (window 5) — 4 rows is below
    [InlineData(6, 4)]  // standard tier boundary
    [InlineData(7, 9)]  // hard tier (window 10) — 9 rows is below
    [InlineData(9, 9)]  // hard tier boundary
    public void InsufficientData_When_Window_Below_TierWindow(int level, int rowCount)
    {
        var d = PromotionPolicy.Evaluate(level, lastPromotionAt: null, Window(rowCount, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonInsufficientData, d.Reason);
        Assert.Equal(level, d.NewLevel); // unchanged
    }

    [Fact]
    public void EasyTier_Promotes_With_3_Rows_At_88()
    {
        // L0 → easy tier (window 3, avg ≥ 88, cooldown 7d).
        var d = PromotionPolicy.Evaluate(0, lastPromotionAt: null, Window(3, 88), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, d.Reason);
        Assert.Equal(1, d.NewLevel);
        Assert.Equal(88m, d.RollingAvg);
    }

    [Fact]
    public void EasyTier_Holds_Below_88()
    {
        // L1 → easy tier; 87 is below the 88 threshold.
        var d = PromotionPolicy.Evaluate(1, lastPromotionAt: null, Window(3, 87), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.Equal(1, d.NewLevel);
    }

    [Fact]
    public void EasyTier_7Day_Cooldown_Applies()
    {
        // L0 → easy tier 7-day cooldown. 6 days ago = blocked, 7 days = ok.
        var blocked = PromotionPolicy.Evaluate(0, Now.AddDays(-6), Window(3, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, blocked.Reason);

        var ok = PromotionPolicy.Evaluate(0, Now.AddDays(-7), Window(3, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, ok.Reason);
    }

    [Fact]
    public void HardTier_Requires_10_Rows_At_94()
    {
        // L7 → hard tier (window 10, avg ≥ 94, cooldown 60d).
        var win9 = PromotionPolicy.Evaluate(7, lastPromotionAt: null, Window(9, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonInsufficientData, win9.Reason);

        var avg93 = PromotionPolicy.Evaluate(7, lastPromotionAt: null, Window(10, 93), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, avg93.Reason); // 93 < 94

        var promote = PromotionPolicy.Evaluate(7, lastPromotionAt: null, Window(10, 94), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, promote.Reason);
        Assert.Equal(8, promote.NewLevel);
    }

    [Fact]
    public void HardTier_60Day_Cooldown_Applies()
    {
        // L8 → hard tier 60-day cooldown. 30 days ago = blocked, 60 days = ok.
        var blocked = PromotionPolicy.Evaluate(8, Now.AddDays(-30), Window(10, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, blocked.Reason);

        var ok = PromotionPolicy.Evaluate(8, Now.AddDays(-60), Window(10, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, ok.Reason);
    }

    [Fact]
    public void Promote_At_92_Average_With_No_Reject()
    {
        var d = PromotionPolicy.Evaluate(3, lastPromotionAt: null, Window(20, 92), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, d.Reason);
        Assert.Equal(4, d.NewLevel);
        Assert.Equal(92m, d.RollingAvg);
    }

    [Fact]
    public void NoPromote_When_Average_Below_92()
    {
        var d = PromotionPolicy.Evaluate(3, lastPromotionAt: null, Window(20, 91), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.Equal(3, d.NewLevel);
    }

    [Fact]
    public void NoPromote_When_Window_Has_Reject()
    {
        // 19 × 95 + 1 × 95-reject. Avg = 95 (still ≥ 92), but a reject blocks.
        var rows = new List<DeliverableRow>();
        for (var i = 0; i < 19; i++)
        {
            rows.Add(new DeliverableRow { Id = i, Score = 95, Verdict = "pass", CreatedAt = Now.AddDays(-i) });
        }
        rows.Add(new DeliverableRow { Id = 99, Score = 95, Verdict = "reject", CreatedAt = Now.AddDays(-20) });

        var d = PromotionPolicy.Evaluate(3, lastPromotionAt: null, rows, Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.True(d.HasRejectInWindow);
    }

    [Fact]
    public void NoPromote_When_Cooldown_Not_Elapsed()
    {
        var lastPromo = Now.AddDays(-20); // < 30 days ago.
        var d = PromotionPolicy.Evaluate(3, lastPromo, Window(20, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
    }

    [Fact]
    public void Promote_When_Cooldown_Elapsed_Exactly()
    {
        // Spec: "≥ 30 calendar days". Boundary case.
        var lastPromo = Now.AddDays(-30);
        var d = PromotionPolicy.Evaluate(3, lastPromo, Window(20, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonPromote, d.Reason);
    }

    [Fact]
    public void NoPromote_At_Cap()
    {
        var d = PromotionPolicy.Evaluate(ScoringConstants.MaxLevel, null, Window(20, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.Equal(ScoringConstants.MaxLevel, d.NewLevel);
    }

    [Fact]
    public void Demote_When_Average_Below_75()
    {
        var d = PromotionPolicy.Evaluate(5, null, Window(20, 70, verdict: "iterate"), Now);
        Assert.Equal(PromotionPolicy.ReasonDemote, d.Reason);
        Assert.Equal(4, d.NewLevel);
    }

    [Fact]
    public void NoDemote_At_Floor()
    {
        var d = PromotionPolicy.Evaluate(0, null, Window(20, 50), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.Equal(0, d.NewLevel);
    }

    [Fact]
    public void NoDemote_At_75_Boundary()
    {
        // Spec: avg < 75 demotes. 75 exactly should NOT demote.
        var d = PromotionPolicy.Evaluate(5, null, Window(20, 75), Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
    }

    [Fact]
    public void Mixed_Window_Computes_True_Average()
    {
        // 10 × 100 + 10 × 80 = avg 90. No promotion (need ≥ 92), no demotion.
        var rows = new List<DeliverableRow>();
        for (var i = 0; i < 10; i++) rows.Add(new DeliverableRow { Id = i, Score = 100, Verdict = "pass", CreatedAt = Now.AddDays(-i) });
        for (var i = 0; i < 10; i++) rows.Add(new DeliverableRow { Id = i + 10, Score = 80, Verdict = "iterate", CreatedAt = Now.AddDays(-i - 10) });

        var d = PromotionPolicy.Evaluate(5, null, rows, Now);
        Assert.Equal(PromotionPolicy.ReasonNoop, d.Reason);
        Assert.Equal(90m, d.RollingAvg);
    }
}
