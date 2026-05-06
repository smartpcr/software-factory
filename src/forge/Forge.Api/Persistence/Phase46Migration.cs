// -----------------------------------------------------------------------
// <copyright file="Phase46Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 46 — auto-merge flag on stories and spawns. When the story's
/// base branch is not <c>main</c>, operators can opt in to automatic PR
/// merge. The flag propagates from <c>ProductStoryRow</c> →
/// <c>StorySpawnRow</c> → <c>PrRow.AutoMerge</c> at PR creation time.
/// Existing rows backfill to <c>false</c> (no behaviour change).
/// </summary>
public static class Phase46Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "product_stories", "AutoMerge", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(conn, "story_spawns", "AutoMerge", "INTEGER NOT NULL DEFAULT 0", ct);
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
