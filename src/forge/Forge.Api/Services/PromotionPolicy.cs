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
/// Inputs: <paramref name="trailingWindow"/> is the agent's trailing
/// deliverables, where each row represents the **final** iteration of one
/// work item (the runner is expected to filter <c>iter &gt; 1</c>
/// intermediates to the max-iter row per work item before calling). The
/// window's minimum size is the agent's tier's window (see
/// <see cref="ScoringConstants.PromotionTierFor(int)"/>) — if fewer than
/// that are available the result is <c>insufficient-data</c>.
/// </para>
/// <para>
/// Phase 14.8 — tiered promotion. The window, avg threshold, and cooldown
/// vary by current level (easy at L0-L2, standard at L3-L6, hard at L7+).
/// Demotion uses the same tier window for the data-sufficiency gate but a
/// uniform avg cutoff (<see cref="ScoringConstants.DemoteAvgMax"/>) and no
/// cooldown.
/// </para>
/// <para>
/// Promotion rule:
/// <list type="bullet">
///   <item>rolling avg ≥ <c>tier.PromoteAvgMin</c></item>
///   <item>zero <c>reject</c> verdicts in the window</item>
///   <item>last promotion ≥ <c>tier.CooldownDays</c> days ago</item>
///   <item>current level &lt; <see cref="ScoringConstants.MaxLevel"/></item>
/// </list>
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

        var tier = ScoringConstants.PromotionTierFor(currentLevel);

        if (trailingWindow.Count < tier.Window)
        {
            return new Decision(currentLevel, currentLevel, 0m,
                ReasonInsufficientData, trailingWindow.Count, HasRejectInWindow: false);
        }

        // Average score across the trailing window. Use a wide accumulator
        // so a 100×N window doesn't risk overflow on the int->decimal sum.
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

        // Promotion path — tier-driven thresholds.
        var daysSincePromotion = lastPromotionAt is null
            ? int.MaxValue
            : (int)Math.Floor((utcNow - lastPromotionAt.Value).TotalDays);

        var canPromote = avg >= tier.PromoteAvgMin
                         && !hasReject
                         && daysSincePromotion >= tier.CooldownDays
                         && currentLevel < ScoringConstants.MaxLevel;
        if (canPromote)
        {
            return new Decision(currentLevel, currentLevel + 1, avg,
                ReasonPromote, trailingWindow.Count, hasReject);
        }

        // Demotion path — uniform avg cutoff.
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
