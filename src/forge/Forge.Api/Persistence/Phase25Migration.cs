// -----------------------------------------------------------------------
// <copyright file="Phase25Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 25 — sync run item details.
/// Creates <c>sync_run_items</c> table so the sync history drawer can
/// show exactly which work items / PRs were created, updated, or removed
/// in each sync run.
/// </summary>
public static class Phase25Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS sync_run_items (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId        TEXT    NOT NULL,
                EntityKind   TEXT    NOT NULL DEFAULT 'workitem',
                EntityId     TEXT    NOT NULL,
                Title        TEXT    NOT NULL DEFAULT '',
                Action       TEXT    NOT NULL DEFAULT 'updated',
                ProviderRef  INTEGER NULL
            )", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS ix_sync_run_items_run
            ON sync_run_items (RunId)", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
