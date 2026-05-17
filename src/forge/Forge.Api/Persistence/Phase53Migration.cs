// -----------------------------------------------------------------------
// <copyright file="Phase53Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 53 — sequential plan-doc tasks.
///
/// <para>The architect crew used to produce all four plan docs
/// (architecture / tech-spec / implementation-plan / e2e-scenarios) in
/// parallel via <c>Task.WhenAll</c> onto one branch (<c>story/{id}/plan</c>)
/// and one PR (<c>spawn.PlanPrId</c>). Phase 53 switches that to strict
/// sequencing: each doc has its own branch
/// (<c>story/{id}/plan-{key}</c>), its own worktree
/// (<c>.worktree/plan-{key}</c>), and its own PR; doc N+1's gen↔eval loop
/// only starts after doc N's PR is merged and its latest deliverable
/// verdict is <c>pass</c>.</para>
///
/// <para>Adds one nullable column to <c>story_spawns</c>:</para>
/// <list type="bullet">
/// <item><c>PlanDocPrsJson TEXT NULL</c> — ordered JSON array of
/// per-doc state: <c>[{ "key": "...", "branch": "...", "prId": "..." | null }, ...]</c>.
/// <c>null</c> on legacy spawns (created before Phase 53 shipped); those
/// rows continue to drive the single-PR <c>PlanPrId</c> path. New rows
/// initialise the column to <c>"[]"</c> at spawn-create time, marking
/// them as sequential-mode.</item>
/// </list>
///
/// <para>Idempotent — checks <c>pragma_table_info</c> before the ALTER.</para>
/// </summary>
public static class Phase53Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "story_spawns", "PlanDocPrsJson", "TEXT NULL", ct);
    }

    private static async Task<bool> EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }
}
