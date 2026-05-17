// -----------------------------------------------------------------------
// <copyright file="Phase21Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 21 — adds <c>SkipSoulPrompt</c>
/// (INTEGER/bool, default 0) to the <c>task_queue</c> table so force-
/// restart can suppress soul.md loading for generator and evaluator.
/// </summary>
public static class Phase21Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "task_queue", "SkipSoulPrompt", "INTEGER NOT NULL DEFAULT 0", ct);
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
