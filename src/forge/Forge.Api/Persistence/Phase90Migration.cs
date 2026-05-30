// -----------------------------------------------------------------------
// <copyright file="Phase90Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 90 — adds <c>deliverables.ReasoningText</c>, the generator model's
/// reasoning ("thinking") text per iteration, captured from the Copilot SDK's
/// <c>AssistantReasoningDeltaEvent</c> stream by
/// <see cref="Services.DeliverableLoop"/> and surfaced per-iteration in the
/// Conversation Summary tab next to the generator/evaluator summaries.
///
/// <para><b>Backfill:</b> nullable column; existing rows stay NULL (the
/// reasoning stream wasn't captured before this phase). The Summary view
/// renders the section only when the column is non-empty.</para>
///
/// <para><b>Product-only — fans out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every per-product
/// <c>{slug}.db</c> as well as the default <c>forge.db</c> (the deliverables
/// table lives in product DBs post-Phase-73 split).</para>
///
/// <para><b>Idempotent</b> — <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>, matching <see cref="Phase87Migration"/>.</para>
/// </summary>
public static class Phase90Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "deliverables", "ReasoningText", "TEXT NULL", ct);
    }

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
