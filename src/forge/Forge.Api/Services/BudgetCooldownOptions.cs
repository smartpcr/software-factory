// -----------------------------------------------------------------------
// <copyright file="BudgetCooldownOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 51 — config for the per-task budget-cap auto-recovery.
///
/// Bound from <c>Forge:Agents:BudgetCooldown:*</c>.
///
/// When a deliverable loop hits <c>MaxTaskBudgetUsd</c> it returns
/// <c>verdict="stalled-budget"</c> and <see cref="CrewQueueWorker"/>
/// parks the work item at <c>ExecutionStatus="cooldown-budget"</c> with
/// <c>BudgetCooldownUntilUtc = UtcNow + MaxWait</c>.
/// <see cref="BudgetCooldownReconciler"/> ticks every
/// <see cref="TickInterval"/> and resumes the work item under one of
/// two conditions:
/// <list type="bullet">
///   <item><b>Cap raised</b> — operator increased <c>MaxTaskBudgetUsd</c>
///   on the crew so cumulative cost is now under cap. Resume immediately
///   on the next tick, keep the same pair (cap was the bottleneck, not
///   the agents).</item>
///   <item><b>Cooldown elapsed</b> — <c>BudgetCooldownUntilUtc</c>
///   passed without a cap raise. Resume with the pair rotated away
///   from <c>WorkItemRow.LastPairAgentIdsJson</c> so a fresh pair gets
///   a turn.</item>
/// </list>
/// </summary>
public sealed class BudgetCooldownOptions
{
    public const string SectionName = "Forge:Agents:BudgetCooldown";

    /// <summary>
    /// How often the reconciler scans for cooldown rows. 15s by default —
    /// the SLA the operator sees after raising a cap. Set to a small value
    /// (e.g. 250 ms) in tests to keep the suite fast.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Max wait before the auto-recovery rotates the pair and forces a
    /// resume regardless of cap. 10m by default — matches the operator
    /// expectation that "budget cooldown is short, not a dead end".
    /// </summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When false, the reconciler is registered but does not run. Useful
    /// for tests that want to drive the cooldown timeline manually via
    /// the <c>RunOnceAsync</c> entry point.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
