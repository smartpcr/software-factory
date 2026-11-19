// -----------------------------------------------------------------------
// <copyright file="ProductDbProvisioningTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// POST /api/products on a split host must create the product's own
/// {slug}.db instead of leaving its rows to fall back into the control DB.
/// </summary>
public sealed class ProductDbProvisioningTests
{
    [Fact]
    public async Task Split_Host_Create_Product_Provisions_Its_Own_Db_And_Routes_To_It()
    {
        await using var app = new ForgeWebApplicationFactory();
        using var admin = await AdminAsync(app);
        StampSplitMarker(app.DbPath);
        SplitMarkerCache.ResetForTesting();
        try
        {
            var resp = await admin.PostAsJsonAsync("/api/products", new { id = "kepler", name = "kepler" });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var paths = app.Services.GetRequiredService<IForgeDbPaths>();
            var file = paths.ProductDbPath("kepler");
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".provisioning"));
            Assert.Equal(ProductTables(app.DbPath), Tables(file));

            using var scope = app.Services.CreateScope();
            var routed = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("kepler");
            Assert.Contains("kepler.db", routed.Database.GetConnectionString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await routed.Workstreams.CountAsync());
            Assert.Equal(5, await routed.Columns.CountAsync()); // board columns seeded, not left to the next boot
            routed.Repos.Add(new RepoRow { Id = "repo-kepler", ProductId = "kepler", RemoteUrl = "https://example.invalid/kepler",
                CloneRoot = Path.Combine(Path.GetTempPath(), "forge-fixture-kepler") });
            await routed.SaveChangesAsync();
            Assert.Equal(1, await routed.Repos.CountAsync());
        }
        finally
        {
            SplitMarkerCache.ResetForTesting();
        }
    }

    [Fact]
    public async Task Split_Host_Copies_The_Schema_Of_An_Existing_Product_Db_When_One_Exists()
    {
        await using var app = new ForgeWebApplicationFactory();
        using var admin = await AdminAsync(app);
        StampSplitMarker(app.DbPath);
        SplitMarkerCache.ResetForTesting();
        try
        {
            // First product: no sibling yet, so the control DB is the template.
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/products", new { id = "alpha", name = "alpha" })).StatusCode);
            var paths = app.Services.GetRequiredService<IForgeDbPaths>();
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductDbPath("alpha"), Pooling = false }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE INDEX IX_template_marker ON repos (RemoteUrl);";
                cmd.ExecuteNonQuery();
            }

            // Second product: alpha.db exists and is used as the template.
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/products", new { id = "beta", name = "beta" })).StatusCode);
            using var beta = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductDbPath("beta"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            beta.Open();
            using var probe = beta.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'IX_template_marker';";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
        finally
        {
            SplitMarkerCache.ResetForTesting();
        }
    }

    [Fact]
    public async Task Split_Host_Refuses_A_Slug_Whose_Db_File_Already_Exists_And_Leaves_It_Untouched()
    {
        await using var app = new ForgeWebApplicationFactory();
        using var admin = await AdminAsync(app);
        StampSplitMarker(app.DbPath);
        SplitMarkerCache.ResetForTesting();
        try
        {
            var stale = app.Services.GetRequiredService<IForgeDbPaths>().ProductDbPath("stale");
            await File.WriteAllTextAsync(stale, "not ours");

            var resp = await admin.PostAsJsonAsync("/api/products", new { id = "stale", name = "stale" });
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            Assert.Equal("not ours", await File.ReadAllTextAsync(stale));

            using var scope = app.Services.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            Assert.False(await control.Products.AnyAsync(p => p.Id == "stale"));
            Assert.False(await control.UserProductRoles.AnyAsync(r => r.ProductId == "stale"));
        }
        finally
        {
            SplitMarkerCache.ResetForTesting();
        }
    }

    [Fact]
    public async Task Pre_Split_Host_Create_Product_Keeps_Using_The_Control_Db()
    {
        await using var app = new ForgeWebApplicationFactory();
        using var admin = await AdminAsync(app);
        SplitMarkerCache.ResetForTesting();

        var resp = await admin.PostAsJsonAsync("/api/products", new { id = "vega", name = "vega" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.False(File.Exists(app.Services.GetRequiredService<IForgeDbPaths>().ProductDbPath("vega")));
    }

    private static async Task<HttpClient> AdminAsync(ForgeWebApplicationFactory app)
    {
        // Like the UI: an admin who owns the currently selected product.
        await app.SeedProductAsync("p");
        await app.EnrollAsync("admin", admin: true, productId: "p");
        var client = await app.PairedClientAsync("admin");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        return client;
    }

    private static void StampSplitMarker(string controlPath)
    {
        using var conn = new SqliteConnection($"Data Source={controlPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);"
            + "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES ('phase73-split-applied', '2026-11-19T00:00:00Z');";
        cmd.ExecuteNonQuery();
    }

    private static SortedSet<string> ProductTables(string controlPath)
    {
        var product = Forge.Api.Persistence.Split.DbBoundary.AllTables
            .Where(t => t.Kind == Forge.Api.Persistence.Split.DbKind.Product)
            .Select(t => t.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A split product DB = DbBoundary's product tables plus `repos`
        // (moved per product in Phase 80.B). Same set as the live local.db.
        product.Add("repos");
        return new SortedSet<string>(Tables(controlPath).Where(product.Contains), StringComparer.OrdinalIgnoreCase);
    }

    private static SortedSet<string> Tables(string path)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}