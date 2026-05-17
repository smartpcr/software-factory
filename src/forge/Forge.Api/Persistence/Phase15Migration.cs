// -----------------------------------------------------------------------
// <copyright file="Phase15Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 15 — product sprint &amp; area-path
/// management. Adds <c>product_iterations</c>, <c>product_area_paths</c>,
/// <c>repo_iteration_bindings</c>, <c>product_saved_prefixes</c> and the
/// <c>ActiveIterationId</c> column on <c>products</c>. Runs after
/// <see cref="Phase14Migration"/>; SQLite-only.
/// </summary>
public static class Phase15Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Phase 15.6 — product-level override for the active iteration.
        await EnsureColumnAsync(conn, "products", "ActiveIterationId", "TEXT NULL", ct);

        // Phase 15.1 — product-scoped iteration registry.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS product_iterations (
                Id TEXT NOT NULL PRIMARY KEY,
                ProductId TEXT NOT NULL,
                ProviderKind TEXT NOT NULL DEFAULT 'ado',
                ProviderOrg TEXT NOT NULL DEFAULT '',
                ProviderProject TEXT NOT NULL DEFAULT '',
                ProviderNodeId TEXT NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL DEFAULT '',
                StartDate TEXT NULL,
                FinishDate TEXT NULL,
                State TEXT NOT NULL DEFAULT 'future',
                AttributesJson TEXT NOT NULL DEFAULT '{}',
                ImportedAt TEXT NOT NULL,
                RefreshedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS UX_product_iterations_Product_Node ON product_iterations(ProductId, ProviderNodeId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_iterations_ProductId ON product_iterations(ProductId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_iterations_State ON product_iterations(State);", ct);

        // Phase 15.2 — product-scoped area-path registry.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS product_area_paths (
                Id TEXT NOT NULL PRIMARY KEY,
                ProductId TEXT NOT NULL,
                ProviderKind TEXT NOT NULL DEFAULT 'ado',
                ProviderOrg TEXT NOT NULL DEFAULT '',
                ProviderProject TEXT NOT NULL DEFAULT '',
                ProviderNodeId TEXT NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                ParentPath TEXT NULL,
                ParentNodeId TEXT NULL,
                IsDefaultFilter INTEGER NOT NULL DEFAULT 0,
                AttributesJson TEXT NOT NULL DEFAULT '{}',
                ImportedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS UX_product_area_paths_Product_Node ON product_area_paths(ProductId, ProviderNodeId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_area_paths_ProductId ON product_area_paths(ProductId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_area_paths_Default ON product_area_paths(ProductId, IsDefaultFilter);", ct);

        // Phase 15.5 — repo ↔ iteration bindings.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS repo_iteration_bindings (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                RepoId TEXT NOT NULL,
                ProductIterationId TEXT NOT NULL,
                Role TEXT NOT NULL DEFAULT 'reference',
                CreatedAt TEXT NOT NULL,
                CreatedBy TEXT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_repo_iteration_bindings_Repo_Role ON repo_iteration_bindings(RepoId, Role);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_repo_iteration_bindings_Iteration ON repo_iteration_bindings(ProductIterationId);", ct);

        // Phase 15.10 — saved prefixes per product.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS product_saved_prefixes (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ProductId TEXT NOT NULL,
                Prefix TEXT NOT NULL,
                Kind TEXT NOT NULL DEFAULT 'iterations',
                RepoId TEXT NOT NULL,
                AddedBy TEXT NOT NULL DEFAULT '',
                AddedAt TEXT NOT NULL,
                LastRefreshedAt TEXT NULL,
                LastDeltaJson TEXT NOT NULL DEFAULT '{"added":0,"removed":0,"renamed":0}'
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS UX_product_saved_prefixes_Product_Prefix_Kind ON product_saved_prefixes(ProductId, Prefix, Kind);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_product_saved_prefixes_ProductId ON product_saved_prefixes(ProductId);", ct);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
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
