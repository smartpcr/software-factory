// -----------------------------------------------------------------------
// <copyright file="Phase92Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 92 — adds <c>workstreams.LastBuildGate</c> and
/// <c>workstreams.LastTestGate</c>, the durable mirror of the latest build /
/// test gate outcome (<c>passed</c> | <c>failed</c>) written by
/// <see cref="Services.Stories.CrewStoryArtifactStrategy.PersistGateOutcomeAsync"/>.
/// The per-iter <c>task.{build,test}-gate.*</c> transcript events are pruned
/// for older / completed workstreams, so the swarm phase-board BUILD / TEST
/// badges read these columns instead and survive eviction.
///
/// <para><b>Backfill:</b> nullable columns; existing rows stay NULL until the
/// next gate runs for that workstream (completed workstreams show no badge
/// until then — acceptable, the gate ran before this column existed).</para>
///
/// <para><b>Product-only — fans out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every per-product
/// <c>{slug}.db</c> as well as the default <c>forge.db</c> (the
/// <c>workstreams</c> table lives in product DBs post-split).</para>
///
/// <para><b>Idempotent</b> — <c>pragma_table_info</c>-guarded
/// <c>ALTER TABLE</c>, matching <see cref="Phase91Migration"/>.</para>
/// </summary>
public static class Phase92Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "workstreams", "LastBuildGate", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "workstreams", "LastTestGate", "TEXT NULL", ct);

        // One-time backfill from surviving transcript gate events so workstreams
        // that already ran a gate keep their badge immediately after deploy
        // (rather than going blank until the next gate writes the column). Reads
        // the most recent passed/failed gate event per workstream — skipped /
        // resolving / resolved are ignored, matching PersistGateOutcomeAsync.
        // Workstreams whose gate events were already evicted stay NULL (the data
        // is gone; only future gates can populate them). Idempotent via
        // `LastBuildGate IS NULL`.
        await BackfillGateAsync(conn, "LastBuildGate", "task.build-gate.passed", "task.build-gate.failed", ct);
        await BackfillGateAsync(conn, "LastTestGate", "task.test-gate.passed", "task.test-gate.failed", ct);
    }

    private static async Task BackfillGateAsync(
        SqliteConnection conn, string column, string passedKind, string failedKind, CancellationToken ct)
    {
        // Both tables must exist (table-probe guards a brand-new / partial DB).
        foreach (var table in new[] { "workstreams", "task_transcript_event" })
        {
            await using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = @t";
            probe.Parameters.AddWithValue("@t", table);
            if (await probe.ExecuteScalarAsync(ct) is null) return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
UPDATE workstreams SET {column} = (
    SELECT CASE WHEN e.Kind = @passed THEN 'passed' ELSE 'failed' END
    FROM task_transcript_event e
    WHERE e.WorkItemId = workstreams.Id AND e.Kind IN (@passed, @failed)
    ORDER BY e.Id DESC LIMIT 1)
WHERE {column} IS NULL
  AND EXISTS (
    SELECT 1 FROM task_transcript_event e2
    WHERE e2.WorkItemId = workstreams.Id AND e2.Kind IN (@passed, @failed));";
        cmd.Parameters.AddWithValue("@passed", passedKind);
        cmd.Parameters.AddWithValue("@failed", failedKind);
        await cmd.ExecuteNonQueryAsync(ct);
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
