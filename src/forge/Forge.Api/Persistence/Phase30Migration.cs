// -----------------------------------------------------------------------
// <copyright file="Phase30Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 24 Q24.14 — pin the original
/// generator agent on a comment-response queue row. The Sweep 2 picker
/// reads this column before round-robin so continuity wins over load
/// balancing for fix work. Also carries the consecutive-stall counter
/// for Q24.13's livelock guard so a fresh enqueue can refuse the row
/// after three stalled deliverables on the same comment set.
/// </summary>
public static class Phase30Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "task_queue", "PreferredAgentId", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "task_queue", "PinReason", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "task_queue", "LastError", "TEXT NULL", ct);

        // Livelock guard counter — incremented per consecutive stalled
        // comment-response deliverable on the same work item; reset to 0
        // on any non-stalled outcome (pass / iterate / reject).
        await AddColumnIfMissingAsync(conn, "work_items", "CommentResponseStallCount", "INTEGER NOT NULL DEFAULT 0", ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alterCmd.ExecuteNonQueryAsync(ct);
    }
}
