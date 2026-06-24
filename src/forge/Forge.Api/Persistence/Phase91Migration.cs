// -----------------------------------------------------------------------
// <copyright file="Phase91Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 91 — adds <c>deliverable_questions.ExplanationJson</c>, the
/// operator-facing rationale for an open question (terms/context, per-choice
/// logic, and the agent's recommendation). Primarily agent-authored — parsed
/// from the generator's <c>```json open-questions</c> block by
/// <see cref="Services.OpenQuestionsExtractor"/> — with on-demand LLM
/// back-fill via <c>POST /api/work-items/{id}/questions/{externalId}/explain</c>
/// for questions emitted before agents carried it. Powers the Questions tab's
/// "Explain" popup (<c>OpenQuestionsWizard</c>).
///
/// <para><b>Backfill:</b> nullable column; existing rows stay NULL (no
/// explanation captured before this phase). The Explain button generates one
/// on first click for those rows.</para>
///
/// <para><b>Product-only — fans out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every per-product
/// <c>{slug}.db</c> as well as the default <c>forge.db</c> (the
/// deliverable_questions table lives in product DBs post-Phase-73 split).</para>
///
/// <para><b>Idempotent</b> — <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>, matching <see cref="Phase90Migration"/>.</para>
/// </summary>
public static class Phase91Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "deliverable_questions", "ExplanationJson", "TEXT NULL", ct);
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
