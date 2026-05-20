// -----------------------------------------------------------------------
// <copyright file="Phase81Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 80 (Stage 80.A) — product-side schema for the repo refactor.
/// <b>Additive only; no behavior change.</b> Adds the
/// <c>product_stories.RepoId</c> column so a story can bind to one repo
/// (a product owns many; Stage 80.F makes it required + backfills legacy
/// nulls to <see cref="ProductRow.DefaultRepoId"/>). Nothing reads the column
/// in Stage 80.A — the EF model just maps it.
///
/// <para><b>Product-only — must fan out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to <i>every</i> per-product
/// <c>{slug}.db</c> (post-Phase-F) as well as the default <c>forge.db</c>
/// (pre-Phase-F). A <see cref="PhaseMigrationKind.Mixed"/> classification would
/// be wrong here: the runner deliberately skips fan-out for Mixed, so existing
/// split <c>{slug}.db</c> files would never get the new column. All ops target
/// the <c>ProductDbContext</c> connection.</para>
///
/// <para><b>Idempotent</b> — <c>pragma_table_info</c>-guarded <c>ALTER TABLE</c>.</para>
/// </summary>
public static class Phase81Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "product_stories", "RepoId", "TEXT NULL", ct);
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> when absent. Idempotent.</summary>
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
