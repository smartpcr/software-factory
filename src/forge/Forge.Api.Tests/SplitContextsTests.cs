// -----------------------------------------------------------------------
// <copyright file="SplitContextsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase D.1 — unit tests for the new split contexts:
/// <see cref="ControlDbContext"/>, <see cref="ProductDbContext"/>,
/// <see cref="IProductDbContextFactory"/>, and the
/// <c>ProductScopeMiddleware</c>.
///
/// <para>Each test seeds a fresh on-disk SQLite file via
/// <see cref="ForgeDbContext"/> (which still owns the union of both
/// models, so the schema is fully built) and then asserts that the
/// split contexts see the appropriate subset.</para>
/// </summary>
public class SplitContextsTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-split-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;

    public SplitContextsTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ControlDbContext_Model_HasOnlyControlEntities()
    {
        using var ctx = BuildControl();
        var entityTypes = ctx.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(n => n is not null)
            .ToHashSet();

        // Spot-check: every Control table from DbBoundary appears.
        Assert.Contains("products", entityTypes);
        Assert.Contains("repos", entityTypes);
        Assert.Contains("users", entityTypes);
        Assert.Contains("crews", entityTypes);
        Assert.Contains("agents", entityTypes);
        Assert.Contains("agent_usage", entityTypes);
        Assert.Contains("agent_claims", entityTypes);
        Assert.Contains("forge_actions", entityTypes);

        // Critically: product tables are NOT in the control model.
        Assert.DoesNotContain("workstreams", entityTypes);
        Assert.DoesNotContain("work_items", entityTypes);
        Assert.DoesNotContain("cards", entityTypes);
        Assert.DoesNotContain("prs", entityTypes);
        Assert.DoesNotContain("deliverables", entityTypes);
        Assert.DoesNotContain("task_queue", entityTypes);
        Assert.DoesNotContain("story_spawns", entityTypes);
        Assert.DoesNotContain("builds", entityTypes);
    }

    [Fact]
    public void ProductDbContext_Model_HasOnlyProductEntities()
    {
        using var ctx = BuildProductAt(this.paths.ControlDbPath());
        var entityTypes = ctx.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(n => n is not null)
            .ToHashSet();

        // Spot-check: every Product table from DbBoundary appears.
        Assert.Contains("workstreams", entityTypes);
        Assert.Contains("work_items", entityTypes);
        Assert.Contains("cards", entityTypes);
        Assert.Contains("prs", entityTypes);
        Assert.Contains("deliverables", entityTypes);
        Assert.Contains("task_queue", entityTypes);
        Assert.Contains("story_spawns", entityTypes);
        Assert.Contains("builds", entityTypes);
        Assert.Contains("idempotency", entityTypes);

        // Critically: control tables are NOT in the product model.
        Assert.DoesNotContain("products", entityTypes);
        Assert.DoesNotContain("repos", entityTypes);
        Assert.DoesNotContain("users", entityTypes);
        Assert.DoesNotContain("crews", entityTypes);
        Assert.DoesNotContain("agents", entityTypes);
        Assert.DoesNotContain("agent_usage", entityTypes);
        Assert.DoesNotContain("agent_claims", entityTypes);
        Assert.DoesNotContain("forge_actions", entityTypes);
    }

    [Fact]
    public async Task BothContexts_ReadSameData_FromSamePhysicalFile()
    {
        // Build schema via legacy ForgeDbContext (still has the union),
        // then read via the new split contexts. Phase D interim: both
        // point at forge.db.
        using (var legacy = BuildLegacy())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Products.Add(new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" });
            legacy.Repos.Add(new RepoRow { Id = "r1", ProductId = "atlas", Kind = "github", RemoteUrl = "https://x", DisplayName = "r1", DefaultBranch = "main", CloneRoot = "C:\\r1" });
            legacy.Workstreams.Add(new WorkstreamRow { Id = "ws1", Name = "ws1", Branch = "ws/1", Epic = "EPIC", Status = "active", RepoId = "r1" });
            await legacy.SaveChangesAsync();
        }

        using (var control = BuildControl())
        {
            var product = await control.Products.AsNoTracking().FirstAsync();
            Assert.Equal("atlas", product.Id);
            var repo = await control.Repos.AsNoTracking().FirstAsync();
            Assert.Equal("r1", repo.Id);
        }

        using (var product = BuildProductAt(this.paths.ControlDbPath()))
        {
            var ws = await product.Workstreams.AsNoTracking().FirstAsync();
            Assert.Equal("ws1", ws.Id);
            Assert.Equal("r1", ws.RepoId);
        }
    }

    [Fact]
    public void Factory_CachesSameInstancePerSlug()
    {
        var http = new TestHttpContextAccessor();
        using var factory = new ProductDbContextFactory(this.paths, http);

        var a1 = factory.CreateFor("atlas");
        var a2 = factory.CreateFor("atlas");
        var b1 = factory.CreateFor("failover-cluster");

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
        Assert.Equal("atlas", a1.ProductSlug);
        Assert.Equal("failover-cluster", b1.ProductSlug);
    }

    [Fact]
    public void Factory_RejectsInvalidSlug()
    {
        var http = new TestHttpContextAccessor();
        using var factory = new ProductDbContextFactory(this.paths, http);

        Assert.Throws<ArgumentException>(() => factory.CreateFor("forge")); // reserved
        Assert.Throws<ArgumentException>(() => factory.CreateFor("../evil"));
        Assert.Throws<ArgumentException>(() => factory.CreateFor(""));
    }

    [Fact]
    public void Factory_FallsBackTo_ControlDbPath_WhenPerProductFileMissing()
    {
        var http = new TestHttpContextAccessor();
        using var factory = new ProductDbContextFactory(this.paths, http);

        // Per-product file doesn't exist yet → factory uses forge.db.
        var ctx = factory.CreateFor("atlas");
        var connectionString = ctx.Database.GetDbConnection().ConnectionString;
        Assert.Contains("forge.db", connectionString);

        // Now create a per-product file and try a new slug — should pick it up.
        var slugPath = this.paths.ProductDbPath("perproduct");
        File.WriteAllText(slugPath, string.Empty);
        var ctx2 = factory.CreateFor("perproduct");
        var cs2 = ctx2.Database.GetDbConnection().ConnectionString;
        Assert.Contains("perproduct.db", cs2);
    }

    [Fact]
    public void Factory_CreateForCurrentRequest_ThrowsWhenSlugMissing()
    {
        var http = new TestHttpContextAccessor(); // no HttpContext set
        using var factory = new ProductDbContextFactory(this.paths, http);

        Assert.Throws<InvalidOperationException>(() => factory.CreateForCurrentRequest());
    }

    [Fact]
    public void Factory_Dispose_DisposesCachedContexts()
    {
        var http = new TestHttpContextAccessor();
        var factory = new ProductDbContextFactory(this.paths, http);

        var ctx = factory.CreateFor("atlas");
        factory.Dispose();

        // Subsequent operations on the disposed context throw.
        Assert.Throws<ObjectDisposedException>(() => ctx.Workstreams.Count());
        // And further factory calls throw.
        Assert.Throws<ObjectDisposedException>(() => factory.CreateFor("atlas"));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private ForgeDbContext BuildLegacy()
    {
        var opt = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        return new ForgeDbContext(opt);
    }

    private ControlDbContext BuildControl()
    {
        var opt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        return new ControlDbContext(opt);
    }

    private static ProductDbContext BuildProductAt(string path)
    {
        var opt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new ProductDbContext(opt);
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
