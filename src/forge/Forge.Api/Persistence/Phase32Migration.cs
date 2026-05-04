// -----------------------------------------------------------------------
// <copyright file="Phase32Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for forge_implementation.md Phase 28 — story-driven
/// spawn / multi-team orchestrated delivery. The file is named "Phase32"
/// because the previous Phase28..Phase31 migration slots are already taken
/// by unrelated additive migrations (PR diff stats, init/restore commands,
/// preferred-agent pin, build-sync). The Phase 28 spec is implemented here
/// as the next link in the migration chain (after Phase31Migration).
/// <para>
/// Adds the story-spawn surface:
/// <list type="bullet">
/// <item><c>story_spawns</c> — one row per spawned story; tracks the lifecycle phase, planning + QA branches, and decomposition JSON.</item>
/// <item><c>workstream_deps</c> — one row per inter-workstream dependency edge; composite PK (WorkstreamId, DependsOnId).</item>
/// <item><c>workstreams.StorySpawnId</c> — nullable back-pointer column on the existing workstreams table.</item>
/// </list>
/// </para>
/// </summary>
public static class Phase32Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // -- story_spawns table (28.1) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS story_spawns (
                Id                    TEXT    NOT NULL PRIMARY KEY,
                StoryId               TEXT    NOT NULL,
                StoryTitle            TEXT    NOT NULL DEFAULT '',
                RepoId                TEXT    NULL,
                Phase                 TEXT    NOT NULL DEFAULT 'plan',
                PlanBranch            TEXT    NULL,
                PlanPrId              TEXT    NULL,
                DecompositionJson     TEXT    NULL,
                DecompositionApproved INTEGER NOT NULL DEFAULT 0,
                QaBranch              TEXT    NULL,
                QaPrId                TEXT    NULL,
                CreatedAt             TEXT    NOT NULL,
                UpdatedAt             TEXT    NOT NULL
            );", ct);

        // 28.1 — duplicate spawn for the same story is rejected (409).
        await ExecAsync(conn, @"
            CREATE UNIQUE INDEX IF NOT EXISTS UX_story_spawns_StoryId
            ON story_spawns (StoryId);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_story_spawns_Phase
            ON story_spawns (Phase);", ct);

        // -- workstream_deps table (28.11) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS workstream_deps (
                WorkstreamId TEXT NOT NULL,
                DependsOnId  TEXT NOT NULL,
                CreatedAt    TEXT NOT NULL,
                PRIMARY KEY (WorkstreamId, DependsOnId)
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_workstream_deps_DependsOnId
            ON workstream_deps (DependsOnId);", ct);

        // -- workstreams.StorySpawnId additive column (idempotent) --
        await AddColumnIfMissingAsync(conn, "workstreams", "StorySpawnId", "TEXT NULL", ct);
        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_workstreams_StorySpawnId
            ON workstreams (StorySpawnId);", ct);

        // Story description (architect-agent prompt) and points carried over from
        // the originating ProductStory or upstream work item.
        await AddColumnIfMissingAsync(conn, "story_spawns", "StoryDescription", "TEXT NOT NULL DEFAULT ''", ct);
        await AddColumnIfMissingAsync(conn, "story_spawns", "StoryPoints", "INTEGER NOT NULL DEFAULT 0", ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alterCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
