// -----------------------------------------------------------------------
// <copyright file="Phase97Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 (chat bridge, Part B) — control-side <c>chat_spawn_topics</c>
/// (spawn → forum-topic mapping) + <c>chat_notifications.SpawnId</c>.
/// <b>Additive only.</b> Control-only; idempotent
/// (<c>CREATE TABLE IF NOT EXISTS</c> + <c>pragma_table_info</c>-guarded ALTER).
/// </summary>
public static class Phase97Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS chat_spawn_topics (
                SpawnId TEXT NOT NULL PRIMARY KEY,
                ProductId TEXT NOT NULL DEFAULT '',
                ChatId TEXT NOT NULL DEFAULT '',
                ThreadId TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT '',
                ClosedAt TEXT NULL
            );
            """, ct);

        await EnsureColumnAsync(conn, "chat_notifications", "SpawnId", "TEXT NULL", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
            probe.Parameters.AddWithValue("@name", column);
            if (await probe.ExecuteScalarAsync(ct) is not null) return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
