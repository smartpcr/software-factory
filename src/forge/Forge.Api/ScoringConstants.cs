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

    /// <summary>
    /// Phase 14.8 — tiered promotion difficulty. Each tier specifies the
    /// rolling window, the rolling-average score required to promote, and
    /// the cooldown between promotions. Tier is selected by the **current**
    /// level of the agent (i.e. the tier you must satisfy to promote OUT of
    /// your current rung). See <see cref="PromotionTierFor(int)"/>.
    /// </summary>
    public sealed record PromotionTier(int Window, decimal PromoteAvgMin, int CooldownDays, string Name);

    /// <summary>
    /// Phase 14.8 — tier lookup. Lower levels promote on a small window
    /// with a forgiving average and short cooldown ("easy start") so new
    /// agents build momentum; mid levels follow the spec default; high
    /// levels (L7+) require sustained excellence over a larger window
    /// with a higher avg and longer cooldown ("harder after L7"). The
    /// spec at <c>docs/forge/scoring-and-promotion.md</c> §4 mirrors this
    /// table and <see cref="Forge.Api.Tests.Phase14SpecConsistencyTests"/>
    /// pins it.
    /// </summary>
    public static PromotionTier PromotionTierFor(int currentLevel) => currentLevel switch
    {
        <= 2 => new PromotionTier(Window: 3,  PromoteAvgMin: 88m, CooldownDays: 7,  Name: "easy"),
        <= 6 => new PromotionTier(Window: 5,  PromoteAvgMin: 92m, CooldownDays: 30, Name: "standard"),
        _    => new PromotionTier(Window: 10, PromoteAvgMin: 94m, CooldownDays: 60, Name: "hard"),
    };

    /// <summary>
    /// Largest tier window across <see cref="PromotionTierFor(int)"/>. Used
    /// by callers (PromotionRunner / PerformanceEndpoints) to load enough
    /// rows up-front so any tier's window can be satisfied from the same
    /// in-memory buffer.
    /// </summary>
    public const int MaxPromotionWindow = 10;

    /// <summary>
    /// Canonical "standard" tier window. Retained as a top-level constant
    /// because much of the codebase (Phase 14 scorecards, the agent
    /// preamble, the original spec) was written before tiers existed.
    /// New code should prefer <see cref="PromotionTierFor(int)"/>.
    /// </summary>
    public const int RollingWindow         = 5;      // standard tier window

    /// <summary>Canonical "standard" tier promote threshold.</summary>
    public const decimal PromoteAvgMin     = 92m;    // standard tier rolling-avg ≥ 92 → promote

    public const decimal DemoteAvgMax      = 75m;    // rolling avg < 75 → demote

    /// <summary>Canonical "standard" tier cooldown (days).</summary>
    public const int PromotionCooldownDays = 30;     // standard tier cooldown

    // ── Phase 91 — nightly auto-promotion safety gate ───────────────────

    /// <summary>
    /// Phase 91 — extra evidence gate the <b>nightly auto-promotion
    /// scheduler</b> (<see cref="Forge.Api.Services.PromotionScheduler"/>)
    /// applies <i>on top of</i> the tier-based <see cref="Services.PromotionPolicy"/>
    /// verdict. An agent is auto-promoted only when, in addition to the policy
    /// returning <see cref="Services.PromotionPolicy.ReasonPromote"/> (tier avg
    /// + zero rejects + cooldown clear), its trailing-window average score is
    /// <b>strictly greater than</b> this value AND it has at least
    /// <see cref="AutoPromoteMinThroughput"/> final deliverables in the window.
    /// <para>
    /// These gates do NOT change the displayed verdict / Promo-ETA on the
    /// Performance scoreboard — they only gate the automatic level mutation so
    /// a thin-evidence easy-tier agent (policy promote at avg 88 over a 3-row
    /// window) is not auto-promoted overnight. Operators can still promote such
    /// an agent manually via the per-agent PO override (Phase 89) or the admin
    /// <c>POST /api/admin/promotion/run</c> endpoint, both of which bypass this
    /// gate.
    /// </para>
    /// <para>
    /// Mirrors the Performance scoreboard columns the operator reads: the
    /// runner's <c>RollingAvg</c> is the trailing-window average (= AVG column)
    /// and <c>WindowSize</c> is the trailing-window final-deliverable count
    /// (= THROUGHPUT column).
    /// </para>
    /// </summary>
    public const decimal AutoPromoteMinAvg = 90m;

    /// <summary>
    /// Phase 91 — minimum trailing-window final-deliverable count (throughput)
    /// for the nightly auto-promotion gate. See <see cref="AutoPromoteMinAvg"/>.
    /// </summary>
    public const int AutoPromoteMinThroughput = 5;

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
    /// Phase 79 — maximum number of rebase+resolve attempts the
    /// orchestrator's <c>TryResolveConflictsAndRetryMergeAsync</c> will
    /// make before stalling the workstream with the merge-conflict
    /// reason. Each attempt = one full rebase cycle (may include
    /// multiple commit-by-commit conflict rounds inside a single
    /// rebase). On the Nth failure the workstream stalls with the
    /// LAST resolver outcome carried as the stall detail.
    /// </summary>
    public const int MaxResolveAttempts = 3;

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

    // ── Phase 61 — consecutive-score-zero sloppy trigger ────────────────

    /// <summary>
    /// Phase 61 — threshold (K) for the consecutive-score-zero sloppy
    /// trigger. When the last <c>K</c> deliverable scores on a single
    /// work item are all <c>0</c>, the deliverable loop flips the verdict
    /// to <c>sloppy</c> with
    /// <see cref="SloppyReasons.ConsecutiveScoreZero"/> and parks the
    /// work item for reassignment. <c>K = 3</c> chosen empirically against
    /// the <c>local:UPDATE-HISTORY-IN-CL:architecture.md</c> stall pattern
    /// (78, 71, 0, 0, 82, 0, 0, 0, 0, 0) — <c>K = 3</c> would have aborted
    /// after iter 8 saving ~$230 vs. the actual halt at iter 10, while
    /// preserving the genuine iter-5 recovery (only 2 consecutive zeros
    /// before that, so the trigger does not fire prematurely).
    ///
    /// <para>Generator-side only: the score reflects the evaluator's grade
    /// of the generator's artifact. Three zeros in a row indicates either
    /// generator terminal failure (Phase 58 synthetic score) or repeated
    /// rubric-bottom output — both are signals to reassign rather than
    /// keep paying for further attempts on the same pair.</para>
    /// </summary>
    public const int SloppyConsecutiveZeroThreshold = 3;

    // ── Phase 90 — flat-score-plateau sloppy trigger ────────────────────

    /// <summary>
    /// Phase 90 — minimum iter count before the flat-plateau trigger can
    /// fire. Tasks legitimately need a few warmup iters to find a
    /// non-zero baseline; we don't want to short-circuit early
    /// exploration. Set to <c>8</c> so the trigger never fires before
    /// iter 8, which still saves ~$200/attempt vs. the 40-iter cap.
    /// </summary>
    /// <remarks>
    /// Empirical reference: <c>local:HTTP-MEMORY:bounded-trace-primitives-
    /// stage-extend-requesttraceexpressions-with-stream-evaluator-e2e</c>
    /// (2026-05-27) — 34 iters at flat score 68 burned ~$200 per attempt
    /// × 3 reassignments = ~$1,080 before iter-cap finally fired. Phase 90
    /// would have caught this at iter 8 (3 iters into the 6-window) ×
    /// 3 attempts ≈ ~$150 vs. the ~$1,080 actually spent.
    /// </remarks>
    public const int SloppyFlatPlateauMinIters = 8;

    /// <summary>
    /// Phase 90 — width (K) of the trailing window that must all hold
    /// IDENTICAL non-zero scores for the flat-plateau trigger to fire.
    /// K=6 is chosen so the signal is unambiguous (six consecutive
    /// identical scores is extremely rare absent score quantization /
    /// rubric collapse) while still triggering early.
    /// </summary>
    /// <remarks>
    /// <para>Compare to the legacy <see cref="SloppyDetector.CheckScorePlateau"/>
    /// (Q52.1, disabled 2026-05-13): that one fires when peak is at
    /// <c>iter &lt;= N-3</c> i.e. only the LAST score must be ≤ peak. The
    /// 2026-05-13 disable rationale was correct for the Q52.1 form (3-iter
    /// window too noisy, fires on benign downgrades). Phase 90's K=6
    /// identical-score window is a strictly narrower signal — far less
    /// false-positive-prone.</para>
    /// <para>Distinct from <see cref="SloppyConsecutiveZeroThreshold"/>
    /// (Phase 61): that handles non-converging zero scores; Phase 90
    /// handles non-zero stuck plateaus. Both fire on the same code path
    /// (<see cref="SloppyReasons"/> + reassignment reconciler).</para>
    /// </remarks>
    public const int SloppyFlatPlateauWindow = 6;

}
