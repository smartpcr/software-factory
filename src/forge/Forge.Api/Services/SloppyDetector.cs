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
    /// Combined check. Token cap takes precedence on tie (Q52.2 rationale —
    /// the meter is deterministic; score plateau requires multiple
    /// iterations to stabilise).
    /// </summary>
    public static async Task<SloppyCheckResult> CheckAsync(
        ForgeDbContext db,
        string workItemId,
        string generatorAgentId,
        DateTimeOffset? budgetWindowStartUtc,
        IReadOnlyList<int> scoresInOrder,
        CancellationToken ct)
    {
        var (tokenSloppy, totalTokens) =
            await CheckTokenCapAsync(db, workItemId, generatorAgentId, budgetWindowStartUtc, ct);
        if (tokenSloppy)
        {
            return new SloppyCheckResult(true, SloppyReasons.TokenCap, 0, 0, totalTokens);
        }

        var (plateauSloppy, bestIter, bestScore) = CheckScorePlateau(scoresInOrder);
        if (plateauSloppy)
        {
            return new SloppyCheckResult(true, SloppyReasons.ScorePlateau, bestIter, bestScore, totalTokens);
        }

        return new SloppyCheckResult(false, null, bestIter, bestScore, totalTokens);
    }
}
