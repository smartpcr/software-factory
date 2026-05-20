// -----------------------------------------------------------------------
// <copyright file="Phase80SchemaTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 80 (Stage 80.A) — additive schema for the repo refactor:
/// the control-side <c>repo_registry</c> table + its partial-unique
/// indexes (<see cref="Phase80Migration"/>), <c>products.DefaultRepoId</c>,
/// and the product-side <c>product_stories.RepoId</c> column
/// (<see cref="Phase81Migration"/>).
///
/// <para>Unit tests (1–6) run each migration against a hand-rolled in-memory
/// SQLite connection seeded with only the columns the migration touches, so
/// the raw SQL is the contract under test. The integration tests (7–9) boot
/// the full host so the production <see cref="PhaseMigrationRunner"/> applies
/// both migrations, then exercise the live EF mapping.</para>
/// </summary>
public class Phase80SchemaTests
{
    // ── plumbing ─────────────────────────────────────────────────────────

    /// <summary>Minimal "pre-Phase80" control DB: just a <c>products</c> table.</summary>
    private static async Task<SqliteConnection> NewControlConnectionAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE products (
              Id TEXT PRIMARY KEY,
              Slug TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    /// <summary>Minimal "pre-Phase81" product DB: just a <c>product_stories</c> table.</summary>
    private static async Task<SqliteConnection> NewProductConnectionAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE product_stories (
              Id TEXT PRIMARY KEY,
              ProductId TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private static ControlDbContext WrapControl(SqliteConnection conn) =>
        new(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);

    private static ProductDbContext WrapProduct(SqliteConnection conn) =>
        new(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<int> CountColumnsNamedAsync(SqliteConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var r = await cmd.ExecuteReaderAsync();
        var n = 0;
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n;";
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection conn, string indexName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=@n;";
        cmd.Parameters.AddWithValue("@n", indexName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task InsertRegistryAsync(
        SqliteConnection conn, string id, string remoteUrl, string cloneRoot, string? deletedAt = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO repo_registry (Id, ProductId, ProductSlug, Kind, RemoteUrl, CloneRoot, DisplayName, DeletedAt) " +
            "VALUES (@id, 'p1', 'p1', 'github', @url, @root, @id, @del);";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        cmd.Parameters.AddWithValue("@root", cloneRoot);
        cmd.Parameters.AddWithValue("@del", (object?)deletedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── 1. Phase80 creates repo_registry + indexes + products.DefaultRepoId ──

    [Fact]
    public async Task Phase80_Creates_Registry_Indexes_And_DefaultRepoId()
    {
        await using var conn = await NewControlConnectionAsync();
        Assert.False(await TableExistsAsync(conn, "repo_registry"));
        Assert.False(await ColumnExistsAsync(conn, "products", "DefaultRepoId"));

        await using var control = WrapControl(conn);
        await using (var db = WrapProduct(conn))
        {
            await Phase80Migration.ApplyAsync(control, db);
        }

        Assert.True(await TableExistsAsync(conn, "repo_registry"));
        Assert.True(await IndexExistsAsync(conn, "IX_repo_registry_ProductId"));
        Assert.True(await IndexExistsAsync(conn, "IX_repo_registry_RemoteUrl"));
        Assert.True(await IndexExistsAsync(conn, "IX_repo_registry_CloneRoot"));
        Assert.True(await ColumnExistsAsync(conn, "products", "DefaultRepoId"));
    }

    // ── 2. Idempotent re-run ─────────────────────────────────────────────

    [Fact]
    public async Task Phase80_Is_Idempotent()
    {
        await using var conn = await NewControlConnectionAsync();
        await using var control = WrapControl(conn);
        await using var db = WrapProduct(conn);

        await Phase80Migration.ApplyAsync(control, db);
        await Phase80Migration.ApplyAsync(control, db);
        await Phase80Migration.ApplyAsync(control, db);

        Assert.True(await TableExistsAsync(conn, "repo_registry"));
        // DefaultRepoId added exactly once (no duplicate ALTER).
        Assert.Equal(1, await CountColumnsNamedAsync(conn, "products", "DefaultRepoId"));
    }

    // ── 3. RemoteUrl partial-unique index rejects active duplicates ──────

    [Fact]
    public async Task Phase80_RemoteUrl_Unique_Over_Active_Rows()
    {
        await using var conn = await NewControlConnectionAsync();
        await using var control = WrapControl(conn);
        await using (var db = WrapProduct(conn)) await Phase80Migration.ApplyAsync(control, db);

        await InsertRegistryAsync(conn, "r1", "https://example/repo", "C:\\clone\\r1");

        // Same RemoteUrl, both active → unique violation.
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertRegistryAsync(conn, "r2", "https://example/repo", "C:\\clone\\r2"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. CloneRoot partial-unique index rejects active duplicates ──────

    [Fact]
    public async Task Phase80_CloneRoot_Unique_Over_Active_Rows()
    {
        await using var conn = await NewControlConnectionAsync();
        await using var control = WrapControl(conn);
        await using (var db = WrapProduct(conn)) await Phase80Migration.ApplyAsync(control, db);

        await InsertRegistryAsync(conn, "r1", "https://example/a", "C:\\clone\\shared");

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            InsertRegistryAsync(conn, "r2", "https://example/b", "C:\\clone\\shared"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Soft-deleted rows release the uniqueness slot ─────────────────

    [Fact]
    public async Task Phase80_SoftDeleted_Row_Frees_RemoteUrl_And_CloneRoot()
    {
        await using var conn = await NewControlConnectionAsync();
        await using var control = WrapControl(conn);
        await using (var db = WrapProduct(conn)) await Phase80Migration.ApplyAsync(control, db);

        // First repo is soft-deleted (DeletedAt set) — excluded from the
        // partial index, so the same URL + clone root can be re-added.
        await InsertRegistryAsync(conn, "r1", "https://example/repo", "C:\\clone\\r", deletedAt: "2026-05-20T00:00:00Z");

        // Re-add with identical URL + clone root, active → must succeed.
        await InsertRegistryAsync(conn, "r2", "https://example/repo", "C:\\clone\\r");

        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM repo_registry;";
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync() ?? 0L));
    }

    // ── 6. Phase81 adds product_stories.RepoId (idempotent) ──────────────

    [Fact]
    public async Task Phase81_Adds_ProductStories_RepoId()
    {
        await using var conn = await NewProductConnectionAsync();
        Assert.False(await ColumnExistsAsync(conn, "product_stories", "RepoId"));

        await using var control = WrapControl(conn);
        await using var db = WrapProduct(conn);
        await Phase81Migration.ApplyAsync(control, db);
        await Phase81Migration.ApplyAsync(control, db); // idempotent

        Assert.True(await ColumnExistsAsync(conn, "product_stories", "RepoId"));
        Assert.Equal(1, await CountColumnsNamedAsync(conn, "product_stories", "RepoId"));
    }

    // ── 7. [integration] booted host has the schema + live EF mapping ────

    [Fact]
    public async Task Booted_Host_Has_Registry_And_New_Columns()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot → runs PhaseMigrationRunner */ }

        // repo_registry + its unique index exist in the control DB, and the
        // RepoRegistryRow EF mapping round-trips.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var conn = (SqliteConnection)control.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            Assert.True(await TableExistsAsync(conn, "repo_registry"));
            Assert.True(await IndexExistsAsync(conn, "IX_repo_registry_RemoteUrl"));
            Assert.True(await IndexExistsAsync(conn, "IX_repo_registry_CloneRoot"));
            Assert.True(await ColumnExistsAsync(conn, "products", "DefaultRepoId"));

            control.RepoRegistry.Add(new RepoRegistryRow
            {
                Id = "reg-roundtrip",
                ProductId = "p-roundtrip",
                ProductSlug = "p-roundtrip",
                Kind = "github",
                RemoteUrl = "https://example/roundtrip",
                CloneRoot = "C:\\clone\\roundtrip",
                DisplayName = "roundtrip",
            });
            await control.SaveChangesAsync();
        }

        using (var scope2 = f.Services.CreateScope())
        {
            var control2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
            var read = await control2.RepoRegistry.AsNoTracking().FirstAsync(r => r.Id == "reg-roundtrip");
            Assert.Equal("idle", read.LastSyncStatus); // POCO default persisted
            Assert.Null(read.DeletedAt);
        }
    }

    // ── 8. [integration] ProductStoryRow.RepoId round-trips through EF ───

    [Fact]
    public async Task ProductStory_RepoId_RoundTrips_Through_EF()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            Assert.True(await ColumnExistsAsync(conn, "product_stories", "RepoId"));

            db.ProductStories.Add(new ProductStoryRow
            {
                Id = "story-repo-roundtrip",
                ProductId = "p-roundtrip",
                RepoId = "repo-xyz",
                Name = "repo round-trip",
            });
            await db.SaveChangesAsync();
        }

        using (var scope2 = f.Services.CreateScope())
        {
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var read = await db2.ProductStories.AsNoTracking().FirstAsync(s => s.Id == "story-repo-roundtrip");
            Assert.Equal("repo-xyz", read.RepoId);
        }
    }

    // ── 9. [integration] Product.DefaultRepoId round-trips through EF ────

    [Fact]
    public async Task Product_DefaultRepoId_RoundTrips_Through_EF()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = "p-default-repo",
                Name = "default repo product",
                Slug = "p-default-repo",
                DefaultRepoId = "repo-default",
            });
            await control.SaveChangesAsync();
        }

        using (var scope2 = f.Services.CreateScope())
        {
            var control2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
            var read = await control2.Products.AsNoTracking().FirstAsync(p => p.Id == "p-default-repo");
            Assert.Equal("repo-default", read.DefaultRepoId);
        }
    }
}
