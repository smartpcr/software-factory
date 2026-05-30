// -----------------------------------------------------------------------
// <copyright file="WorkItemExecutionStatusInspector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Computes the *authoritative* <see cref="WorkItemRow.ExecutionStatus"/>
/// for a work item by inspecting the live agent runner, the linked
/// <see cref="WorkstreamRow"/>, open <see cref="TaskQueueRow"/>s, and the
/// latest <see cref="DeliverableRow"/>. The stored field can drift from
/// reality (e.g. an agent crashed before flipping the row to
/// <see cref="ExecutionStatuses.Failed"/>, or a workstream merged its PR
/// while the WI was still flagged <see cref="ExecutionStatuses.Queued"/>) —
/// this inspector returns the value the stored field *should* have right
/// now and a short human-readable reason.
///
/// <para>Used by <c>POST /api/work-items/{id}/execution-status</c>: the
/// drag-to-reconcile endpoint that lets an operator hand-correct a drifted
/// exec-status card on the Exec board. The endpoint only commits the
/// requested target when it equals the inspector's authoritative value —
/// otherwise it returns 409 with the authoritative value + reason so the
/// UI can show an informative toast.</para>
///
/// <para>Truth precedence (first match wins):</para>
/// <list type="number">
///   <item>A recovery-in-flight status is sticky: <see cref="ExecutionStatuses.CooldownBudget"/>
///   and <see cref="ExecutionStatuses.PendingReassign"/> are owned by their
///   dedicated reconcilers; the inspector preserves the stored value so
///   drag-move can't race a reconciler.</item>
///   <item>Live agent runner registered for this work-item id
///   (<see cref="IAgentRunner.IsTaskActive"/>) → <see cref="ExecutionStatuses.Live"/>.</item>
///   <item>Open <see cref="TaskQueueRow"/> in state <c>picked-up</c> →
///   <see cref="ExecutionStatuses.Live"/>.</item>
///   <item>Linked workstream (<c>ws.Id == wi.Id</c>) with <c>Status == "complete"</c>
///   → <see cref="ExecutionStatuses.Completed"/>. <c>ws.Status="complete"</c>
///   is set by the merge pipeline once all linked PRs land (or by an
///   operator Accept) so it's authoritative regardless of the latest
///   deliverable's pass classification — a stale corrupt-pass deliverable
///   does not override a landed merge for the operator-drag use case.
///   The reason string includes the deliverable's classification for
///   transparency.</item>
///   <item>Linked workstream with <c>Status == "stalled"</c> →
///   <see cref="ExecutionStatuses.Failed"/>.</item>
///   <item>Linked workstream with <c>Status == "cancelled"</c> →
///   <see cref="ExecutionStatuses.Cancelled"/>.</item>
///   <item>Open <see cref="TaskQueueRow"/> in any other open state
///   (<c>awaiting-ack</c> / <c>awaiting-route</c> / <c>queued</c> /
///   <c>blocked</c>) → <see cref="ExecutionStatuses.Queued"/>.</item>
///   <item>Stored value is <see cref="ExecutionStatuses.Cancelled"/> AND
///   nothing else applies → <see cref="ExecutionStatuses.Cancelled"/>
///   (operator-set cancellation is preserved when no live activity exists).</item>
///   <item>Stored value is <see cref="ExecutionStatuses.Failed"/> AND no
///   linked workstream → <see cref="ExecutionStatuses.Failed"/>
///   (preserve terminal failure for top-level WIs that don't have a ws).</item>
///   <item>Stored value is <see cref="ExecutionStatuses.Completed"/> AND no
///   linked workstream → <see cref="ExecutionStatuses.Completed"/>
///   (operator-marked done for top-level WIs).</item>
///   <item>Default → <see cref="ExecutionStatuses.Idle"/>.</item>
/// </list>
/// </summary>
public sealed class WorkItemExecutionStatusInspector
{
    private readonly ProductDbContext db;
    private readonly IAgentRunner runner;

    public WorkItemExecutionStatusInspector(ProductDbContext db, IAgentRunner runner)
    {
        this.db = db;
        this.runner = runner;
    }

    /// <summary>
    /// Result of an authoritative-status computation. <see cref="Status"/> is one
    /// of <see cref="ExecutionStatuses"/>; <see cref="Reason"/> is a short
    /// human-readable explanation suitable for a UI toast.
    /// </summary>
    public sealed record Result(string Status, string Reason);

    /// <summary>
    /// Computes the authoritative execution status for the work item. Returns
    /// <c>null</c> when the work item does not exist; otherwise returns a
    /// non-null <see cref="Result"/>.
    /// </summary>
    public async Task<Result?> ComputeAuthoritativeAsync(string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return null;

        var wi = await this.db.WorkItems
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null) return null;

        // (1) Recovery-in-flight statuses are owned by their dedicated
        // reconcilers (BudgetCooldownReconciler / SloppyReassignmentReconciler).
        // The inspector must not let drag-move race a reconciler.
        if (string.Equals(wi.ExecutionStatus, ExecutionStatuses.CooldownBudget, StringComparison.Ordinal))
        {
            return new Result(ExecutionStatuses.CooldownBudget, "Per-task budget cap hit; auto-recovery in flight.");
        }
        if (string.Equals(wi.ExecutionStatus, ExecutionStatuses.PendingReassign, StringComparison.Ordinal))
        {
            return new Result(ExecutionStatuses.PendingReassign, "Sloppy reassignment in flight; auto-recovery owns this row.");
        }

        // (2) Live runner registered for this work item id wins outright.
        if (this.runner.IsTaskActive(workItemId))
        {
            return new Result(ExecutionStatuses.Live, "An agent runner is currently active for this work item.");
        }

        // (3) Open task_queue rows. Picked-up == live; everything else open == queued.
        // Order by Id desc — SQLite-EF can't translate ORDER BY on DateTimeOffset
        // and the auto-increment Id is monotonic with insertion time anyway.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var openQueueRow = await this.db.TaskQueue.AsNoTracking()
            .Where(r => r.WorkItemId == workItemId && openStates.Contains(r.State))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (openQueueRow is not null
            && string.Equals(openQueueRow.State, TaskQueueStates.PickedUp, StringComparison.Ordinal))
        {
            return new Result(
                ExecutionStatuses.Live,
                $"Queue row {openQueueRow.Id} is in 'picked-up' state — agent is working.");
        }

        // (4-6) Linked workstream (ws.Id == wi.Id when WI represents a stage/step).
        var ws = await this.db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is not null)
        {
            if (string.Equals(ws.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                // ws.Status="complete" is set by the merge pipeline when all
                // linked PRs land (or by an operator Accept on the workstream).
                // Either way it's the strongest possible "work shipped" signal
                // and we trust it for the operator-drag use case — a stale
                // corrupt-pass deliverable doesn't override a landed merge.
                //
                // Note: StalledMergedRecoveryReconciler is more conservative
                // (it refuses to *auto*-align WIs with corrupt-pass deliverables
                // to avoid mass-promoting in flake scenarios). Operator-drag is
                // an explicit override on a single row, so it's safe to trust ws.
                var latest = await this.LatestDeliverableAsync(workItemId, ct);
                var passClass = latest is null
                    ? StorySpawnOrchestrator.PassGateClassification.NotPass
                    : StorySpawnOrchestrator.ClassifyPassGate(
                        latest.Verdict, latest.Score, latest.FailureKind, latest.Notes);
                var reason = latest is null
                    ? "Linked workstream is complete (no deliverables found — assumed shipped)."
                    : passClass is StorySpawnOrchestrator.PassGateClassification.NaturalPass
                        or StorySpawnOrchestrator.PassGateClassification.OperatorAccepted
                        ? $"Linked workstream is complete; latest deliverable passed ({passClass})."
                        : $"Linked workstream is complete (merge landed); latest deliverable was {passClass} but the merge is authoritative.";
                return new Result(ExecutionStatuses.Completed, reason);
            }

            if (string.Equals(ws.Status, WorkstreamStatuses.Stalled, StringComparison.OrdinalIgnoreCase))
            {
                var reason = string.IsNullOrEmpty(ws.StallReason)
                    ? "Linked workstream is stalled."
                    : $"Linked workstream is stalled: {ws.StallReason}";
                return new Result(ExecutionStatuses.Failed, reason);
            }

            if (string.Equals(ws.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new Result(ExecutionStatuses.Cancelled, "Linked workstream is cancelled.");
            }
        }

        // (7) Open queue row in any non-picked-up open state means queued.
        if (openQueueRow is not null)
        {
            return new Result(
                ExecutionStatuses.Queued,
                $"Queue row {openQueueRow.Id} is open in state '{openQueueRow.State}'.");
        }

        // (8-10) No live activity, no open queue row, no workstream signal —
        // preserve operator-meaningful stored terminals so we don't silently
        // demote a Cancelled / Failed / Completed top-level WI to Idle.
        if (string.Equals(wi.ExecutionStatus, ExecutionStatuses.Cancelled, StringComparison.Ordinal))
        {
            return new Result(ExecutionStatuses.Cancelled, "No active queue or runner; cancellation is the operative state.");
        }
        if (ws is null && string.Equals(wi.ExecutionStatus, ExecutionStatuses.Failed, StringComparison.Ordinal))
        {
            return new Result(ExecutionStatuses.Failed, "No linked workstream; preserving prior failed status.");
        }
        if (ws is null && string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.Ordinal))
        {
            return new Result(ExecutionStatuses.Completed, "No linked workstream; preserving prior completed status.");
        }

        // (11) Default — nothing in flight, no terminal signal.
        return new Result(ExecutionStatuses.Idle, "No active runner, queue row, or terminal workstream signal.");
    }

    private async Task<DeliverableRow?> LatestDeliverableAsync(string workItemId, CancellationToken ct)
    {
        return await this.db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .OrderByDescending(d => d.PairAttempt ?? 0)
            .ThenByDescending(d => d.Iter)
            .ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);
    }
}
