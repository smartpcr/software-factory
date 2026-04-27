// -----------------------------------------------------------------------
// <copyright file="ITaskQueueService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Contracts;
using Forge.Api.Persistence;

/// <summary>
/// Phase 17.2 — single contract for every state transition on the
/// <see cref="TaskQueueRow"/> state machine. All transitions use atomic
/// SQLite <c>UPDATE … WHERE id=? AND state=?</c> compare-and-swap so two
/// concurrent workers never double-claim the same row.
/// <para>
/// Returns <c>null</c> when the precondition fails (row missing or in the
/// wrong state); callers turn this into a 404 / 409.
/// </para>
/// </summary>
public interface ITaskQueueService
{
    /// <summary>
    /// Q17.2 — enter the pipeline. <paramref name="targetKind"/> is "team"
    /// (the normal team-routed path) or "crew" (direct-to-crew, used by the
    /// existing direct-to-agent assignment path so the unified queue still
    /// records it). Q17.6 idempotency: a duplicate enqueue against the same
    /// open work item returns the existing row instead of failing.
    /// </summary>
    Task<TaskQueueRow> EnqueueAsync(string workItemId, string targetKind, string targetId, CancellationToken ct);

    /// <summary>
    /// Manual ack from the lead, or the auto-ack path on timeout
    /// (<paramref name="auto"/> = true sets <see cref="TaskQueueRow.AutoAcked"/>).
    /// Transitions the row to <c>awaiting-route</c>; if the team has exactly
    /// one matching crew, immediately routes through to <c>queued</c>.
    /// </summary>
    Task<TaskQueueRow?> AcknowledgeAsync(long queueId, string? byUserId, bool auto, CancellationToken ct);

    /// <summary>
    /// Lead picks a crew when the team has multiple matches. Refuses if the
    /// row isn't in <c>awaiting-route</c>.
    /// </summary>
    Task<TaskQueueRow?> RouteToCrewAsync(long queueId, string crewId, string? byUserId, CancellationToken ct);

    /// <summary>
    /// Atomic head-of-queue pop for a crew. Honors priority + FIFO. Returns
    /// <c>null</c> when the crew has no queued rows.
    /// </summary>
    Task<TaskQueueRow?> ClaimNextAsync(string crewId, string? byAgentId, CancellationToken ct);

    /// <summary>Final transition from <c>picked-up</c> → <c>completed</c>.</summary>
    Task<TaskQueueRow?> CompleteAsync(long queueId, CancellationToken ct);

    /// <summary>
    /// Q17.3 — lead moves a queued row from one crew to another. State stays
    /// <c>queued</c>; only <see cref="TaskQueueRow.RoutedToCrewId"/> changes.
    /// </summary>
    Task<TaskQueueRow?> ReassignAsync(long queueId, string newCrewId, string byUserId, CancellationToken ct);

    /// <summary>
    /// Q17.7 — explicit cancel. State → <c>cancelled</c>; the work item
    /// itself is untouched.
    /// </summary>
    Task<TaskQueueRow?> CancelAsync(long queueId, string byUserId, string? reason, CancellationToken ct);

    /// <summary>
    /// List rows for a target. <paramref name="states"/> filters; null
    /// returns every state.
    /// </summary>
    Task<IReadOnlyList<TaskQueueRow>> GetQueueAsync(string targetKind, string targetId, IReadOnlyList<string>? states, CancellationToken ct);

    /// <summary>Single row by id, with a best-effort latest history fetch.</summary>
    Task<TaskQueueRow?> GetAsync(long queueId, CancellationToken ct);

    /// <summary>Append-only audit fetch for a row.</summary>
    Task<IReadOnlyList<TaskQueueHistoryRow>> GetHistoryAsync(long queueId, CancellationToken ct);

    /// <summary>
    /// Q17.3 — depth + ETA + strained flag for a crew. ETA = depth × p50
    /// recent iteration minutes for that crew, falling back to a global
    /// default of 25 min when the sample size is too small.
    /// </summary>
    Task<CrewQueueStats> GetCrewStatsAsync(string crewId, CancellationToken ct);

    /// <summary>
    /// Strain confirm — lead acknowledges the strain banner without
    /// reassigning. Suppresses the next debounce window.
    /// </summary>
    Task<TaskQueueRow?> ConfirmStrainAsync(long queueId, string byUserId, CancellationToken ct);
}

/// <summary>Phase 17 — defaults that drive the strain detector + ETA fallback.</summary>
public static class TaskQueueDefaults
{
    /// <summary>Q17.3 — strained when queue depth exceeds this.</summary>
    public const int StrainDepthThreshold = 3;

    /// <summary>Q17.3 — strained when ETA exceeds this many minutes.</summary>
    public const int StrainEtaMinutesThreshold = 4 * 60;

    /// <summary>ETA fallback when the crew has &lt; 5 closed deliverables.</summary>
    public const int DefaultIterationMinutes = 25;

    /// <summary>Q17.4 — picked-up rows older than this are considered abandoned.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    /// <summary>Q17.2 — ack SLO from spec; the auto-ack path fires when this elapses.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);
}
