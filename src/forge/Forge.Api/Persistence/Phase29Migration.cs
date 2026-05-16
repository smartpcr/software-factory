// -----------------------------------------------------------------------
// <copyright file="Phase29Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 29 — full build pipeline with
/// init/restore commands + persistent validation run history.
/// Adds <c>InitCommand</c> and <c>RestoreCommand</c> columns to the
/// <c>repos</c> table, and creates the <c>build_validation_runs</c>
/// table for persistent validation history.
/// </summary>
public static class Phase29Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Add init/restore command columns to repos.
        await AddColumnIfMissingAsync(conn, "repos", "InitCommand", "TEXT NULL", ct);
        await AddColumnIfMissingAsync(conn, "repos", "RestoreCommand", "TEXT NULL", ct);

        // Create build_validation_runs table if missing.
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS build_validation_runs (
                Id TEXT NOT NULL PRIMARY KEY,
                RepoId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'queued',
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT NULL,
                AllPassed INTEGER NOT NULL DEFAULT 0,
                Ecosystem TEXT NULL,
                StepsJson TEXT NULL,
                TriggeredBy TEXT NULL,
                ErrorMessage TEXT NULL
            )
            """;
        await createCmd.ExecuteNonQueryAsync(ct);

        // Index for repo-scoped history queries.
        await using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_build_validation_runs_repo_started
            ON build_validation_runs (RepoId, StartedAt DESC)
            """;
        await idxCmd.ExecuteNonQueryAsync(ct);
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
}
