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
    /// <summary>
    /// Floor for the iteration cap — used when a work item has no point
    /// estimate (defensive default) and as the multiplier for
    /// <see cref="MaxIterationsForPoints"/>. Kept named <c>MaxIterations</c>
    /// for source-compatibility with older call sites; new code should
    /// prefer <see cref="MaxIterationsForPoints"/>.
    /// </summary>
    public const int MaxIterations = 5;

    /// <summary>Hard ceiling so a runaway 100-point story can't spawn a 500-iteration loop.</summary>
    public const int MaxIterationsHardCap = 50;

    /// <summary>
    /// Iteration cap derived from work-item points. The contract is "5 iterations
    /// per point": a 1-point item gets 5 rounds, a 3-point item 15, etc. Items
    /// with 0 / unknown points fall back to <see cref="MaxIterations"/> so the
    /// loop still runs. Capped at <see cref="MaxIterationsHardCap"/>.
    /// </summary>
    public static int MaxIterationsForPoints(int points)
    {
        var floor = points <= 0 ? 1 : points;
        var raw = floor * MaxIterations;
        return raw > MaxIterationsHardCap ? MaxIterationsHardCap : raw;
    }

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

    // ── Phase 52 — sloppy-iteration guard ───────────────────────────────

    /// <summary>
    /// Q52.2 — generator-side cumulative input+output token cap. When the
    /// generator agent's tokens since <c>WorkItemRow.BudgetWindowStartUtc</c>
    /// exceed this number, the deliverable is marked sloppy and the work
    /// item is parked for reassignment. The evaluator's tokens are bounded
    /// by rubric size and don't bloat, so we only meter the generator side.
    /// </summary>
    public const long SloppyGeneratorTokenCap = 500_000;

    /// <summary>
    /// Q52.5 — maximum number of pair-attempts allowed on a single work item
    /// before terminal failure. Original pair = attempt 1; first reassignment
    /// = attempt 2; second reassignment = attempt 3. After the third attempt
    /// is judged sloppy, the reconciler does NOT pick a fourth pair — it
    /// stamps <see cref="ExecutionStatuses.Failed"/> with
    /// <see cref="SloppyTerminalFailureReason"/>.
    /// </summary>
    public const int MaxPairAttempts = 3;

    /// <summary>
    /// Q52.5 — locked terminal-failure reason for the sloppy-3-attempts path.
    /// Lifted verbatim from the operator brief; the
    /// <c>Phase14SpecConsistencyTests</c> assertion catches any drift.
    /// </summary>
    public const string SloppyTerminalFailureReason =
        "Failed after 3 reassignments, didn't advance, consider refactor and break down the scope";

    /// <summary>
    /// Phase 14.7 / Q52.7 — demotion-risk threshold for the new
    /// <c>SloppyRate</c> KPI. An agent whose share of sloppy-class
    /// pair-attempts exceeds this fraction in the rolling window is flagged
    /// at demotion risk even when their rolling-avg score is above the floor
    /// (symmetric with the existing <c>RejectRate &gt; 0.10</c> rule).
    /// </summary>
    public const double SloppyRiskRate = 0.15;

    // ── Phase 54 — per-turn prompt-size growth guard ────────────────────

    /// <summary>
    /// Phase 54 — input-token growth threshold (per iteration, per role)
    /// that triggers a <c>task.prompt-bloat</c> warning event. Under the
    /// Phase 54 "1 invocation = 1 turn" definition, growth is computed as
    /// <c>inputTokens[iter N] - inputTokens[iter N-1]</c> for the same role
    /// (generator / evaluator) on the same work item. When the delta
    /// exceeds this cap the loop emits an amber warning so the operator
    /// can spot context-bloat before the SloppyDetector's 500K cap fires.
    /// Bloat detection is purely advisory — it does not abort the loop.
    /// </summary>
    public const long PromptBloatGrowthThreshold = 200_000;

}
