// -----------------------------------------------------------------------
// <copyright file="SloppyReassignmentOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 52 — config for the sloppy-iteration reassignment reconciler.
///
/// Bound from <c>Forge:Agents:SloppyReassignment:*</c>.
///
/// <para>When <see cref="DeliverableLoop"/> detects a sloppy verdict
/// (score plateau against personal best, or generator token cap) it parks
/// the work item at <c>ExecutionStatus="pending-reassign"</c> with the
/// active <c>agent_pair_assignment</c> row stamped
/// <c>outcome="reassigned-sloppy"</c>. <see cref="SloppyReassignmentReconciler"/>
/// ticks every <see cref="TickInterval"/> and either:</para>
/// <list type="bullet">
///   <item>Picks a fresh (generator, evaluator) pair via
///   <c>PairDiversityResolver</c> with all prior pairs excluded, then
///   wipes transcript / deliverables / budget window / worktree branch
///   and requeues — Q52.4 full clean-slate semantics.</item>
///   <item>When <c>WorkItemRow.PairAttemptCount &gt;=
///   <see cref="ScoringConstants.MaxPairAttempts"/></c> already, stamps
///   <c>ExecutionStatus=failed</c> with the locked terminal reason and
///   closes the active pair-attempt row as <c>terminal-failed</c>.
///   No fourth pair is attempted.</item>
/// </list>
/// </summary>
public sealed class SloppyReassignmentOptions
{
    public const string SectionName = "Forge:Agents:SloppyReassignment";

    /// <summary>
    /// How often the reconciler scans for <c>pending-reassign</c> rows.
    /// 15s by default — parity with <see cref="BudgetCooldownOptions.TickInterval"/>.
    /// Tests drop to milliseconds and drive <c>RunOnceAsync</c>
    /// deterministically.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When false, the reconciler is registered but does not run on its
    /// own timer. Tests set this to false and call <c>RunOnceAsync</c>
    /// directly.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When the resolver returns null (every eligible pair is busy or
    /// excluded) the reconciler leaves the row in <c>pending-reassign</c>
    /// and retries next tick. After this many consecutive no-pick ticks
    /// for the same row, the reconciler emits a single
    /// <c>task.sloppy-blocked</c> event so the operator knows the roster
    /// needs growing. Counted in memory; resets across reconciler restart
    /// (which is fine — the operator's signal threshold is approximate).
    /// </summary>
    public int MaxNoPickTicks { get; set; } = 10;

    /// <summary>
    /// When true, the reconciler resets the worktree branch to the
    /// repo's default base on reassignment (<c>git reset --hard</c>
    /// + <c>git clean -fdx</c>). Q52.4 default behavior. Tests can
    /// disable this to avoid touching real filesystems.
    /// </summary>
    public bool ResetWorktreeOnReassign { get; set; } = true;
}
