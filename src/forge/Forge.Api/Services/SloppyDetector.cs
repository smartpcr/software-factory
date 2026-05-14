// -----------------------------------------------------------------------
// <copyright file="SloppyDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 52 — pure detection of "sloppy" deliverable patterns. Called by
/// <see cref="DeliverableLoop"/> immediately after persisting an
/// evaluator-scored row; returns the structured sloppy reason (if any) so
/// the loop can rewrite the deliverable verdict to <c>sloppy</c> and park
/// the work item for the reassignment reconciler.
///
/// <para>Two independent triggers — whichever fires first wins. Both are
/// pure SQL + scalar math; the detector is stateless across calls.</para>
/// </summary>
public static class SloppyDetector
{
    /// <summary>Result of one detection pass.</summary>
    /// <param name="IsSloppy">True when at least one trigger fired.</param>
    /// <param name="Reason">
    /// <see cref="SloppyReasons"/> value when sloppy; null otherwise.
    /// Token cap takes precedence over score plateau on tie (the meter is
    /// monotonically increasing; deterministically the earliest-fired
    /// trigger to be true).
    /// </param>
    /// <param name="BestIter">Iter holding the personal-best score; 0 when score-plateau didn't fire.</param>
    /// <param name="BestScore">Personal-best score; 0 when score-plateau didn't fire.</param>
    /// <param name="TotalTokens">Generator's cumulative input+output tokens since the budget window start.</param>
    public sealed record SloppyCheckResult(
        bool IsSloppy,
        string? Reason,
        int BestIter,
        int BestScore,
        long TotalTokens);

    /// <summary>
    /// Q52.1 — score plateau. A deliverable is sloppy on iter N when N >= 4
    /// AND the personal-best score lies at an iter STRICTLY BEFORE the
    /// trailing-3 window — i.e. no new best was reached in the last 3
    /// iterations.
    ///
    /// <para>Tie-breaking: when multiple iters tie for the max score,
    /// <c>argmax</c> picks the EARLIEST iter. A flat plateau {78, 78, 78, 78}
    /// resolves <c>bestIter=1</c> and the trigger fires at iter 4.</para>
    ///
    /// <para>Pure function over the deliverable scores in 1-indexed iter
    /// order. Caller supplies the scores; the helper does not query the DB.</para>
    /// </summary>
    /// <param name="scoresInOrder">
    /// Scores from iter 1 (index 0) through iter N (index N-1). Must be
    /// length &gt;= 1. Less than 4 returns <c>(false, 0, 0)</c>.
    /// </param>
    public static (bool IsSloppy, int BestIter, int BestScore) CheckScorePlateau(
        IReadOnlyList<int> scoresInOrder)
    {
        if (scoresInOrder is null) throw new ArgumentNullException(nameof(scoresInOrder));
        var n = scoresInOrder.Count;
        if (n < 4) return (false, 0, 0);

        // argmax over the whole chain; ties broken by EARLIEST iter (first
        // strict-greater wins; equal does not displace). This is what
        // makes a flat plateau {78, 78, 78, 78} fire at iter 4 — best stays
        // pinned at iter 1, which is at N-3=1 (just barely BEFORE the
        // trailing-3 window {2,3,4}).
        var best = scoresInOrder[0];
        var bestIter = 1; // 1-indexed
        for (var i = 1; i < n; i++)
        {
            if (scoresInOrder[i] > best)
            {
                best = scoresInOrder[i];
                bestIter = i + 1;
            }
        }

        // Trigger when the peak is at iter <= N - 3, i.e. strictly outside
        // the trailing-3 window {N-2, N-1, N}.
        var sloppy = bestIter <= n - 3;
        return (sloppy, bestIter, best);
    }

    /// <summary>
    /// Phase 61 — pure helper. Returns <c>true</c> when the last
    /// <paramref name="threshold"/> scores in
    /// <paramref name="scoresInOrder"/> are ALL <c>0</c>. Operates on the
    /// trailing window only; earlier non-zero scores do not save the loop
    /// once it bottoms out. Returns <c>false</c> when the chain is shorter
    /// than <paramref name="threshold"/> (cannot fire from iter 1 / 2).
    ///
    /// <para>Pure function — caller supplies the scores in 1-indexed iter
    /// order. Score values are clamped 0..points-elsewhere; here we only
    /// care about the boolean "is this zero?" per slot.</para>
    /// </summary>
    /// <param name="scoresInOrder">
    /// Scores from iter 1 (index 0) through iter N (index N-1).
    /// </param>
    /// <param name="threshold">
    /// Number of trailing zeros required to fire. Defaults to
    /// <see cref="ScoringConstants.SloppyConsecutiveZeroThreshold"/> (3).
    /// </param>
    public static bool CheckConsecutiveScoreZero(
        IReadOnlyList<int> scoresInOrder,
        int threshold = -1)
    {
        if (scoresInOrder is null) throw new ArgumentNullException(nameof(scoresInOrder));
        if (threshold < 0) threshold = ScoringConstants.SloppyConsecutiveZeroThreshold;
        if (threshold <= 0) return false;
        var n = scoresInOrder.Count;
        if (n < threshold) return false;
        for (var i = n - threshold; i < n; i++)
        {
            if (scoresInOrder[i] != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Q52.2 — generator token cap. Sums
    /// <c>agent_usage.InputTokens + OutputTokens</c> for the generator
    /// agent's invocations against this work item since
    /// <paramref name="budgetWindowStartUtc"/>. When the sum exceeds
    /// <see cref="ScoringConstants.SloppyGeneratorTokenCap"/>, returns true.
    ///
    /// <para>Generator-only (Q52.2): the evaluator's tokens are bounded by
    /// rubric size and don't bloat. Pre-window usage is excluded so a
    /// reassignment (which resets the window) grants a fresh meter to the
    /// new pair.</para>
    /// </summary>
    public static async Task<(bool IsSloppy, long TotalTokens)> CheckTokenCapAsync(
        ForgeDbContext db,
        string workItemId,
        string generatorAgentId,
        DateTimeOffset? budgetWindowStartUtc,
        CancellationToken ct)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrEmpty(workItemId)) return (false, 0);
        if (string.IsNullOrEmpty(generatorAgentId)) return (false, 0);

        // Filter materialization to the (taskId, agentId) tuple; the
        // existing AgentUsage index covers AgentId-scoped scans. The
        // generator-side filter also keeps the row count tight even on
        // long chains.
        var rows = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == workItemId && u.AgentId == generatorAgentId)
            .Select(u => new { u.StartedAt, u.InputTokens, u.OutputTokens })
            .ToListAsync(ct);

        long sum = 0;
        foreach (var r in rows)
        {
            if (budgetWindowStartUtc is DateTimeOffset ws && r.StartedAt < ws) continue;
            sum += r.InputTokens + r.OutputTokens;
        }

        return (sum > ScoringConstants.SloppyGeneratorTokenCap, sum);
    }

    /// <summary>
    /// Combined check. As of 2026-05-13, the original Phase 52 sloppy
    /// triggers (Q52.1 score-plateau and Q52.2 generator-token cap) are
    /// DISABLED at the operator's direction — the cumulative-token meter
    /// doesn't reflect actual context size (the protocol re-reads context
    /// each turn, so cumulative input balloons naturally on long tasks
    /// without indicating runaway behavior), and score-plateau adds little
    /// signal once the legacy 5-iter cap and Phase 51 dollar cap are in
    /// play.
    /// <para>
    /// Phase 61 (2026-05-14) re-enabled a NARROWER trigger:
    /// <see cref="CheckConsecutiveScoreZero"/>. When the last
    /// <see cref="ScoringConstants.SloppyConsecutiveZeroThreshold"/>
    /// deliverable scores are all 0, the deliverable is sloppy with
    /// <see cref="SloppyReasons.ConsecutiveScoreZero"/>. This catches the
    /// "generator can't converge under its turn/context budget"
    /// pattern (Phase 58 synthesized score=0 from terminal SDK failure)
    /// without the false positives of the cumulative-token meter, and
    /// aborts the loop early instead of paying for the full 5-iter / 10-
    /// iter cap chain.
    /// </para>
    /// <para>
    /// The helper methods <see cref="CheckTokenCapAsync"/> and
    /// <see cref="CheckScorePlateau"/> remain available — the UI and debug
    /// endpoints still consume them to display "tokens used / cap" ratios
    /// and "personal best" stats. The first two triggers deliberately
    /// report <c>IsSloppy=false</c> regardless. The remaining stall paths
    /// also still apply:
    /// </para>
    /// <list type="bullet">
    ///   <item>Legacy 5-iter cap (<see cref="DeliverableLoop"/> rewrites
    ///   the last deliverable's verdict to <c>sloppy</c> with
    ///   <see cref="SloppyReasons.IterCap"/> when iter exhaustion is
    ///   reached without a pass).</item>
    ///   <item>Phase 51 dollar cap (<c>crew.MaxTaskBudgetUsd</c>) emits
    ///   <c>stalled-budget</c> with auto-resume on cap raise / cooldown
    ///   elapse — the actual economic guardrail.</item>
    ///   <item><see cref="NoProgressException"/>, build-gate failure, and
    ///   evaluator <c>reject</c> verdicts all still mark the workstream
    ///   stalled for operator triage.</item>
    /// </list>
    /// </summary>
    public static async Task<SloppyCheckResult> CheckAsync(
        ForgeDbContext db,
        string workItemId,
        string generatorAgentId,
        DateTimeOffset? budgetWindowStartUtc,
        IReadOnlyList<int> scoresInOrder,
        CancellationToken ct)
    {
        // Still compute the meters so the result carries display data for
        // any caller that wants the numbers (UI score panel, debug
        // endpoints) — but never flip IsSloppy from Q52.1 / Q52.2.
        var (_, totalTokens) =
            await CheckTokenCapAsync(db, workItemId, generatorAgentId, budgetWindowStartUtc, ct);
        var (_, bestIter, bestScore) = CheckScorePlateau(scoresInOrder);

        // Phase 61 — only ACTIVE trigger today. Fires on K trailing zeros.
        if (CheckConsecutiveScoreZero(scoresInOrder))
        {
            return new SloppyCheckResult(
                IsSloppy: true,
                Reason: SloppyReasons.ConsecutiveScoreZero,
                BestIter: bestIter,
                BestScore: bestScore,
                TotalTokens: totalTokens);
        }

        return new SloppyCheckResult(false, null, bestIter, bestScore, totalTokens);
    }
}
