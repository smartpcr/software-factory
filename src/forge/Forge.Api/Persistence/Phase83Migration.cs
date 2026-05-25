// -----------------------------------------------------------------------
// <copyright file="Phase83Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 83 — adds <c>repos.PlanDocBaseDir</c>, the per-repo override for
/// where story plan docs land (default <c>docs/stories</c>). Repos whose
/// <c>.gitignore</c> is an allowlist (OneBranch / SDL: <c>/*</c> + <c>!/src/</c>)
/// set this to e.g. <c>src/docs/stories</c> so the Phase 81 commit guard
/// (<see cref="Services.Stories.GitIgnoredPathException"/>) doesn't refuse to
/// ship docs that would be silently git-ignored. <b>Additive nullable column;
/// no behavior change for repos that leave it null.</b>
///
/// <para><b>Product-only — fans out.</b> Post-Phase-80.B the <c>repos</c> table
/// is a <see cref="ProductDbContext"/> entity living in each per-product
/// <c>{slug}.db</c>, so this is classified <see cref="PhaseMigrationKind.Product"/>
/// in <see cref="PhaseMigrationCatalog"/> and the
/// <see cref="PhaseMigrationRunner"/> applies it to every product DB. All ops
/// target the <c>ProductDbContext</c> connection — the same file the readers
/// (<c>db.Repos</c>) use.</para>
///
/// <para><b>Idempotent</b> — table-existence + <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>. The table guard matters because the Phase 80.B repo move
/// is marker-gated: a <c>{slug}.db</c> that hasn't received its repos table yet
/// is skipped (a later <c>EnsureCreated</c> builds the table with the column
/// already in the EF model).</para>
/// </summary>
public static class Phase83Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "repos", "PlanDocBaseDir", "TEXT NULL", ct);
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> when both the
    /// table exists and the column is absent. Idempotent / safe on a DB whose repos
    /// table hasn't been created yet.</summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using (var tableProbe = conn.CreateCommand())
        {
            tableProbe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = @t";
            tableProbe.Parameters.AddWithValue("@t", table);
            if (await tableProbe.ExecuteScalarAsync(ct) is null) return;
        }

        await using (var columnProbe = conn.CreateCommand())
        {
            columnProbe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
            columnProbe.Parameters.AddWithValue("@name", column);
            if (await columnProbe.ExecuteScalarAsync(ct) is not null) return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
