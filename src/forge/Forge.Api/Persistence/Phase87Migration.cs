// -----------------------------------------------------------------------
// <copyright file="Phase87Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 87 — adds <c>deliverables.PairAttempt</c>, the per-row 1-based
/// counter mirroring <c>WorkItem.PairAttemptCount</c> at deliverable-write
/// time. Disambiguates rows from different generator/evaluator pair
/// attempts within the same workstream.
///
/// <para><b>Why:</b> the <c>Iter</c> column resets when an operator-Retry
/// (or any SoftResetForResumeAsync path) wipes pair-attempt accounting and
/// the orchestrator assigns a fresh pair. The resumed loop's first
/// iteration writes <c>Iter=1</c> again, producing duplicate <c>(WorkItemId, Iter)</c>
/// rows that the Conversation-tab iter-KPI strip can't visually distinguish.
/// Stamping <c>PairAttempt</c> lets the UI render a pair-boundary divider
/// between consecutive attempts and label each chip with its real (pair,
/// iter) tuple instead of a misleading bare iter number.</para>
///
/// <para><b>Backfill:</b> nullable column; existing rows stay NULL. The
/// UI treats null as pair 1 (the pre-Phase-87 baseline) so legacy
/// workstreams render unchanged. Going forward,
/// <see cref="Services.DeliverableLoop"/> populates the column from
/// <c>workItemRow.PairAttemptCount</c> at every <c>Deliverables.Add</c>
/// site.</para>
///
/// <para><b>Product-only — fans out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every per-product
/// <c>{slug}.db</c> as well as the default <c>forge.db</c>. All ops target
/// the <see cref="ProductDbContext"/> connection (the deliverables table
/// lives in product DBs post-Phase-73 split).</para>
///
/// <para><b>Idempotent</b> — <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>, matching <see cref="Phase64Migration"/> /
/// <see cref="Phase65Migration"/>.</para>
/// </summary>
public static class Phase87Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "deliverables", "PairAttempt", "INTEGER NULL", ct);
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
