// -----------------------------------------------------------------------
// <copyright file="Phase73SlugPropagationTests.cs" company="Microsoft Corp.">
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
/// Phase 73 Stage 2B — verifies that pushing an AsyncLocal slug at a
/// writer entry point propagates through every nested
/// <see cref="IServiceScopeFactory.CreateScope"/> call to
/// <see cref="ProductDbContextFactory.CreateForCurrentRequest"/>, so
/// background workers (orchestrator + resume sweeper + child scopes
/// they open) all resolve <see cref="ProductDbContext"/> against the
/// correct per-product DB.
/// </summary>
[Collection("Phase73SerialTests")]
public class Phase73SlugPropagationTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-slugprop-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;

    public Phase73SlugPropagationTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
        SplitMarkerCache.ResetForTesting();

        // Seed the control DB with the forge_seed_state marker so the
        // factory honours per-product files, plus a per-product file
        // on disk for the assertion target.
        using var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText =
                "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES ($k, $at);";
            ins.Parameters.AddWithValue("$k", SplitMarkerCache.MarkerKey);
            ins.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }
        SplitMarkerCache.ResetForTesting();
        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);
        File.WriteAllText(this.paths.ProductDbPath("failover-cluster"), string.Empty);
    }

    public void Dispose()
    {
        SplitMarkerCache.ResetForTesting();
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void PushAsyncLocalSlug_RoutesCreateForCurrentRequestToPerProductDb()
    {
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        using (ProductDbContextFactory.PushAsyncLocalSlug("code-intelligence"))
        {
            var ctx = factory.CreateForCurrentRequest();
            Assert.Contains("code-intelligence.db", ctx.Database.GetDbConnection().ConnectionString);
        }
    }

    [Fact]
    public void NoSlugPush_FallsBackToControlDb()
    {
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        var ctx = factory.CreateForCurrentRequest();
        Assert.Contains("forge.db", ctx.Database.GetDbConnection().ConnectionString);
        Assert.DoesNotContain("code-intelligence.db", ctx.Database.GetDbConnection().ConnectionString);
    }

    [Fact]
    public async Task SlugPush_PropagatesAcrossAsyncContinuations()
    {
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        using (ProductDbContextFactory.PushAsyncLocalSlug("failover-cluster"))
        {
            await Task.Yield();
            await Task.Delay(5);
            var ctx = factory.CreateForCurrentRequest();
            Assert.Contains("failover-cluster.db", ctx.Database.GetDbConnection().ConnectionString);
        }
    }

    [Fact]
    public async Task SlugPush_RestoredOnScopeDispose()
    {
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        using (ProductDbContextFactory.PushAsyncLocalSlug("code-intelligence"))
        {
            using (ProductDbContextFactory.PushAsyncLocalSlug("failover-cluster"))
            {
                var inner = factory.CreateForCurrentRequest();
                Assert.Contains("failover-cluster.db", inner.Database.GetDbConnection().ConnectionString);
            }

            // After the inner scope disposes, the outer slug must come
            // back into effect for any new scope creation.
            await Task.Yield();
            var factory2 = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());
            var outerAgain = factory2.CreateForCurrentRequest();
            Assert.Contains("code-intelligence.db", outerAgain.Database.GetDbConnection().ConnectionString);
        }
    }

    [Fact]
    public void NullSlug_IsNoOp_FallsBackToControlDb()
    {
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        using (ProductDbContextFactory.PushAsyncLocalSlug(null))
        {
            var ctx = factory.CreateForCurrentRequest();
            Assert.Contains("forge.db", ctx.Database.GetDbConnection().ConnectionString);
        }
    }

    [Fact]
    public void SlugPush_OverridesHttpContextSlug()
    {
        var http = new TestHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        http.HttpContext!.Items[ProductDbContextFactory.SlugItemKey] = "code-intelligence";

        var factory = new ProductDbContextFactory(this.paths, http);
        using (ProductDbContextFactory.PushAsyncLocalSlug("failover-cluster"))
        {
            // AsyncLocal has priority over HttpContext per the factory's
            // resolution order, so the push wins.
            var ctx = factory.CreateForCurrentRequest();
            Assert.Contains("failover-cluster.db", ctx.Database.GetDbConnection().ConnectionString);
        }
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
