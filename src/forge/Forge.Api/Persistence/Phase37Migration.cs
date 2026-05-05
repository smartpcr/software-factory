// -----------------------------------------------------------------------
// <copyright file="Phase37Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Add LastError + LastErrorAt columns to story_spawns so the orchestrator
/// can persist the reason a spawn stopped advancing (architect crew below
/// pass, plan PR merge conflict, decomposition empty, etc.) and the
/// workstream UI can surface it as a banner with a Retry action.
/// </summary>
public static class Phase37Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "story_spawns", "LastError", "TEXT", ct);
        await EnsureColumnAsync(conn, "story_spawns", "LastErrorAt", "TEXT", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
