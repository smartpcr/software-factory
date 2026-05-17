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
/// <see cref="ProductDbContext"/> (which still owns the union of both
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
    public void ProductDbContext_Model_ExposesProductEntities_AndControlEntitiesDuringPhaseDInterim()
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

        // Phase G — ProductDbContext is now tightened to product-only.
        // Control entities (products, repos, users, crews, agents,
        // agent_usage, agent_claims, forge_actions) live on
        // ControlDbContext. Verify they are NOT present in the
        // ProductDbContext model.
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
        // Build schema via both contexts (control + product), then read via
        // the split contexts against the same physical forge.db file.
        // Phase G: control entities live on ControlDbContext, product
        // entities on ProductDbContext; they share the same file in Phase D
        // interim mode.
        using (var legacyCtl = BuildControl())
        {
            await legacyCtl.Database.EnsureCreatedAsync();
            legacyCtl.Products.Add(new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" });
            legacyCtl.Repos.Add(new RepoRow { Id = "r1", ProductId = "atlas", Kind = "github", RemoteUrl = "https://x", DisplayName = "r1", DefaultBranch = "main", CloneRoot = "C:\\r1" });
            await legacyCtl.SaveChangesAsync();
        }
        using (var legacy = BuildLegacy())
        {
            var script = legacy.Database.GenerateCreateScript();
            foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = stmt.Trim();
                if (trimmed.Length > 0) await legacy.Database.ExecuteSqlRawAsync(trimmed);
            }
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

        // Phase 73 half-applied guard: creating a per-product file does
        // NOT cause the factory to pick it up unless the
        // `phase73-split-applied` marker is also stamped in
        // `forge_seed_state`. Without the marker, the factory refuses
        // the per-product file and continues routing to the control DB.
        var slugPath = this.paths.ProductDbPath("perproduct");
        File.WriteAllText(slugPath, string.Empty);
        SplitMarkerCache.ResetForTesting();
        var ctx2 = factory.CreateFor("perproduct");
        var cs2 = ctx2.Database.GetDbConnection().ConnectionString;
        Assert.Contains("forge.db", cs2);
        Assert.DoesNotContain("perproduct.db", cs2);

        // Phase 73 happy path: stamp the marker → the per-product file
        // is now used. A new factory instance is required because
        // CreateFor caches per slug.
        StampPhase73Marker(this.paths.ControlDbPath());
        SplitMarkerCache.ResetForTesting();
        using var factory2 = new ProductDbContextFactory(this.paths, http);
        var ctx3 = factory2.CreateFor("perproduct");
        var cs3 = ctx3.Database.GetDbConnection().ConnectionString;
        Assert.Contains("perproduct.db", cs3);
    }

    [Fact]
    public void Factory_CreateForCurrentRequest_FallsBackTo_ControlDbWhenSlugMissing()
    {
        var http = new TestHttpContextAccessor(); // no HttpContext set
        using var factory = new ProductDbContextFactory(this.paths, http);

        // Phase D interim — no slug means fall back to forge.db so admin /
        // sync paths without a product header still work. After Phase F the
        // factory will require an explicit slug for any product-table write.
        var ctx = factory.CreateForCurrentRequest();
        Assert.NotNull(ctx);
        Assert.Null(ctx.ProductSlug);
        Assert.Contains("forge.db", ctx.Database.GetDbConnection().ConnectionString);
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

    private ProductDbContext BuildLegacy()
    {
        var opt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        return new ProductDbContext(opt);
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

    /// <summary>
    /// Test helper — stamps the Phase 73 split-applied marker in the
    /// control DB so the marker-guard branch of
    /// <see cref="ProductDbContextFactory.CreateFor"/> uses
    /// per-product files. Creates the <c>forge_seed_state</c> table on
    /// demand because tests don't run the Phase 22 migration.
    /// </summary>
    private static void StampPhase73Marker(string controlPath)
    {
        using var conn = new SqliteConnection($"Data Source={controlPath}");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText =
            "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
        create.ExecuteNonQuery();
        using var ins = conn.CreateCommand();
        ins.CommandText =
            "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES ('phase73-split-applied', $at);";
        var p = ins.CreateParameter();
        p.ParameterName = "$at";
        p.Value = DateTimeOffset.UtcNow.ToString("O");
        ins.Parameters.Add(p);
        ins.ExecuteNonQuery();
    }
}
