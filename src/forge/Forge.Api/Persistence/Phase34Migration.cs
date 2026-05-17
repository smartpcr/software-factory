// -----------------------------------------------------------------------
// <copyright file="Phase34Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade — per-repo sync cadence overrides + upstream-changedate cursor.
/// Backs the sync-strategy refactor that bumps the global cadence to 2 hr and switches the
/// delta cursor from local clock (<c>LastSyncAt</c>) to the upstream <c>System.ChangedDate</c> /
/// <c>updated_at</c> max observed in the last successful run.
/// </summary>
public static class Phase34Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Per-repo cadence overrides (null = inherit global SyncConstants).
        await AddColumnIfMissingAsync(conn, "repos", "WorkItemSyncIntervalSeconds", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "repos", "PrSyncIntervalSeconds", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "repos", "BuildSyncIntervalSeconds", "INTEGER", ct);

        // Upstream-changedate cursor — separate from LastSyncAt so the audit timestamp
        // and the delta-query boundary aren't tangled together.
        await AddColumnIfMissingAsync(conn, "repos", "LastUpstreamChangedDate", "TEXT", ct);
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
