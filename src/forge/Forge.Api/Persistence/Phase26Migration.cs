// -----------------------------------------------------------------------
// <copyright file="Phase26Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 26 — build/test gate before PR creation.
/// Adds <c>BuildCommand</c>, <c>TestCommand</c>, <c>BuildTimeoutSec</c>, and
/// <c>TestTimeoutSec</c> columns to the <c>repos</c> table so the delivery
/// pipeline can run build + test verification before creating a PR.
/// </summary>
public static class Phase26Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "repos", "BuildCommand", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "repos", "TestCommand", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "repos", "BuildTimeoutSec", "INTEGER NULL", ct);
        await AddColumnIfMissingAsync(conn, "repos", "TestTimeoutSec", "INTEGER NULL", ct);
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
