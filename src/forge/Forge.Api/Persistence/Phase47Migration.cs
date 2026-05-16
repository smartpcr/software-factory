// -----------------------------------------------------------------------
// <copyright file="Phase47Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Spec Phase 46 — implementation-plan layout v2 + per-stage Dependencies.
/// (Filename retained as <c>Phase47Migration</c> on disk because
/// <c>Phase46Migration.cs</c> is already taken by the AutoMerge flag
/// migration; see the "Spec-vs-migration numbering" note at the end of
/// spec Phase 28.)
///
/// Adds two columns to <c>workstreams</c>:
/// <list type="bullet">
/// <item><c>DepsJson TEXT NULL</c> — JSON <c>string[]</c> of upstream
/// workstream titles (the resolved + phase-expanded dependency set the
/// orchestrator schedules against). Backfilled by walking
/// <c>StorySpawnRow.DecompositionJson</c> once and projecting the
/// <c>deps</c> field per workstream title.</item>
/// <item><c>PhaseAnchor TEXT NULL</c> — cached for cheap UI grouping;
/// matches the <c>phase-{slug}</c> prefix of <see
/// cref="WorkstreamRow.PlanAnchor"/>. Backfilled from PlanAnchor when
/// present.</item>
/// </list>
///
/// Idempotent — checks <c>pragma_table_info</c> before each ALTER. Runs
/// after <c>Phase46Migration</c> (AutoMerge) in the boot chain.
/// </summary>
public static class Phase47Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var addedDeps = await EnsureColumnAsync(conn, "workstreams", "DepsJson", "TEXT NULL", ct);
        var addedAnchor = await EnsureColumnAsync(conn, "workstreams", "PhaseAnchor", "TEXT NULL", ct);

        if (addedDeps)
        {
            await BackfillDepsJsonAsync(conn, ct);
        }
        if (addedAnchor)
        {
            await BackfillPhaseAnchorAsync(conn, ct);
        }
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

    /// <summary>
    /// Walk <c>story_spawns.DecompositionJson</c> once and project each
    /// workstream's <c>deps</c> array onto the matching
    /// <c>workstreams.DepsJson</c> row. Best-effort: rows that don't match
    /// (decomposition title without a workstream row, or vice-versa) are
    /// skipped silently — the reconciler corrects on the next plan tick.
    /// </summary>
    private static async Task BackfillDepsJsonAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Pull spawn id + decomposition JSON. We read every spawn that
        // has a decomposition; null / empty rows are skipped.
        var spawnRows = new List<(string SpawnId, string? Json)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT Id, DecompositionJson FROM story_spawns WHERE DecompositionJson IS NOT NULL";
            await using var rdr = await read.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                spawnRows.Add((rdr.GetString(0), rdr.IsDBNull(1) ? null : rdr.GetString(1)));
            }
        }

        foreach (var (spawnId, json) in spawnRows)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            List<DecompositionLite>? rows;
            try
            {
                rows = JsonSerializer.Deserialize<List<DecompositionLite>>(json, JsonOpts);
            }
            catch (JsonException)
            {
                continue;
            }
            if (rows is null) continue;

            // Title -> deps; the workstream row's Name matches the
            // architect's title verbatim (the orchestrator passes Title
            // straight through to WorkstreamRow.Name).
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Title) || row.Deps is null || row.Deps.Count == 0) continue;
                var depsJson = JsonSerializer.Serialize(row.Deps);

                await using var upd = conn.CreateCommand();
                upd.CommandText = @"
                    UPDATE workstreams
                       SET DepsJson = @deps
                     WHERE StorySpawnId = @spawn AND Name = @name AND (DepsJson IS NULL OR DepsJson = '')";
                upd.Parameters.AddWithValue("@deps", depsJson);
                upd.Parameters.AddWithValue("@spawn", spawnId);
                upd.Parameters.AddWithValue("@name", row.Title);
                await upd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>
    /// Backfill <c>workstreams.PhaseAnchor</c> from <c>PlanAnchor</c>'s
    /// <c>phase-{slug}/stage-{slug}</c> prefix. Rows without a PlanAnchor
    /// are left null so the UI falls through to the existing label.
    /// </summary>
    private static async Task BackfillPhaseAnchorAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // SQLite substr indexing is 1-based; instr returns 0 when the
        // needle isn't found. We split on the first '/'; rows whose
        // PlanAnchor has no '/' (a malformed anchor) get the whole
        // string copied — which is a phase anchor already, so it's safe.
        cmd.CommandText = @"
            UPDATE workstreams
               SET PhaseAnchor = CASE
                   WHEN instr(PlanAnchor, '/') > 0
                       THEN substr(PlanAnchor, 1, instr(PlanAnchor, '/') - 1)
                   ELSE PlanAnchor
               END
             WHERE PlanAnchor IS NOT NULL AND PhaseAnchor IS NULL";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Migration-private DTO matching the existing decomposition JSON shape (subset: title + deps).</summary>
    private sealed class DecompositionLite
    {
        public string? Title { get; set; }

        public List<string>? Deps { get; set; }
    }
}
