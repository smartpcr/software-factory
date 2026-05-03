// -----------------------------------------------------------------------
// <copyright file="ScoringConstants.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api;

/// <summary>
/// Phase 14 — locked rubric, schedule, level, and payroll constants. The
/// values here are the **single source of truth in code**; the spec at
/// <c>docs/forge/scoring-and-promotion.md</c> is the single source of
/// truth in prose. <see cref="Forge.Api.Tests.Phase14SpecConsistencyTests"/>
/// parses the spec and asserts these constants match — bumping a number
/// here without bumping the spec (or vice-versa) fails CI.
/// </summary>
public static class ScoringConstants
{
    // ── Rubric (0..100, weights sum to 100) ─────────────────────────────
    public const int CorrectnessWeight = 40;
    public const int QualityWeight     = 25;
    public const int TestsWeight       = 15;
    public const int ScopeWeight       = 10;
    public const int RiskWeight        = 10;

    public const int RubricTotal = CorrectnessWeight + QualityWeight + TestsWeight + ScopeWeight + RiskWeight;

    // ── Verdict thresholds ──────────────────────────────────────────────
    public const int PassThreshold = 90; // score >= 90 → pass

    /// <summary>
    /// Points-based reject threshold. Higher-point (harder) work items get a
    /// lower bar so the loop iterates more before giving up.
    /// </summary>
    public static int RejectThreshold(int points) => points switch
    {
        <= 1 => 70,
        2    => 65,
        3    => 60,
        4    => 50,
        5    => 40,
        6    => 30,
        7    => 20,
        8    => 15,
        _    => 10, // 9+
    };

    /// <summary>Iterate range: [RejectThreshold .. PassThreshold-1].</summary>
    public static int IterateThreshold(int points) => RejectThreshold(points);

    // ── Iteration loop ──────────────────────────────────────────────────
    public const int MaxIterations = 5;

    // ── Schedule (9-9-6) ────────────────────────────────────────────────
    public const int ShiftStartHour = 9;   // 09:00 local
    public const int ShiftEndHour   = 21;  // 21:00 local
    public const int ShiftHoursPerDay = ShiftEndHour - ShiftStartHour; // 12

    /// <summary>Mon–Sat. Sunday is 0 in <see cref="DayOfWeek"/>.</summary>
    public static readonly DayOfWeek[] WorkDays =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
    };

    public const string DefaultTimezone = "America/Los_Angeles";

    // ── Level + payroll curve ───────────────────────────────────────────
    public const int MinLevel = 0;
    public const int MaxLevel = 10;

    public const decimal HourlyBaseUsd = 4m;
    public const decimal HourlyStepUsd = 3m;

    /// <summary>hourly_usd = base + level * step. Floors at level 0, caps at 10.</summary>
    public static decimal HourlyForLevel(int level)
    {
        if (level < MinLevel) level = MinLevel;
        if (level > MaxLevel) level = MaxLevel;
        return HourlyBaseUsd + level * HourlyStepUsd;
    }

    // ── Promotion job ───────────────────────────────────────────────────
    public const int RollingWindow         = 20;     // last N deliverables
    public const decimal PromoteAvgMin     = 92m;    // rolling avg ≥ 92 → promote
    public const decimal DemoteAvgMax      = 75m;    // rolling avg < 75 → demote
    public const int PromotionCooldownDays = 30;     // min days between promotions

    // ── Lead floor (Q14.5) ──────────────────────────────────────────────
    public const int LeadMinLevel = 7;
}
