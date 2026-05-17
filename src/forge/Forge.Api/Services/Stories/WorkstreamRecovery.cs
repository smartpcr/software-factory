// -----------------------------------------------------------------------
// <copyright file="WorkstreamRecovery.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 67 — "operator-driven recovery from a stalled / sloppy-capped
/// workstream whose root cause has been resolved" (questions answered,
/// base branch unblocked, etc). Mirrors a subset of the Workstream
/// Restart endpoint's reset surface — enough to make the orchestrator
/// re-claim the row — without touching the branch, worktree, PRs, or
/// deliverables history (those carry the prior context the resumed
/// gen-eval loop needs).
///
/// <para><b>Why this exists.</b> Three operator surfaces that all need
/// the same reset semantic were each independently incomplete:</para>
/// <list type="number">
///   <item><c>WorkstreamsEndpoints.Retry</c> — preserved
///   <see cref="WorkItemRow.PairAttemptCount"/> per its
///   "continuation, not clean slate" policy. On a row that hit
///   <see cref="ScoringConstants.MaxPairAttempts"/>=3 (Sloppy-3-attempts),
///   the orchestrator's claim loop at
///   <c>StorySpawnOrchestrator.cs:2418-2431</c> immediately re-capped
///   and refused to advance — Retry was structurally incapable of
///   recovering a sloppy-capped row.</item>
///   <item><c>TaskExecutionEndpoints.TryUnparkAndResumeAsync</c>
///   workstream branch (Phase 66) — only cleared the parent spawn's
///   <c>LastError</c> and emitted a SignalR event. Did NOT touch
///   <c>ws.Status</c>, the pair-attempt accounting, or the verdict.
///   Returned <c>resumeScheduled=true</c> while doing nothing
///   meaningful, so the UI looked like it worked but the workstream
///   stayed terminal-failed.</item>
///   <item>The verdict-demotion list at <c>Retry</c> line 1121
///   only included <c>pass</c> / <c>stalled</c> / <c>stalled-budget</c>,
///   missing <c>stalled-questions</c>, <c>sloppy</c>, and
///   <c>stalled-no-convergence</c>. So a Retry on a row whose last
///   verdict was <c>stalled-questions</c> didn't demote at all.</item>
/// </list>
///
/// <para>This helper centralizes the recovery semantic so all three
/// paths converge on the same correct shape. Observed 2026-05-15 on
/// <c>code-intelligence:AGENT-MEMORY</c> Stage 1.3 Episodic and
/// concept schema migrations: 3 sloppy reassignments burned ~$690
/// chasing the same unanswered SQL-design question; operator
/// answered + clicked Retry, both no-ops; row stayed terminal-failed.</para>
/// </summary>
public static class WorkstreamRecovery
{
    /// <summary>
    /// Outcome of <see cref="SoftResetForResumeAsync"/>. Caller can
    /// surface this on the API response so the operator knows what
    /// actually changed (helpful when the workstream wasn't stalled
    /// in the first place — only the verdict demotion fires).
    /// </summary>
    public sealed record SoftResetReport(
        bool StatusReset,
        bool PairAccountingReset,
        int RemovedPairAssignments,
        int RemovedAgentUsage,
        bool VerdictDemoted,
        string? PriorStatus,
        string? PriorVerdict);

    /// <summary>
    /// Apply the recovery reset. Caller is responsible for
    /// <c>SaveChangesAsync</c>; this method only mutates tracked
    /// entities so a single transaction can wrap the change set.
    ///
    /// <para><b>What gets reset:</b></para>
    /// <list type="bullet">
    ///   <item><c>ws.Status</c>: <c>stalled</c> → <c>active</c> (when stalled).</item>
    ///   <item><c>ws.StallReason</c>: cleared (when status was reset).</item>
    ///   <item><c>wi.PairAttemptCount</c> → 1, <c>PriorPairsJson</c> /
    ///   <c>LastPairAgentIdsJson</c> / <c>FailureReason</c> → null,
    ///   <c>BudgetWindowStartUtc</c> → now, <c>ExecutionStatus</c> →
    ///   <c>queued</c>: ONLY when the workstream was stalled OR had hit
    ///   the pair-attempt cap. Ordinary not-yet-failed retries preserve
    ///   the running pair-attempt accounting per the
    ///   "continuation, not clean slate" intent.</item>
    ///   <item><c>agent_pair_assignment</c> + <c>agent_usage</c> rows for
    ///   this work item: removed when pair accounting is reset, so the
    ///   <c>PairDiversityResolver</c> sees a clean slate (no pinned-pair
    ///   exclusion against the burned attempts) and the cost ledger
    ///   restarts at zero. Matches the Workstream Restart endpoint's
    ///   wipe semantics exactly.</item>
    ///   <item><c>lastDel.Verdict</c>: terminal verdicts (<c>pass</c>,
    ///   <c>stalled</c>, <c>stalled-budget</c>, <c>stalled-questions</c>,
    ///   <c>stalled-no-convergence</c>, <c>sloppy</c>) → <c>iterate</c>
    ///   so <see cref="DeliverableLoop"/>'s seeding logic re-enters at
    ///   iter+1 with the full prior chain (which now includes the
    ///   answered question / unblocked context).</item>
    /// </list>
    ///
    /// <para><b>What gets preserved:</b> branch, worktree, deliverables
    /// (the prior chain is the whole point — the architect's hard-won
    /// context survives), transcript, PR rows, attachments,
    /// open-questions answers.</para>
    /// </summary>
    public static async Task<SoftResetReport> SoftResetForResumeAsync(
        ControlDbContext control,
        ProductDbContext db,
        WorkstreamRow ws,
        WorkItemRow? wi,
        DeliverableRow? lastDel,
        string? operatorActor,
        string? noteSuffix,
        CancellationToken ct = default)
    {
        var priorStatus = ws.Status;
        var priorVerdict = lastDel?.Verdict;

        // (1) Un-stall the workstream so the orchestrator's claim loop
        // stops skipping it. Stalled is the only sticky-skip status we
        // un-stick here; cancelled / historic / complete stay where they are.
        var statusReset = false;
        if (string.Equals(ws.Status, WorkstreamStatuses.Stalled, StringComparison.Ordinal))
        {
            ws.Status = "active";
            ws.StallReason = null;
            statusReset = true;
        }

        // (2) Reset pair-attempt accounting when either:
        //   (a) the row was stalled (terminal-failed; no point preserving
        //       the burned-pair history into the recovery attempt), OR
        //   (b) the row hit the sloppy-attempt cap (the orchestrator's
        //       claim loop refuses to advance regardless of status).
        // Plain "ordinary Retry on a still-active row" gates here so the
        // existing "continuation, not clean slate" semantic survives.
        var atCap = wi is not null && wi.PairAttemptCount >= ScoringConstants.MaxPairAttempts;
        var pairReset = false;
        var removedAssignments = 0;
        var removedUsage = 0;
        if (wi is not null && (statusReset || atCap))
        {
            wi.PairAttemptCount = 1;
            wi.PriorPairsJson = null;
            wi.LastPairAgentIdsJson = null;
            wi.FailureReason = null;
            wi.BudgetWindowStartUtc = DateTimeOffset.UtcNow;
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            pairReset = true;

            // Wipe agent_pair_assignment rows so PairDiversityResolver
            // doesn't pin against the prior burned attempts.
            var prior = await db.AgentPairAssignments
                .Where(a => a.WorkItemId == wi.Id)
                .ToListAsync(ct);
            if (prior.Count > 0)
            {
                db.AgentPairAssignments.RemoveRange(prior);
                removedAssignments = prior.Count;
            }

            // Wipe agent_usage rows so the cost ledger for the recovery
            // attempt starts at zero. Matches the Workstream Restart
            // endpoint's wipe semantics so the operator sees consistent
            // cost-card behavior across recovery surfaces.
            var usage = await control.AgentUsage
                .Where(u => u.TaskId == wi.Id)
                .ToListAsync(ct);
            if (usage.Count > 0)
            {
                control.AgentUsage.RemoveRange(usage);
                removedUsage = usage.Count;
            }
        }

        // (3) Demote terminal verdicts to iterate so DeliverableLoop's
        // seeding logic re-enters the gen-eval loop at iter+1 with the
        // full prior chain. The terminal-verdict list mirrors what
        // DeliverableLoop.cs:526 already accepts as resumable, plus
        // `pass` (which can land here from the Phase 56 OnPassValidate
        // throw path — evaluator scored at pass but the gate suite
        // rejected it post-pass).
        var verdictDemoted = false;
        if (lastDel is not null
            && lastDel.Verdict is "pass"
                or "stalled"
                or "stalled-budget"
                or "stalled-questions"
                or "stalled-no-convergence"
                or "sloppy")
        {
            lastDel.Verdict = "iterate";
            var actor = string.IsNullOrEmpty(operatorActor) ? "operator" : operatorActor;
            var note = $"OPERATOR RECOVERY ({actor}) — verdict demoted from '{priorVerdict}' to 'iterate'"
                + (string.IsNullOrEmpty(noteSuffix) ? string.Empty : "; " + noteSuffix)
                + (pairReset ? "; pair-attempt accounting + agent_usage wiped so the orchestrator re-claims with a fresh pair on a zeroed cost ledger." : ".");
            lastDel.Notes = string.IsNullOrEmpty(lastDel.Notes)
                ? note
                : lastDel.Notes + "\n\n" + note;
            verdictDemoted = true;
        }

        return new SoftResetReport(
            StatusReset: statusReset,
            PairAccountingReset: pairReset,
            RemovedPairAssignments: removedAssignments,
            RemovedAgentUsage: removedUsage,
            VerdictDemoted: verdictDemoted,
            PriorStatus: priorStatus,
            PriorVerdict: priorVerdict);
    }
}
