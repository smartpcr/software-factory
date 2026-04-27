// -----------------------------------------------------------------------
// <copyright file="PromotionPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;

/// <summary>
/// Phase 14.7 — pure promotion / demotion decision. Inputs come from the
/// runner (which reads <c>deliverables</c> and current agent state); the
/// policy itself is timezone-free and DB-free so the rules are unit-testable
/// against arbitrary instants.
/// <para>
/// Inputs: <paramref name="trailingWindow"/> is the agent's last
/// <see cref="ScoringConstants.RollingWindow"/> deliverables, where each
/// row represents the **final** iteration of one work item (the runner is
/// expected to filter <c>iter &gt; 1</c> intermediates to the max-iter row
/// per work item before calling). If fewer than the window size are
/// available the result is <c>insufficient-data</c>.
/// </para>
/// <para>
/// Promotion rule (locked by spec §4):
/// <list type="bullet">
///   <item>rolling avg ≥ <see cref="ScoringConstants.PromoteAvgMin"/></item>
///   <item>zero <c>reject</c> verdicts in the window</item>
///   <item>last promotion ≥ <see cref="ScoringConstants.PromotionCooldownDays"/> days ago</item>
///   <item>current level &lt; <see cref="ScoringConstants.MaxLevel"/></item>
/// </list>
/// Demotion: rolling avg &lt; <see cref="ScoringConstants.DemoteAvgMax"/>
/// AND current level &gt; <see cref="ScoringConstants.MinLevel"/>.
/// Demotion has no cooldown (spec doesn't specify one).
/// </para>
/// </summary>
public static class PromotionPolicy
{
    /// <summary>Outcome of a policy evaluation.</summary>
    public sealed record Decision(
        int OldLevel,
        int NewLevel,
        decimal RollingAvg,
        string Reason,
        int WindowSize,
        bool HasRejectInWindow);

    /// <summary>Reason values emitted into <see cref="AgentLevelHistoryRow.Reason"/>.</summary>
    public const string ReasonPromote          = "promote";
    public const string ReasonDemote           = "demote";
    public const string ReasonNoop             = "noop";
    public const string ReasonInsufficientData = "insufficient-data";

    /// <summary>
    /// Decide whether the agent should be promoted, demoted, or left alone.
    /// </summary>
    public static Decision Evaluate(
        int currentLevel,
        DateTimeOffset? lastPromotionAt,
        IReadOnlyList<DeliverableRow> trailingWindow,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(trailingWindow);

        if (trailingWindow.Count < ScoringConstants.RollingWindow)
        {
            return new Decision(currentLevel, currentLevel, 0m,
                ReasonInsufficientData, trailingWindow.Count, HasRejectInWindow: false);
        }

        // Average score across the trailing window. Use a wide accumulator
        // so a 100×20 window doesn't risk overflow on the int->decimal sum.
        long sum = 0;
        var hasReject = false;
        for (var i = 0; i < trailingWindow.Count; i++)
        {
            sum += trailingWindow[i].Score;
            if (string.Equals(trailingWindow[i].Verdict, "reject", StringComparison.OrdinalIgnoreCase))
            {
                hasReject = true;
            }
        }
        var avg = (decimal)sum / trailingWindow.Count;

        // Promotion path.
        var daysSincePromotion = lastPromotionAt is null
            ? int.MaxValue
            : (int)Math.Floor((utcNow - lastPromotionAt.Value).TotalDays);

        var canPromote = avg >= ScoringConstants.PromoteAvgMin
                         && !hasReject
                         && daysSincePromotion >= ScoringConstants.PromotionCooldownDays
                         && currentLevel < ScoringConstants.MaxLevel;
        if (canPromote)
        {
            return new Decision(currentLevel, currentLevel + 1, avg,
                ReasonPromote, trailingWindow.Count, hasReject);
        }

        // Demotion path.
        var canDemote = avg < ScoringConstants.DemoteAvgMax
                        && currentLevel > ScoringConstants.MinLevel;
        if (canDemote)
        {
            return new Decision(currentLevel, currentLevel - 1, avg,
                ReasonDemote, trailingWindow.Count, hasReject);
        }

        return new Decision(currentLevel, currentLevel, avg,
            ReasonNoop, trailingWindow.Count, hasReject);
    }
}
