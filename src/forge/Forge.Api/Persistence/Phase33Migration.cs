// -----------------------------------------------------------------------
// <copyright file="Phase33Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade — persisted sync settings on repos.
/// Adds columns for WI sync defaults, PR sync defaults, and build pipeline IDs.
/// </summary>
public static class Phase33Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // WI sync defaults
        await AddColumnIfMissingAsync(conn, "repos", "WiSyncAssignedTo", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "repos", "WiSyncDateFromDays", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "repos", "WiSyncFromDate", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "repos", "WiSyncToDate", "TEXT", ct);

        // PR sync defaults
        await AddColumnIfMissingAsync(conn, "repos", "PrSyncCreatedBy", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "repos", "PrSyncStatus", "TEXT", ct);

        // Build pipeline definition IDs (JSON array of strings)
        await AddColumnIfMissingAsync(conn, "repos", "BuildPipelineIdsJson", "TEXT", ct);
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
