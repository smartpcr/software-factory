// -----------------------------------------------------------------------
// <copyright file="ImplSpawnGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 2026-05-07 — pure decision helper for <see cref="StorySpawnOrchestrator"/>'s
/// 15-second tick. Decides, given the transcript of impl-spawn events for a
/// workstream, whether the next tick is allowed to launch a fresh
/// <c>DriveOneWorkstreamImplAsync</c>.
/// <para>
/// Two states block a spawn:
/// </para>
/// <list type="number">
///   <item>
///     <b>In flight</b> — a <c>task.impl.spawn-started</c> event exists with
///     no later <c>task.impl.spawn-finished</c>, and the elapsed wall-clock
///     time since the start is less than the per-WS budget plus slack.
///   </item>
///   <item>
///     <b>Failure backoff</b> — the latest <c>task.impl.spawn-finished</c>
///     has <c>success=false</c>, and the elapsed time since that failure is
///     less than <c>min(FailureBackoffBase * consecutiveFailures,
///     FailureBackoffMax)</c>. Letting the immediate next tick respawn was
///     exactly the failure mode (25 spawns over 80 minutes for one WS,
///     observed 2026-05-07 on WI-HIGH-MEM-CONSUMPTION iter 38).
///   </item>
/// </list>
/// <para>
/// Pure-function semantics so unit tests can construct an in-memory DbContext
/// + transcript fixture and assert each branch. Production callers must pass
/// <c>DateTimeOffset.UtcNow</c> for <paramref name="now"/>; tests can pass a
/// frozen value to deterministically exercise the boundaries.
/// </para>
/// </summary>
internal static class ImplSpawnGuard
{
    public const string SpawnStartedKind = "task.impl.spawn-started";
    public const string SpawnFinishedKind = "task.impl.spawn-finished";

    /// <summary>
    /// Phase 52 follow-up — emitted by the orchestrator on the FIRST
    /// no-progress backoff for a workstream so the operator sees one
    /// clear signal in the conversation tab (rather than a cluster of
    /// raw spawn-started/finished pairs). Subsequent ticks within the
    /// same no-progress run reuse the backoff path silently.
    /// </summary>
    public const string SpawnNoProgressKind = "task.impl.spawn-no-progress";

    public enum Decision
    {
        /// <summary>No prior in-flight or backoff blocking — caller may spawn.</summary>
        Allow,

        /// <summary>An unfinished spawn exists within the budget+slack window.</summary>
        SkipInFlight,

        /// <summary>The most recent finished spawn failed and the backoff window has not yet elapsed.</summary>
        SkipBackoff,

        /// <summary>
        /// Phase 52 follow-up — consecutive successful spawns produced no new
        /// deliverable. Same skip semantics as <see cref="SkipBackoff"/>;
        /// distinct value so telemetry / log filters can separate the
        /// two failure modes.
        /// </summary>
        SkipNoProgressBackoff,

        /// <summary>
        /// 2026-05-15 — the work item is parked on
        /// <c>stalled-questions</c> with unanswered questions. No spawn is
        /// allowed AND the no-progress counter is suppressed: parking on
        /// operator questions is intentional, NOT a sloppy-loop signal.
        /// Without this, the pre-iter gate at
        /// <c>DeliverableLoop.cs:568-594</c> keeps returning
        /// <c>stalled-questions</c> on every tick without producing a new
        /// deliverable row; <c>CountConsecutiveNoProgressSuccesses</c>
        /// crosses <see cref="SyncConstants.NoProgressReassignThreshold"/>
        /// after ~5 ticks, routes to
        /// <see cref="StorySpawnOrchestrator.TryRouteToSloppyReassignAsync"/>,
        /// and the SloppyReassignmentReconciler stamps a bogus
        /// <c>"Sloppy-3-attempts: ..."</c> terminal failure on a workstream
        /// that's legitimately waiting for the operator. Observed
        /// 2026-05-15 on code-intelligence:AGENT-MEMORY ws 1.3 (Episodic
        /// and concept schema migrations): iter 1 stalled-questions at
        /// 7:38, 5 no-progress ticks later (7:50:05)
        /// task.sloppy-parked → task.sloppy-terminal-failure at 7:50:06
        /// → workstream wedged in stalled even after operator answered
        /// and iter 3 eventually passed at 11:38 (score 92).
        /// </summary>
        SkipAwaitingAnswers,
    }

    /// <summary>
    /// Result of one guard evaluation.
    /// </summary>
    /// <param name="Outcome">Decision the caller must obey.</param>
    /// <param name="Reason">Human-readable explanation for logs / telemetry.</param>
    /// <param name="ConsecutiveNoProgress">
    /// Phase 52 follow-up — count of consecutive spawn-finished:true events
    /// whose corresponding spawn-started fired AFTER the latest
    /// deliverable's <c>CreatedAt</c>. Zero on the happy path; non-zero
    /// when the workstream is stuck in a re-spawn loop without new work.
    /// </param>
    /// <param name="ShouldTriggerReassign">
    /// Phase 52 follow-up — true when <see cref="ConsecutiveNoProgress"/>
    /// has reached <see cref="SyncConstants.NoProgressReassignThreshold"/>.
    /// Caller routes the work item through the Phase 52
    /// <c>SloppyReassignmentReconciler</c>: set ExecutionStatus=pending-reassign,
    /// append the current pair to PriorPairsJson, skip this tick.
    /// </param>
    public sealed record Result(
        Decision Outcome,
        string Reason,
        int ConsecutiveNoProgress = 0,
        bool ShouldTriggerReassign = false);

    /// <summary>
    /// Decide whether a fresh impl spawn is allowed for <paramref name="workstreamId"/>.
    /// </summary>
    /// <param name="isInvocationActive">
    /// Phase 64 — optional in-process check ("is there a live SDK
    /// invocation tied to this taskId right now?"). When supplied AND
    /// the in-flight branch overruns its budget+slack window, the guard
    /// still refuses to Allow if this delegate reports the invocation
    /// is alive — preventing the concurrent dispatch observed
    /// 2026-05-14 on local:UPDATE-HISTORY-IN-CL where a second agent
    /// was started while the first was still 63 s short of its own
    /// 16-min timeout. Null = original behaviour (transcript-only).
    /// </param>
    public static async Task<Result> CheckAsync(
        ProductDbContext db,
        string workstreamId,
        int wsPoints,
        DateTimeOffset now,
        CancellationToken ct,
        Func<string, bool>? isInvocationActive = null)
    {
        // Pull the small set of impl-spawn events for this WI in one query —
        // there's typically a handful of rows; sorting in memory avoids
        // index dependence on (WorkItemId, Kind, Id) which we don't have today.
        var eventRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workstreamId
                     && (e.Kind == SpawnStartedKind || e.Kind == SpawnFinishedKind))
            .OrderByDescending(e => e.Id)
            .Take(64)
            .Select(e => new { e.Id, e.Kind, e.At, e.PayloadJson })
            .ToListAsync(ct);

        // Materialize to a named tuple list so the no-progress helper can
        // index into it without dynamic-typing the anonymous projection
        // (DLR over internal anonymous types is brittle).
        var events = eventRows
            .Select(e => (Id: e.Id, Kind: e.Kind, At: e.At, PayloadJson: e.PayloadJson))
            .ToList();

        if (events.Count == 0)
        {
            return new Result(Decision.Allow, "no prior impl spawn");
        }

        var latestStart = events.FirstOrDefault(e => e.Kind == SpawnStartedKind);
        var latestFinish = events.FirstOrDefault(e => e.Kind == SpawnFinishedKind);

        // Case A — never started (only stray finishes? impossible in practice
        // but defensive). Allow.
        if (latestStart.Id == 0)
        {
            return new Result(Decision.Allow, "no spawn-started event");
        }

        // Case B — finished after the most recent start → not in flight.
        var finishedAfterStart = latestFinish.Id != 0 && latestFinish.Id > latestStart.Id;

        if (!finishedAfterStart)
        {
            // Still in flight from the orchestrator's perspective. Skip while
            // the elapsed time is within budget+slack; release the guard once
            // the budget has clearly overrun (something hung beyond what's
            // reasonable, give the next tick a chance to recover).
            var budget = CrewStoryArtifactStrategy.BudgetForPhase(wsPoints, "impl");
            var guardSeconds = Math.Max(
                SyncConstants.InflightSpawnGuardMinSeconds,
                (int)budget.TotalSeconds + SyncConstants.InflightSpawnGuardBudgetSlackSeconds);
            var elapsed = (now - latestStart.At).TotalSeconds;

            // Phase 71 — fast recovery on dead invocation. When the
            // dispatcher's isActive check is wired AND reports the task
            // is NOT alive, the original orchestrator drive must have
            // died (process crash, Forge.Api restart, host kill).
            // Waiting the full budget+slack would burn 20-30 min of
            // dead time on a workstream whose verdict has already
            // landed in DB but the post-loop commit/PR step never ran.
            // Observed 2026-05-15 on
            // failover-cluster:XRAFT:stage-4.1-grpc-transport-layer
            // iter 7 (verdict=pass at 4:49:43, Forge.Api restarted 40s
            // later, workstream sat at 13min elapsed / 26min budget,
            // PR never opened, ACCEPT button hidden by Vite cache).
            // The recovery spawn rebuilds from the persisted deliverable
            // chain so no work is lost.
            if (isInvocationActive is not null && !isInvocationActive(workstreamId))
            {
                return new Result(
                    Decision.Allow,
                    $"in-flight spawn-started {elapsed:0}s ago has no matching finish AND dispatcher reports invocation NOT alive — original drive died (likely Forge.Api restart). Allowing recovery spawn.");
            }

            if (elapsed < guardSeconds)
            {
                return new Result(
                    Decision.SkipInFlight,
                    $"in-flight: spawn-started {elapsed:0}s ago < guard {guardSeconds}s (budget {budget.TotalMinutes:0}min + slack)");
            }

            // Overran — historically we let a recovery spawn run while
            // the hung invocation was left to time out on its own
            // wall-clock budget. Phase 64 — if the dispatcher confirms
            // the SDK invocation is still alive in-process, refuse the
            // recovery spawn instead. The original invocation gets a
            // few more minutes to finish (or its own wall-clock will
            // kill it shortly anyway), and we save the double-token
            // burn of a parallel agent on the same iter.
            if (isInvocationActive is not null && isInvocationActive(workstreamId))
            {
                return new Result(
                    Decision.SkipInFlight,
                    $"in-flight overran guard ({elapsed:0}s >= {guardSeconds}s) "
                    + "but dispatcher reports invocation still alive; refusing concurrent spawn");
            }
            return new Result(
                Decision.Allow,
                $"in-flight overran guard: {elapsed:0}s >= {guardSeconds}s; allowing recovery spawn");
        }

        // Case C — most recent attempt finished. Decide based on success/fail.
        var lastFailed = LastSpawnFailed(latestFinish.PayloadJson);
        if (!lastFailed)
        {
            // 2026-05-15 — awaiting-answers detection. When the gen-eval
            // loop's pre-iter gate (DeliverableLoop.cs:568-594) parks the
            // WI on `stalled-questions` because there are unanswered
            // operator questions, every subsequent orchestrator tick:
            //   - spawns the loop
            //   - the loop short-circuits via the pre-iter gate WITHOUT
            //     creating a new deliverable row
            //   - returns success=true to the spawn-finished event
            // Without the early-return below, those "successful" spawns
            // accumulate in CountConsecutiveNoProgressSuccesses and after
            // ~5 ticks ShouldTriggerReassign=true flips on, routing the
            // (legitimately parked) workstream through
            // SloppyReassignmentReconciler.MarkTerminalFailedAsync which
            // stamps a bogus "Sloppy-3-attempts" stall. Detection rule:
            // the latest deliverable's verdict is `stalled-questions` AND
            // at least one DeliverableQuestion row for this WI is still
            // unanswered (Answer is null). When both hold, we return
            // SkipAwaitingAnswers with ShouldTriggerReassign=false and
            // ConsecutiveNoProgress=0 so even the conversation tab's
            // "consecutive no-progress" counter resets cleanly once the
            // operator answers.
            var latestDeliv = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == workstreamId)
                .Select(d => new { d.Verdict, d.CreatedAt })
                .ToListAsync(ct);
            var orderedDelivs = latestDeliv
                .OrderByDescending(d => d.CreatedAt)
                .ToList();
            DateTimeOffset? latestDeliverableAt = orderedDelivs.Count == 0
                ? null
                : orderedDelivs[0].CreatedAt;
            var latestVerdict = orderedDelivs.Count == 0 ? null : orderedDelivs[0].Verdict;

            if (string.Equals(latestVerdict, "stalled-questions", StringComparison.Ordinal))
            {
                var hasUnanswered = await db.DeliverableQuestions.AsNoTracking()
                    .Where(q => q.WorkItemId == workstreamId && q.AnsweredAt == null)
                    .AnyAsync(ct);
                if (hasUnanswered)
                {
                    return new Result(
                        Decision.SkipAwaitingAnswers,
                        "parked on stalled-questions verdict with unanswered operator questions; suppressing no-progress counter",
                        ConsecutiveNoProgress: 0,
                        ShouldTriggerReassign: false);
                }
                // Questions are all answered/dismissed but the gate hasn't
                // re-fired yet (the resume is fire-and-forget; this tick
                // may have raced ahead of the gen-eval call). Fall through
                // with a FRESH progress boundary set to the most recent
                // answered timestamp so pre-answer no-progress events
                // don't immediately trigger reassignment on the first
                // post-answer tick.
            }

            // 2026-05-15 — progress boundary = max(latestDeliverableAt,
            // latestAnsweredAt). Operator answers count as forward
            // progress: any spawn that started before the most recent
            // answer is "from the pre-answer era" and must NOT count
            // toward consecutive no-progress (the workstream WAS making
            // progress via the operator's dialogue even if no deliverable
            // landed). Without this, a workstream that sat on
            // stalled-questions for 5+ ticks and then resumed would
            // immediately re-trigger sloppy reassignment on the post-
            // answer happy-path tick.
            var answeredTimestamps = await db.DeliverableQuestions.AsNoTracking()
                .Where(q => q.WorkItemId == workstreamId && q.AnsweredAt != null)
                .Select(q => q.AnsweredAt)
                .ToListAsync(ct);
            DateTimeOffset? latestAnsweredAt = answeredTimestamps.Count == 0
                ? null
                : answeredTimestamps.Where(a => a != null).Max();

            DateTimeOffset? progressBoundary = latestDeliverableAt;
            if (latestAnsweredAt is DateTimeOffset ans
                && (progressBoundary is null || ans > progressBoundary.Value))
            {
                progressBoundary = ans;
            }

            var consecutiveNoProgress = CountConsecutiveNoProgressSuccesses(events, progressBoundary);
            if (consecutiveNoProgress >= SyncConstants.NoProgressReassignThreshold)
            {
                // Hand off to Phase 52 reassignment. We still RETURN a
                // skip decision so this tick doesn't fire — the
                // orchestrator's reassign-routing block runs as a side
                // effect of seeing ShouldTriggerReassign=true.
                return new Result(
                    Decision.SkipNoProgressBackoff,
                    $"no-progress reassign: {consecutiveNoProgress} consecutive successful spawns without new deliverable (>= {SyncConstants.NoProgressReassignThreshold} threshold); routing through Phase 52",
                    consecutiveNoProgress,
                    ShouldTriggerReassign: true);
            }

            if (consecutiveNoProgress >= SyncConstants.NoProgressBackoffThreshold)
            {
                // Escalating backoff. Re-use the failure backoff curve
                // (Base × N, capped at Max) so the operator sees the same
                // escalation shape they're already used to.
                var backoffSeconds = Math.Min(
                    SyncConstants.FailureBackoffMaxSeconds,
                    SyncConstants.FailureBackoffBaseSeconds * consecutiveNoProgress);
                var sinceFinish = (now - latestFinish.At).TotalSeconds;
                if (sinceFinish < backoffSeconds)
                {
                    return new Result(
                        Decision.SkipNoProgressBackoff,
                        $"no-progress backoff: {sinceFinish:0}s since last success < {backoffSeconds}s ({consecutiveNoProgress} consecutive no-progress successes)",
                        consecutiveNoProgress,
                        ShouldTriggerReassign: false);
                }
                // Cool-down elapsed — allow one probe spawn.
                return new Result(
                    Decision.Allow,
                    $"no-progress cool-down elapsed: {sinceFinish:0}s >= {backoffSeconds}s ({consecutiveNoProgress} consecutive no-progress); allowing probe",
                    consecutiveNoProgress,
                    ShouldTriggerReassign: false);
            }

            return new Result(
                Decision.Allow,
                "previous spawn finished successfully",
                consecutiveNoProgress,
                ShouldTriggerReassign: false);
        }

        // Failure backoff. Count consecutive failed-finishes from the most
        // recent backwards (in event order, descending) until we hit either
        // a successful finish or a start without a finish (i.e. an
        // intervening recovery).
        var consecutiveFailures = 0;
        foreach (var ev in events.Where(e => e.Kind == SpawnFinishedKind))
        {
            if (LastSpawnFailed(ev.PayloadJson)) consecutiveFailures++;
            else break;
        }

        var backoffSec = Math.Min(
            SyncConstants.FailureBackoffMaxSeconds,
            SyncConstants.FailureBackoffBaseSeconds * Math.Max(1, consecutiveFailures));
        var sinceFailure = (now - latestFinish.At).TotalSeconds;
        if (sinceFailure < backoffSec)
        {
            return new Result(
                Decision.SkipBackoff,
                $"failure backoff: {sinceFailure:0}s since last failure < {backoffSec}s ({consecutiveFailures} consecutive)");
        }

        return new Result(
            Decision.Allow,
            $"failure backoff elapsed: {sinceFailure:0}s >= {backoffSec}s ({consecutiveFailures} consecutive failures)");
    }

    /// <summary>
    /// Phase 52 follow-up — count the most-recent consecutive run of
    /// spawn-finished:true events whose matching spawn-started fired
    /// AFTER <paramref name="progressBoundary"/>. The count resets when
    /// we encounter a start with no matching finish (in-flight, not
    /// counted), a finish with success=false (handled by the failure
    /// backoff path, not counted here), or a spawn-started.At &lt;=
    /// <paramref name="progressBoundary"/> (this spawn predates a
    /// progress event — either a deliverable or an operator answer).
    /// </summary>
    /// <param name="events">
    /// Spawn-started + spawn-finished events for the workstream, sorted
    /// descending by Id (most-recent first).
    /// </param>
    /// <param name="progressBoundary">
    /// 2026-05-15 — timestamp used as the "this is forward progress"
    /// cutoff. Spawns that started AT OR BEFORE this boundary belong to
    /// a prior progress window and don't count toward consecutive
    /// no-progress. The caller uses
    /// <c>max(latestDeliverableAt, latestAnsweredAt)</c> so operator
    /// answers to <c>stalled-questions</c> count as forward progress
    /// (otherwise pre-answer no-progress ticks would immediately
    /// trigger reassignment on the first post-answer tick).
    /// </param>
    private static int CountConsecutiveNoProgressSuccesses(
        IReadOnlyList<(long Id, string Kind, DateTimeOffset At, string PayloadJson)> events,
        DateTimeOffset? progressBoundary)
    {
        // When there's no deliverable AND no answered question, treat as
        // "infinitely old" so any successful spawn counts as no-progress.
        // (Brand-new workstreams whose first iteration is still in flight
        // take the SkipInFlight branch above, not this one.)
        var threshold = progressBoundary ?? DateTimeOffset.MinValue;

        var count = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Kind != SpawnFinishedKind) continue;

            // Look for the matching spawn-started (largest Id < finish.Id).
            DateTimeOffset? startedAt = null;
            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind != SpawnStartedKind) continue;
                startedAt = events[j].At;
                break;
            }
            if (startedAt is null) break; // no matching start — stop scanning

            // A failure-finish belongs to the failure-backoff path; don't
            // count, and break (the streak doesn't extend through a fail).
            if (LastSpawnFailed(ev.PayloadJson)) break;

            // Spawn started BEFORE the latest deliverable → its window may
            // have produced the deliverable. Real progress; stop counting.
            if (startedAt.Value <= threshold) break;

            count++;
        }
        return count;
    }

    /// <summary>
    /// Extract the <c>success</c> flag from a <c>task.impl.spawn-finished</c>
    /// payload. Defaults to <c>false</c> (failure) when the payload is
    /// missing or malformed — ambiguity should fail closed (apply backoff)
    /// rather than risk a retry storm.
    /// </summary>
    private static bool LastSpawnFailed(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("success", out var s)
                && s.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }
}
