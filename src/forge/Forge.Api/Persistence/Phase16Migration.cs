// -----------------------------------------------------------------------
// <copyright file="Phase16Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 16 — crew execution &amp; live
/// observability. Adds the pair columns to <c>crews</c>, the auto-dispatch
/// + step-budget columns to <c>work_items</c>, the <c>cost_category</c>
/// column to <c>agent_usage</c>, and the new <c>task_transcript_event</c>
/// table. Runs after <see cref="Phase15Migration"/>; SQLite-only.
/// </summary>
public static class Phase16Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Phase 16.2 — pair columns on crews.
        await EnsureColumnAsync(conn, "crews", "PairRole",         "TEXT NOT NULL DEFAULT 'standalone'", ct);
        await EnsureColumnAsync(conn, "crews", "PairedWithCrewId", "TEXT NULL",                          ct);
        // The "at most one pair per crew" invariant in SQLite is best modelled
        // as a partial unique index. EF doesn't surface this portably, so we
        // create it directly. The WHERE clause is the SQLite syntax.
        await ExecAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_crews_PairedWith
            ON crews(PairedWithCrewId)
            WHERE PairedWithCrewId IS NOT NULL;
            """, ct);

        // Phase 16.1 — auto-dispatch + step-budget columns on work_items.
        await EnsureColumnAsync(conn, "work_items", "AutoDispatchedAt",     "TEXT NULL",    ct);
        await EnsureColumnAsync(conn, "work_items", "ExpectedFileChanges",  "INTEGER NULL", ct);

        // Phase 16.1 — cost-category split on agent_usage. Backfill existing
        // rows to 'generator' so the historical view stays accurate.
        if (!await ColumnExistsAsync(conn, "agent_usage", "CostCategory", ct))
        {
            await ExecAsync(conn, "ALTER TABLE agent_usage ADD COLUMN CostCategory TEXT NOT NULL DEFAULT 'generator';", ct);
            // ALTER ... ADD COLUMN with DEFAULT already populates existing
            // rows; the explicit UPDATE is a belt-and-braces no-op.
            await ExecAsync(conn, "UPDATE agent_usage SET CostCategory = 'generator' WHERE CostCategory IS NULL OR CostCategory = '';", ct);
        }

        // Phase 16.5 — persisted live-execution events.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS task_transcript_event (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                AgentId TEXT NULL,
                CrewId TEXT NULL,
                PayloadJson TEXT NOT NULL DEFAULT '{}',
                At TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_transcript_event_WorkItemId_At ON task_transcript_event(WorkItemId, At);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_transcript_event_WorkItemId ON task_transcript_event(WorkItemId);", ct);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeDecl, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return;
        await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
