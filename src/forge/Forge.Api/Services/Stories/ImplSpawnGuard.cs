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

    public enum Decision
    {
        /// <summary>No prior in-flight or backoff blocking — caller may spawn.</summary>
        Allow,

        /// <summary>An unfinished spawn exists within the budget+slack window.</summary>
        SkipInFlight,

        /// <summary>The most recent finished spawn failed and the backoff window has not yet elapsed.</summary>
        SkipBackoff,
    }

    public sealed record Result(Decision Outcome, string Reason);

    /// <summary>
    /// Decide whether a fresh impl spawn is allowed for <paramref name="workstreamId"/>.
    /// </summary>
    public static async Task<Result> CheckAsync(
        ForgeDbContext db,
        string workstreamId,
        int wsPoints,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Pull the small set of impl-spawn events for this WI in one query —
        // there's typically a handful of rows; sorting in memory avoids
        // index dependence on (WorkItemId, Kind, Id) which we don't have today.
        var events = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workstreamId
                     && (e.Kind == SpawnStartedKind || e.Kind == SpawnFinishedKind))
            .OrderByDescending(e => e.Id)
            .Take(64)
            .Select(e => new { e.Id, e.Kind, e.At, e.PayloadJson })
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return new Result(Decision.Allow, "no prior impl spawn");
        }

        var latestStart = events.FirstOrDefault(e => e.Kind == SpawnStartedKind);
        var latestFinish = events.FirstOrDefault(e => e.Kind == SpawnFinishedKind);

        // Case A — never started (only stray finishes? impossible in practice
        // but defensive). Allow.
        if (latestStart is null)
        {
            return new Result(Decision.Allow, "no spawn-started event");
        }

        // Case B — finished after the most recent start → not in flight.
        var finishedAfterStart = latestFinish is not null && latestFinish.Id > latestStart.Id;

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
            if (elapsed < guardSeconds)
            {
                return new Result(
                    Decision.SkipInFlight,
                    $"in-flight: spawn-started {elapsed:0}s ago < guard {guardSeconds}s (budget {budget.TotalMinutes:0}min + slack)");
            }

            // Overran — fall through to allow recovery spawn. The currently
            // hung invocation (if any) will be left to time out on its own
            // wall-clock budget; the new spawn provides forward progress.
            return new Result(
                Decision.Allow,
                $"in-flight overran guard: {elapsed:0}s >= {guardSeconds}s; allowing recovery spawn");
        }

        // Case C — most recent attempt finished. Decide based on success/fail.
        var lastFailed = LastSpawnFailed(latestFinish!.PayloadJson);
        if (!lastFailed)
        {
            return new Result(Decision.Allow, "previous spawn finished successfully");
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

        var backoffSeconds = Math.Min(
            SyncConstants.FailureBackoffMaxSeconds,
            SyncConstants.FailureBackoffBaseSeconds * Math.Max(1, consecutiveFailures));
        var sinceFailure = (now - latestFinish.At).TotalSeconds;
        if (sinceFailure < backoffSeconds)
        {
            return new Result(
                Decision.SkipBackoff,
                $"failure backoff: {sinceFailure:0}s since last failure < {backoffSeconds}s ({consecutiveFailures} consecutive)");
        }

        return new Result(
            Decision.Allow,
            $"failure backoff elapsed: {sinceFailure:0}s >= {backoffSeconds}s ({consecutiveFailures} consecutive failures)");
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
