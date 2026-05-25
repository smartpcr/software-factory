// -----------------------------------------------------------------------
// <copyright file="Phase84Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 84 — adds <c>repos.RunTestsElevated</c>, the per-repo flag that marks
/// a repo whose build/test validation must run with administrator (elevated)
/// privileges. AzureStack repos' tests write HKLM and bind HttpListener URLs,
/// which require elevation; the validation worker inherits elevation from the
/// Forge.Api host (the launcher self-elevates) and fails the run fast with a
/// clear message when the flag is set but the host is not elevated.
/// <b>Additive nullable column; null/false = no elevation requirement, so no
/// behaviour change for existing repos.</b>
///
/// <para><b>Product-only — fans out.</b> Post-Phase-80.B the <c>repos</c> table
/// is a <see cref="ProductDbContext"/> entity living in each per-product
/// <c>{slug}.db</c>, so this is classified <see cref="PhaseMigrationKind.Product"/>
/// in <see cref="PhaseMigrationCatalog"/> and the
/// <see cref="PhaseMigrationRunner"/> applies it to every product DB. All ops
/// target the <c>ProductDbContext</c> connection — the same file the readers
/// (<c>db.Repos</c>, including <see cref="Services.BuildValidationWorker"/>) use.</para>
///
/// <para><b>Idempotent</b> — table-existence + <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>, matching <see cref="Phase83Migration"/>.</para>
/// </summary>
public static class Phase84Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "repos", "RunTestsElevated", "INTEGER NULL", ct);
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
