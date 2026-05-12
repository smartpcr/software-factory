// -----------------------------------------------------------------------
// <copyright file="Phase50Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 50 — decision pins on a story spawn. Operators can pin a
/// directive (e.g. "DefaultAction lives in sidecar — stop flipping")
/// that is prepended to every future iter prompt for every plan-doc /
/// workstream under the spawn. Breaks cross-doc convergence loops where
/// the agent re-litigates the same decision because sibling docs change
/// between iters.
///
/// <para>Stored as a JSON array in <see cref="StorySpawnRow.DecisionPinsJson"/>;
/// a single new nullable column on the existing <c>story_spawns</c> table.
/// Existing rows backfill to <c>NULL</c> (no behaviour change).</para>
/// </summary>
public static class Phase50Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "story_spawns", "DecisionPinsJson", "TEXT NULL", ct);
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
