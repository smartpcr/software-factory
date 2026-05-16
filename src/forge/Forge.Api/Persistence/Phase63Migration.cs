// -----------------------------------------------------------------------
// <copyright file="Phase63Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 63 — adds spawn-level pause / resume support.
///
/// <list type="bullet">
/// <item><c>story_spawns.PausedFromPhase</c> — stores the phase the spawn
/// was in before the operator paused it. Null unless Phase='paused'.
/// The resume endpoint restores Phase to this value and clears the field.</item>
/// <item><c>workstreams.PausedFromStatus</c> — stores the status the
/// workstream was in before the spawn-level pause. Null unless
/// Status='paused'. The resume endpoint restores Status to this value
/// and clears the field.</item>
/// </list>
///
/// <para>Both columns are idempotent: the <c>ALTER TABLE … ADD COLUMN</c>
/// is wrapped in a column-existence check so re-running on an
/// already-migrated database is a no-op.</para>
/// </summary>
public static class Phase63Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "story_spawns", "PausedFromPhase", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "workstreams", "PausedFromStatus", "TEXT", ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alter.ExecuteNonQueryAsync(ct);
    }
}
