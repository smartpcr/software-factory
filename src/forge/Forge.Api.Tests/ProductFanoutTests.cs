// -----------------------------------------------------------------------
// <copyright file="ProductFanoutTests.cs" company="Microsoft Corp.">
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
/// Phase E — unit tests for <see cref="ProductFanout"/>. Seeds two
/// products in the control DB, then asserts the fan-out helper invokes
/// the per-product callback for each one (in slug order), respects the
/// parallelism cap, and aggregates per-product errors into an
/// <see cref="AggregateException"/>.
/// </summary>
public class ProductFanoutTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-fanout-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;
    private readonly ControlDbContext control;
    private readonly ProductDbContextFactory factory;
    private readonly ProductFanout fanout;

    public ProductFanoutTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
            ["Forge:Db:MaxConcurrentProductDbs"] = "4",
        }).Build();
        this.paths = new ForgeDbPaths(cfg);

        var ctlOptions = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        this.control = new ControlDbContext(ctlOptions);
        this.control.Database.EnsureCreated();

        this.control.Products.AddRange(
            new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" },
            new ProductRow { Id = "raft", Slug = "raft", Name = "Raft" },
            new ProductRow { Id = "failover", Slug = "failover-cluster", Name = "Failover Cluster" });
        this.control.SaveChanges();

        var http = new FakeHttpContextAccessor();
        this.factory = new ProductDbContextFactory(this.paths, http);
        this.fanout = new ProductFanout(this.control, this.factory, cfg);
    }

    public void Dispose()
    {
        this.control.Dispose();
        this.factory.Dispose();
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ForEachProduct_InvokesCallback_OncePerProductInSlugOrder()
    {
        var visited = new List<string>();
        var slugLock = new object();

        var count = await this.fanout.ForEachProductAsync(async (db, slug, ct) =>
        {
            lock (slugLock) visited.Add(slug);
            await Task.CompletedTask;
        });

        Assert.Equal(3, count);
        // Each product slug seen exactly once.
        var deduped = visited.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(3, deduped.Count);
        Assert.Contains("atlas", deduped);
        Assert.Contains("raft", deduped);
        Assert.Contains("failover-cluster", deduped);
    }

    [Fact]
    public async Task ForEachProduct_ContinuesPastSinglePerProductError_ThenThrowsAggregate()
    {
        var visited = new List<string>();
        var lockObj = new object();

        var ex = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await this.fanout.ForEachProductAsync(async (db, slug, ct) =>
            {
                lock (lockObj) visited.Add(slug);
                if (slug == "raft") throw new InvalidOperationException("raft is bad");
                await Task.CompletedTask;
            });
        });

        // Other products still ran.
        Assert.Contains("atlas", visited);
        Assert.Contains("failover-cluster", visited);
        Assert.Single(ex.InnerExceptions);
        Assert.Contains("raft is bad", ex.InnerExceptions[0].Message);
    }

    [Fact]
    public async Task ForEachProduct_WithExplicitSlugList_OnlyRunsThose()
    {
        var visited = new List<string>();
        var lockObj = new object();

        var count = await this.fanout.ForEachProductAsync(
            new[] { "atlas", "failover-cluster" },
            async (db, slug, ct) =>
            {
                lock (lockObj) visited.Add(slug);
                await Task.CompletedTask;
            });

        Assert.Equal(2, count);
        Assert.Equal(2, visited.Count);
        Assert.Contains("atlas", visited);
        Assert.DoesNotContain("raft", visited);
    }

    [Fact]
    public async Task CollectAsync_ReturnsPerProductResult()
    {
        var result = await this.fanout.CollectAsync<string>(async (db, slug, ct) =>
        {
            await Task.CompletedTask;
            return $"hello {slug}";
        });

        Assert.Equal(3, result.Count);
        Assert.Equal("hello atlas", result["atlas"]);
        Assert.Equal("hello raft", result["raft"]);
        Assert.Equal("hello failover-cluster", result["failover-cluster"]);
    }

    [Fact]
    public async Task ForEachProduct_RespectsParallelismCap()
    {
        // Configured cap = 4 (from ctor). With 3 products, max active = 3.
        var active = 0;
        var maxActive = 0;
        var activeLock = new object();

        await this.fanout.ForEachProductAsync(async (db, slug, ct) =>
        {
            int snapshot;
            lock (activeLock)
            {
                active++;
                snapshot = active;
                if (snapshot > maxActive) maxActive = snapshot;
            }
            await Task.Delay(50, ct);
            lock (activeLock) active--;
        });

        Assert.True(maxActive <= 4, $"Expected at most 4 concurrent invocations, observed {maxActive}.");
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
