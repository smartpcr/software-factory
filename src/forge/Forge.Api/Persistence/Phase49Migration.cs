// -----------------------------------------------------------------------
// <copyright file="Phase49Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Spec Phase 49 — split the overloaded <c>workstreams.Status="blocked"</c>
/// triage state into a dedicated <c>"stalled"</c> failure-triage state
/// (build-gate failure, no-progress abort, non-pass terminal verdict) so
/// the UI can distinguish "waiting on upstream dependencies" (still
/// surfaced as the existing blocked / planning visual) from "needs
/// operator attention because something went wrong" (the new stalled
/// badge with reason tooltip).
///
/// <para>Adds one nullable column to <c>workstreams</c>:
/// <list type="bullet">
/// <item><c>StallReason TEXT NULL</c> — human-readable description of
/// why the row entered <c>stalled</c>. Set by
/// <c>StorySpawnOrchestrator</c> at each failure-triage transition,
/// cleared by the <c>Restart</c> endpoint when the row flips back to
/// <c>active</c>. Pre-existing rows are left at NULL; if any were
/// stuck at <c>Status="blocked"</c> from an older build, the operator
/// just clicks Restart and they pick up the new vocabulary.</item>
/// </list></para>
///
/// <para>Idempotent — checks <c>pragma_table_info</c> before the ALTER.
/// Runs after <c>Phase48Migration</c> in the boot chain.</para>
/// </summary>
public static class Phase49Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "workstreams", "StallReason", "TEXT NULL", ct);
    }

    /// <summary>
    /// Returns <c>true</c> when the column was added, <c>false</c> when it
    /// already existed (re-run on an upgraded DB).
    /// </summary>
    private static async Task<bool> EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }
}
