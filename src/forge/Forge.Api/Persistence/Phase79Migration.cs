// -----------------------------------------------------------------------
// <copyright file="Phase79Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// docs/forge/e2e.md Stage 1.1 — adds the <c>Category</c> discriminator
/// to the <c>workstreams</c> table so the orchestrator can route
/// per-stage QA workstreams (Gherkin + step-defs + e2e run) through the
/// implement driver as trailing rows on each Stage, instead of clustering
/// them in a separate "Phase 5 QA" pass at the end of the spawn.
///
/// <para>Column shape:</para>
/// <list type="bullet">
/// <item><c>Category TEXT NOT NULL DEFAULT 'impl'</c> — one of
/// <c>"impl"</c> (normal Stage implementation, the default) or
/// <c>"qa-e2e"</c> (trailing per-stage QA workstream emitted by the
/// decomposer for any Stage whose <c>### Test Scenarios</c> block has
/// at least one entry).</item>
/// </list>
///
/// <para>Pre-existing rows backfill to <c>'impl'</c> via the SQL
/// <c>DEFAULT</c> clause (SQLite applies the default to existing rows
/// when adding a NOT NULL column with a default literal). New rows
/// produced by <c>CrewStoryArtifactStrategy.ProduceDecompositionAsync</c>
/// set the value explicitly.</para>
///
/// <para>A composite index <c>IX_workstreams_StorySpawnId_Category</c>
/// is also created so the swarm-control DAG-render query (which fetches
/// rows scoped to a single spawn and groups by Category to render
/// stacked impl/qa-e2e pairs) hits an index instead of a table scan.
/// Index creation is <c>IF NOT EXISTS</c>-guarded so re-runs are cheap.</para>
///
/// <para>Idempotent — checks <c>pragma_table_info</c> before the ALTER
/// and uses <c>CREATE INDEX IF NOT EXISTS</c> for the index. Safe to
/// re-run on an upgraded DB.</para>
/// </summary>
public static class Phase79Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "workstreams", "Category", "TEXT NOT NULL DEFAULT 'impl'", ct);

        // Composite index for the DAG-render query. EF's HasIndex emits the
        // same shape via EnsureCreated on a fresh DB; this CREATE INDEX
        // ensures upgraded DBs also get the index without waiting for an
        // EnsureCreated pass (Forge boot path doesn't run EnsureCreated on
        // an already-populated DB).
        await using var idx = conn.CreateCommand();
        idx.CommandText =
            "CREATE INDEX IF NOT EXISTS IX_workstreams_StorySpawnId_Category " +
            "ON workstreams (StorySpawnId, Category)";
        await idx.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns <c>true</c> when the column was added, <c>false</c> when it
    /// already existed (re-run on an upgraded DB).
    /// </summary>
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
