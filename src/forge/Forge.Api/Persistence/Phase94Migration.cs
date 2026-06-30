// -----------------------------------------------------------------------
// <copyright file="Phase94Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 94 — adds <c>repos.E2eUseBddPattern</c>, the per-repo override for the
/// qa-e2e test STYLE. <c>null</c> = inherit the global
/// <c>Forge:Stories:E2eUseBddPattern</c> default (which defaults to true =
/// BDD/Gherkin); <c>1</c> = force BDD/Gherkin (a <c>.feature</c> file + step
/// bindings); <c>0</c> = plain idiomatic test methods in the repo's existing
/// unit-test framework, no <c>.feature</c> file. Drives
/// <see cref="Forge.Api.Services.Stories.CrewStoryArtifactStrategy"/>'s qa-e2e
/// generator-prompt selection. <b>Additive nullable column; null = inherit, so
/// no behaviour change for existing repos.</b>
///
/// <para><b>Product-only — fans out.</b> Post-Phase-80.B the <c>repos</c> table
/// is a <see cref="ProductDbContext"/> entity living in each per-product
/// <c>{slug}.db</c>, so this is classified <see cref="PhaseMigrationKind.Product"/>
/// in <see cref="PhaseMigrationCatalog"/> and the
/// <see cref="PhaseMigrationRunner"/> applies it to every product DB. All ops
/// target the <c>ProductDbContext</c> connection — the same file the readers
/// (<c>db.Repos</c>) use. Mirrors <see cref="Phase84Migration"/>.</para>
///
/// <para><b>Idempotent</b> — table-existence + <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>.</para>
/// </summary>
public static class Phase94Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = control;
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "repos", "E2eUseBddPattern", "INTEGER NULL", ct);
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
