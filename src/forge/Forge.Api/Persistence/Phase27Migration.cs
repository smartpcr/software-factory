// -----------------------------------------------------------------------
// <copyright file="Phase27Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 27 — PR detail enrichment.
/// Adds <c>Description</c>, <c>WebUrl</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>
/// columns to the <c>prs</c> table so the PR detail panel can display
/// real data from ADO/GitHub instead of placeholders.
/// </summary>
public static class Phase27Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "prs", "Description", "TEXT NOT NULL DEFAULT ''", ct);
        await AddColumnIfMissingAsync(conn, "prs", "WebUrl", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "prs", "CreatedAt", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "prs", "UpdatedAt", "TEXT NULL", ct);
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
