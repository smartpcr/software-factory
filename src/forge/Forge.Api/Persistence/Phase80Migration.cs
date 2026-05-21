// -----------------------------------------------------------------------
// <copyright file="Phase80Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 80 (Stage 80.A) — control-side schema for the repo refactor.
/// <b>Additive only; no behavior change.</b> Creates:
/// <list type="number">
///   <item>the <c>repo_registry</c> table — the thin control-side index that
///         (in later stages) mirrors each per-product <c>repos</c> row,
///         enforces the Q2/Q5 global uniqueness invariants, and feeds
///         sync-due enumeration. See <see cref="RepoRegistryRow"/>;</item>
///   <item>two <b>partial</b> UNIQUE indexes (<c>WHERE DeletedAt IS NULL</c>)
///         on <c>RemoteUrl</c> and <c>CloneRoot</c> — the cross-product
///         guarantees SQLite can't enforce once repos live in separate
///         <c>{slug}.db</c> files;</item>
///   <item>the <c>products.DefaultRepoId</c> column — the topbar repo
///         switcher's initial selection + the Stage 80.F backfill anchor.</item>
/// </list>
///
/// <para><b>Control-only.</b> All ops target the control DB (the
/// <see cref="ControlDbContext"/> connection); the <c>ProductDbContext</c>
/// parameter is unused but kept to satisfy the
/// <see cref="PhaseMigrationRunner"/> reflection signature
/// <c>ApplyAsync(ControlDbContext, ProductDbContext, CancellationToken)</c>.</para>
///
/// <para><b>Idempotent.</b> <c>CREATE TABLE / INDEX IF NOT EXISTS</c> +
/// <c>pragma_table_info</c>-guarded <c>ALTER TABLE</c>. Index names are pinned
/// to match <see cref="DbModelControl"/>'s <c>HasDatabaseName(...)</c> so that
/// on a fresh DB — where <c>EnsureCreated</c> lays down the model index first —
/// the <c>IF NOT EXISTS</c> here is a clean no-op rather than a second index.</para>
/// </summary>
public static class Phase80Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS repo_registry (
                Id TEXT NOT NULL PRIMARY KEY,
                Sort INTEGER NOT NULL DEFAULT 0,
                ProductId TEXT NOT NULL,
                ProductSlug TEXT NOT NULL DEFAULT '',
                Kind TEXT NOT NULL DEFAULT '',
                RemoteUrl TEXT NOT NULL DEFAULT '',
                CloneRoot TEXT NOT NULL DEFAULT '',
                DisplayName TEXT NOT NULL DEFAULT '',
                DeletedAt TEXT NULL,
                LastSyncAt TEXT NULL,
                LastSyncCursor TEXT NULL,
                LastSyncStatus TEXT NOT NULL DEFAULT 'idle',
                LastSyncError TEXT NULL,
                IsRunningSince TEXT NULL,
                LastUpstreamChangedDate TEXT NULL,
                WorkItemSyncIntervalSeconds INTEGER NULL,
                PrSyncIntervalSeconds INTEGER NULL,
                BuildSyncIntervalSeconds INTEGER NULL
            );
            """, ct);

        await ExecAsync(conn,
            "CREATE INDEX IF NOT EXISTS IX_repo_registry_ProductId ON repo_registry (ProductId);", ct);
        // Q5 / Q2 — globally unique over ACTIVE rows only (soft-deleted rows
        // release the slot so a repo can be re-added with the same URL / root).
        await ExecAsync(conn,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_repo_registry_RemoteUrl ON repo_registry (RemoteUrl) WHERE DeletedAt IS NULL;", ct);
        await ExecAsync(conn,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_repo_registry_CloneRoot ON repo_registry (CloneRoot) WHERE DeletedAt IS NULL;", ct);

        // Product-level default repo (backfill anchor + switcher seed).
        await EnsureColumnAsync(conn, "products", "DefaultRepoId", "TEXT NULL", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> when absent. Idempotent.</summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
