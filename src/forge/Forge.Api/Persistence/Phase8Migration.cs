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
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
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

        // Phase 9.6 — users table.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS users (
                Id TEXT NOT NULL PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Email TEXT NULL,
                Login TEXT NULL,
                AvatarUrl TEXT NULL,
                Role TEXT NOT NULL DEFAULT 'human',
                CreatedAt TEXT NOT NULL,
                LastSeenAt TEXT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS IX_users_Email ON users(Email) WHERE Email IS NOT NULL;", ct);

        // Phase 10.1 — per-user preferences.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS user_settings (
                UserId TEXT NOT NULL PRIMARY KEY,
                DefaultAdoOrgUrl TEXT NULL,
                DefaultAdoProject TEXT NULL,
                DefaultGitHubOwner TEXT NULL,
                DefaultProductId TEXT NULL,
                DefaultRepoId TEXT NULL,
                BrowserPreference TEXT NULL,
                Theme TEXT NULL,
                NotificationsEnabled INTEGER NOT NULL DEFAULT 1,
                UpdatedAt TEXT NOT NULL
            );
            """, ct);

        // Phase 10.6 — crews.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS crews (
                Id TEXT NOT NULL PRIMARY KEY,
                Sort INTEGER NOT NULL DEFAULT 0,
                Name TEXT NOT NULL,
                ProductId TEXT NULL,
                Summary TEXT NOT NULL DEFAULT '',
                Lead TEXT NULL,
                Model TEXT NOT NULL DEFAULT 'claude-opus-4-7',
                ModelInputPricePerMTok REAL NOT NULL DEFAULT 0,
                ModelOutputPricePerMTok REAL NOT NULL DEFAULT 0
            );
            """, ct);

        // Phase 10.8 — supervising flag on the audit row.
        await EnsureColumnAsync(conn, "forge_actions", "Supervising", "INTEGER NOT NULL DEFAULT 0", ct);

        // Product-scoped stories.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS product_stories (
                Id TEXT NOT NULL PRIMARY KEY,
                ProductId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'planning',
                Sort INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z'
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_stories_ProductId ON product_stories(ProductId);", ct);

        // Story description (architect-agent prompt) and Points columns.
        await EnsureColumnAsync(conn, "product_stories", "Description", "TEXT NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync(conn, "product_stories", "Points", "INTEGER NOT NULL DEFAULT 0", ct);

        // Add ProductId to legacy epics table (may already exist).
        await EnsureColumnAsync(conn, "epics", "ProductId", "TEXT NULL", ct);
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
