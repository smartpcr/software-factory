// -----------------------------------------------------------------------
// <copyright file="Phase45Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 45 — operator-chosen base branch on a story spawn AND on the
/// authored <c>ProductStory</c>. The story's value is the default carried
/// through to the spawn; the spawn can still override at spawn time.
/// Both tables get the <c>BaseBranch</c> column with default <c>'main'</c>;
/// existing rows backfill to <c>'main'</c> so behavior is unchanged for
/// in-flight rows.
/// </summary>
public static class Phase45Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "story_spawns", "BaseBranch", "TEXT NOT NULL DEFAULT 'main'", ct);
        await EnsureColumnAsync(conn, "product_stories", "BaseBranch", "TEXT NOT NULL DEFAULT 'main'", ct);

        // Backfill — earlier rows added before NOT NULL DEFAULT was honored
        // on column-add land NULL. Cheap one-shot UPDATE per table.
        await using var fixSpawns = conn.CreateCommand();
        fixSpawns.CommandText = "UPDATE story_spawns SET BaseBranch = 'main' WHERE BaseBranch IS NULL OR BaseBranch = ''";
        await fixSpawns.ExecuteNonQueryAsync(ct);

        await using var fixStories = conn.CreateCommand();
        fixStories.CommandText = "UPDATE product_stories SET BaseBranch = 'main' WHERE BaseBranch IS NULL OR BaseBranch = ''";
        await fixStories.ExecuteNonQueryAsync(ct);
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
