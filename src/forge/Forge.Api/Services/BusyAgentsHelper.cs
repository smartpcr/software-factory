// -----------------------------------------------------------------------
// <copyright file="BusyAgentsHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Spec Phase 47 — central source for "which agents are busy right now".
/// The cost ledger (<see cref="AgentUsageRow"/>) is the truth: every
/// invocation appends a row at start and the runner sets
/// <see cref="AgentUsageRow.EndedAt"/> on completion / cancellation.
///
/// <para>Why ledger-driven, not <c>task_queue.PickedUpByAgent</c>: the
/// queue row only carries the generator agent id, not the evaluator,
/// and plan-doc + judge invocations don't go through the queue at all.
/// The ledger sees every invocation regardless of caller path, so a
/// single source covers all four busy-detection scenarios:
/// <list type="bullet">
/// <item>Engineer picking a generator while another generator is mid-iteration.</item>
/// <item>Engineer picking an evaluator while the same evaluator is still scoring a sibling row.</item>
/// <item>Architect picking the 4th plan-doc evaluator while the 1st is still running.</item>
/// <item>Judge spawn for a dispute while the same agent is the active generator.</item>
/// </list>
/// </para>
///
/// <para>Stale-row reaper: a crashed runner can leave <c>EndedAt = null</c>
/// rows behind, permanently marking an agent busy. <see cref="ReapStaleAsync"/>
/// runs at boot to flip rows older than the cap to <c>EndedAt = StartedAt + cap</c>.
/// The cap is 24 hours — any legitimate iteration is long-since done by
/// then (Phase 14 evaluator-loop and Phase 27 build-fix loop both have
/// their own per-iter wall-clock caps well below 24h).</para>
/// </summary>
internal static class BusyAgentsHelper
{
    /// <summary>
    /// Stale-row cap for <see cref="ReapStaleAsync"/>. A row whose
    /// <c>EndedAt</c> is still null after this much wall-clock time is
    /// presumed orphaned by a crashed runner.
    /// </summary>
    public static readonly TimeSpan StaleAgentUsageCap = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns the set of agent IDs with an open invocation. O(busy)
    /// scan over <c>agent_usage WHERE EndedAt IS NULL</c>; in production
    /// the busy set is bounded by the sum of crew <c>MaxConcurrent</c>
    /// caps across active crews so the result is small.
    /// </summary>
    public static async Task<HashSet<string>> LoadBusyAgentsAsync(
        ForgeDbContext db, CancellationToken ct)
    {
        var busyList = await db.AgentUsage
            .AsNoTracking()
            .Where(u => u.EndedAt == null)
            .Select(u => u.AgentId)
            .ToListAsync(ct);

        return new HashSet<string>(busyList, StringComparer.Ordinal);
    }

    /// <summary>
    /// Phase 48 follow-up — boot-time reaper. By definition, no agent
    /// invocation can be in flight when the process just started, so
    /// EVERY row with <c>EndedAt = NULL</c> at boot is orphaned. Without
    /// this, fresh-orphaned rows from the most recent crash leave their
    /// agents marked busy and the Phase 47 pair-diversity picker filters
    /// them out — manifesting as plan-doc loops that "freeze" because
    /// no agent is available to dispatch.
    ///
    /// <para>Mechanism: SQL UPDATE setting <c>EndedAt = StartedAt + 1s</c>
    /// (a small delta past StartedAt so chronological order is preserved)
    /// for every open row. Idempotent. Returns the number of rows
    /// closed.</para>
    /// </summary>
    public static async Task<int> ReapAllOpenOnBootAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE agent_usage
               SET EndedAt = strftime('%Y-%m-%dT%H:%M:%fZ', StartedAt, '+1 seconds')
             WHERE EndedAt IS NULL;
            SELECT changes();";
        var changes = (long?)await cmd.ExecuteScalarAsync(ct) ?? 0L;
        return (int)changes;
    }

    /// <summary>
    /// Reap stale <c>agent_usage</c> rows: any row whose
    /// <c>EndedAt</c> is null and whose <c>StartedAt</c> is older than
    /// <see cref="StaleAgentUsageCap"/> gets <c>EndedAt = StartedAt +
    /// StaleAgentUsageCap</c>. Returns the count of reaped rows so the
    /// boot block can emit telemetry.
    /// </summary>
    public static async Task<int> ReapStaleAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        // SQLite EF can't translate `DateTimeOffset` arithmetic in the
        // WHERE clause (`StartedAt < cutoff` produces "could not be
        // translated"), so we hand the date math to SQLite via raw SQL.
        // The column is stored as ISO-8601 text via EF's
        // DateTimeOffset converter; lexicographic compare matches
        // chronological compare for that format.
        var cutoffIso = (DateTimeOffset.UtcNow - StaleAgentUsageCap).ToString("o");
        var capSeconds = (long)StaleAgentUsageCap.TotalSeconds;

        var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Compute the new EndedAt = StartedAt + cap (in seconds) inside
        // SQLite via the `datetime` built-in. Returning ROWID isn't
        // useful here; we want the row count, so we read changes()
        // after the UPDATE.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE agent_usage
               SET EndedAt = strftime('%Y-%m-%dT%H:%M:%fZ', StartedAt, '+' || @cap || ' seconds')
             WHERE EndedAt IS NULL AND StartedAt < @cutoff;
            SELECT changes();";
        cmd.Parameters.AddWithValue("@cutoff", cutoffIso);
        cmd.Parameters.AddWithValue("@cap", capSeconds);
        var changes = (long?)await cmd.ExecuteScalarAsync(ct) ?? 0L;
        return (int)changes;
    }
}
