// -----------------------------------------------------------------------
// <copyright file="Phase48Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Spec Phase 47 — pair-diversity for parallel deliverables.
/// (Filename retained as <c>Phase48Migration</c> because <c>Phase47Migration</c>
/// is taken by Phase 46's <c>workstreams.DepsJson</c> + <c>PhaseAnchor</c>
/// migration; see the "Spec-vs-migration numbering" note at the end of
/// spec Phase 28.)
///
/// Adds one column:
/// <list type="bullet">
/// <item><c>task_queue.RoutedEvaluatorAgentId TEXT NULL</c> — agent the
/// worker resolved as the evaluator for this row at claim time. Read by
/// <c>CrewQueueWorker.RunAgentAsync</c> so the deliverable loop scores
/// against the diverse evaluator that was advertised in
/// <c>WorkItemRow.AssignedAgentJson</c> instead of falling back to
/// <c>CrewRow.DefaultEvaluatorAgentId</c>.</item>
/// </list>
///
/// Idempotent — checks <c>pragma_table_info</c> before the ALTER. Runs
/// after <c>Phase47Migration</c> (workstream deps) in the boot chain.
/// Backfill: existing in-flight rows leave the column null and the
/// resolver falls back to the legacy DefaultEvaluatorAgentId path so
/// running deliverables don't see a behaviour change mid-flight.
/// </summary>
public static class Phase48Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "task_queue", "RoutedEvaluatorAgentId", "TEXT NULL", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
