// -----------------------------------------------------------------------
// <copyright file="Phase23Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 23 — adds scheduled work-item sync
/// columns to the <c>repos</c> table and creates the
/// <c>work_item_sync_runs</c> history table.
/// </summary>
public static class Phase23Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // New columns on repos.
        await EnsureColumnAsync(conn, "repos", "AreaPathsJson", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "LastSyncAt", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "LastSyncCursor", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "LastSyncStatus", "TEXT NOT NULL DEFAULT 'idle'", ct);
        await EnsureColumnAsync(conn, "repos", "LastSyncError", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "IsRunningSince", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "AgentReadyDefaultTeam", "TEXT", ct);
        await EnsureColumnAsync(conn, "repos", "InitialSyncWindowDays", "INTEGER", ct);

        // work_item_sync_runs table.
        await EnsureTableAsync(conn, ct);
    }

    private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_item_sync_runs (
                Id TEXT PRIMARY KEY,
                RepoId TEXT NOT NULL,
                Kind TEXT NOT NULL DEFAULT 'delta',
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT,
                ItemsAdded INTEGER NOT NULL DEFAULT 0,
                ItemsUpdated INTEGER NOT NULL DEFAULT 0,
                ItemsConflicted INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT,
                CompletedSuccessfully INTEGER NOT NULL DEFAULT 0
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Index: (RepoId, StartedAt DESC) — the runs endpoint sorts by most recent.
        await using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_work_item_sync_runs_repo_started
            ON work_item_sync_runs (RepoId, StartedAt DESC);
            """;
        await idxCmd.ExecuteNonQueryAsync(ct);
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
