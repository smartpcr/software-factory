// -----------------------------------------------------------------------
// <copyright file="Phase31Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for forge_implementation.md Phase 27 — build sync
/// &amp; failure-driven repair loop. The file is named "Phase31" because the
/// previous Phase27Migration through Phase30Migration slots are already taken
/// by unrelated PR-detail / validation-run / preferred-agent additive
/// migrations; the Phase 27 migration in the spec is implemented here as
/// the next link in the migration chain (after Phase30Migration).
/// <para>
/// Adds the build-sync surface:
/// <list type="bullet">
/// <item><c>builds</c> — one row per upstream pipeline run, keyed (provider, providerBuildId).</item>
/// <item><c>build_stages</c> — per-leaf-stage detail for terminal-failed builds.</item>
/// <item><c>build_stage_logs</c> — tail-truncated log content per stage.</item>
/// <item><c>build_artifacts</c> — extracted artifact rows.</item>
/// <item><c>build_artifact_files</c> — indexed files inside an artifact.</item>
/// <item><c>build_failure_points</c> — parsed FailurePoint rows.</item>
/// <item><c>build_fix_pending</c> — Q27.12 deferral staging.</item>
/// </list>
/// </para>
/// </summary>
public static class Phase31Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // -- builds table (Q27.2) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS builds (
                Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                Provider          TEXT    NOT NULL CHECK (Provider IN ('ado','github')),
                ProviderBuildId   TEXT    NOT NULL,
                PrId              TEXT    NULL,
                RepoId            TEXT    NOT NULL,
                PipelineId        TEXT    NULL,
                PipelineName      TEXT    NULL,
                State             TEXT    NOT NULL,
                Result            TEXT    NULL,
                TriggeringCommit  TEXT    NULL,
                Branch            TEXT    NULL,
                WebUrl            TEXT    NULL,
                StartedAt         TEXT    NULL,
                FinishedAt        TEXT    NULL,
                LastSyncAt        TEXT    NULL,
                LastSyncStatus    TEXT    NOT NULL DEFAULT 'idle',
                LastSyncError     TEXT    NULL,
                IsRunningSince    TEXT    NULL,
                IsActive          INTEGER NOT NULL DEFAULT 1
            );", ct);

        await ExecAsync(conn, @"
            CREATE UNIQUE INDEX IF NOT EXISTS UX_builds_Provider_ProviderBuildId
            ON builds (Provider, ProviderBuildId);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_builds_PrId
            ON builds (PrId);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_builds_PrId_IsActive
            ON builds (PrId, IsActive);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_builds_LastSyncAt
            ON builds (LastSyncAt);", ct);

        // -- build_stages table (Q27.3) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_stages (
                Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                BuildId          INTEGER NOT NULL,
                ProviderStageRef TEXT    NOT NULL,
                StageName        TEXT    NOT NULL DEFAULT '',
                StageOrder       INTEGER NOT NULL DEFAULT 0,
                State            TEXT    NOT NULL DEFAULT '',
                Result           TEXT    NULL,
                StartedAt        TEXT    NULL,
                FinishedAt       TEXT    NULL,
                LogRef           TEXT    NULL,
                LogTailHash      TEXT    NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_build_stages_BuildId_StageOrder
            ON build_stages (BuildId, StageOrder);", ct);

        await ExecAsync(conn, @"
            CREATE UNIQUE INDEX IF NOT EXISTS UX_build_stages_BuildId_ProviderStageRef
            ON build_stages (BuildId, ProviderStageRef);", ct);

        // -- build_stage_logs table (Q27.4) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_stage_logs (
                StageId            INTEGER NOT NULL,
                LogIndex           INTEGER NOT NULL,
                Content            TEXT    NOT NULL DEFAULT '',
                SizeBytes          INTEGER NOT NULL DEFAULT 0,
                TruncatedFromBytes INTEGER NOT NULL DEFAULT 0,
                FetchedAt          TEXT    NOT NULL,
                PRIMARY KEY (StageId, LogIndex)
            );", ct);

        // -- build_artifacts table (Q27.5) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_artifacts (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                BuildId       INTEGER NOT NULL,
                Name          TEXT    NOT NULL,
                DownloadUrl   TEXT    NULL,
                SizeBytes     INTEGER NOT NULL DEFAULT 0,
                ExtractedPath TEXT    NULL,
                FetchedAt     TEXT    NULL,
                ExpiredAt     TEXT    NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_build_artifacts_BuildId
            ON build_artifacts (BuildId);", ct);

        // -- build_artifact_files table (Q27.5) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_artifact_files (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ArtifactId INTEGER NOT NULL,
                RelPath    TEXT    NOT NULL,
                SizeBytes  INTEGER NOT NULL DEFAULT 0,
                IsLog      INTEGER NOT NULL DEFAULT 0
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_build_artifact_files_ArtifactId
            ON build_artifact_files (ArtifactId);", ct);

        // -- build_failure_points table (Q27.6) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_failure_points (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                StageId             INTEGER NOT NULL,
                Kind                TEXT    NOT NULL DEFAULT '',
                FilePath            TEXT    NULL,
                Line                INTEGER NULL,
                Column              INTEGER NULL,
                Message             TEXT    NOT NULL DEFAULT '',
                SourceLogTailOffset INTEGER NOT NULL DEFAULT 0,
                Severity            TEXT    NULL,
                PayloadJson         TEXT    NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_build_failure_points_StageId
            ON build_failure_points (StageId);", ct);

        // -- build_fix_pending table (Q27.12) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_fix_pending (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkItemId  TEXT    NOT NULL,
                BuildId     INTEGER NOT NULL,
                EnqueuedAt  TEXT    NOT NULL,
                ReasonJson  TEXT    NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE UNIQUE INDEX IF NOT EXISTS UX_build_fix_pending_WorkItemId
            ON build_fix_pending (WorkItemId);", ct);

        // -- build_annotations table (operator override per build / fingerprint) --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS build_annotations (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                BuildId         INTEGER NOT NULL,
                Category        TEXT    NOT NULL,
                RootCause       TEXT    NULL,
                SuggestedFiles  TEXT    NULL,
                ByUserId        TEXT    NULL,
                At              TEXT    NOT NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_build_annotations_BuildId
            ON build_annotations (BuildId);", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
