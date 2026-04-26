// -----------------------------------------------------------------------
// <copyright file="Phase8Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 8 — products, repos, and the repo_id
/// columns added to cards / workstreams / prs. Runs after EnsureCreatedAsync
/// so a fresh DB picks up everything from the EF model and an existing dev
/// DB only sees the additive changes. SQLite-only.
/// </summary>
public static class Phase8Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "cards", "RepoId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "workstreams", "RepoId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "RepoId", "TEXT NULL", ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS products (
                Id TEXT NOT NULL PRIMARY KEY,
                Sort INTEGER NOT NULL DEFAULT 0,
                Name TEXT NOT NULL,
                Slug TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                OwnerUserId TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS IX_products_Slug ON products(Slug);", ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS repos (
                Id TEXT NOT NULL PRIMARY KEY,
                Sort INTEGER NOT NULL DEFAULT 0,
                ProductId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                RemoteUrl TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                DefaultBranch TEXT NOT NULL DEFAULT 'main',
                CloneRoot TEXT NOT NULL,
                AdoOrg TEXT NULL,
                AdoProject TEXT NULL,
                GhOwner TEXT NULL,
                GhRepo TEXT NULL,
                FOREIGN KEY (ProductId) REFERENCES products(Id) ON DELETE CASCADE
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS IX_repos_RemoteUrl ON repos(RemoteUrl);", ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS IX_repos_CloneRoot ON repos(CloneRoot);", ct);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // table_info column 1 is "name".
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeDecl, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return;
        await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
