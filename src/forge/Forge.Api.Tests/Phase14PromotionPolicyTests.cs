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

    [Fact]
    public void InsufficientData_When_Window_Below_20()
    {
        var d = PromotionPolicy.Evaluate(2, lastPromotionAt: null, Window(15, 95), Now);
        Assert.Equal(PromotionPolicy.ReasonInsufficientData, d.Reason);
        Assert.Equal(2, d.NewLevel); // unchanged
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
