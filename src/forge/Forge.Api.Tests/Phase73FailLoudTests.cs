// -----------------------------------------------------------------------
// <copyright file="Phase73FailLoudTests.cs" company="Microsoft Corp.">
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
/// Phase 73 Stage 2D — verifies the opt-in fail-loud behaviour of
/// <see cref="ProductDbContextFactory.CreateForCurrentRequest"/> once
/// the split marker has been stamped. Default-off keeps the existing
/// <c>CreateUnscoped()</c> fallback behaviour for admin paths; opt-in
/// surfaces the bug class where a background writer forgot to push a
/// slug AND the marker is set (which would otherwise silently route
/// the write to <c>forge.db</c> after the split source tables have
/// been dropped).
/// </summary>
[Collection("Phase73SerialTests")]
public class Phase73FailLoudTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-failloud-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;

    public Phase73FailLoudTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
        SplitMarkerCache.ResetForTesting();

        using var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}");
        conn.Open();
    }

    public void Dispose()
    {
        SplitMarkerCache.ResetForTesting();
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void MarkerAbsent_FailLoudFlagOn_DoesNotThrow_AndFallsBackToControl()
    {
        var cfg = BuildConfig(failLoud: true);
        var factory = new ProductDbContextFactory(
            this.paths, new TestHttpContextAccessor(), logger: null, cfg);

        var ctx = factory.CreateForCurrentRequest();
        Assert.Contains("forge.db", ctx.Database.GetDbConnection().ConnectionString);
    }

    [Fact]
    public void MarkerPresent_FailLoudFlagOff_DoesNotThrow_AndFallsBackToControl()
    {
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        var cfg = BuildConfig(failLoud: false);
        var factory = new ProductDbContextFactory(
            this.paths, new TestHttpContextAccessor(), logger: null, cfg);

        var ctx = factory.CreateForCurrentRequest();
        Assert.Contains("forge.db", ctx.Database.GetDbConnection().ConnectionString);
    }

    [Fact]
    public void MarkerPresent_FailLoudFlagOn_NoSlugPush_Throws()
    {
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        var cfg = BuildConfig(failLoud: true);
        var factory = new ProductDbContextFactory(
            this.paths, new TestHttpContextAccessor(), logger: null, cfg);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateForCurrentRequest());
        Assert.Contains("ProductDbContextFactory.CreateForCurrentRequest", ex.Message, StringComparison.Ordinal);
        Assert.Contains(this.paths.ControlDbPath(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PushAsyncLocalSlug", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerPresent_FailLoudFlagOn_WithSlugPush_DoesNotThrow()
    {
        StampMarker();
        SplitMarkerCache.ResetForTesting();
        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);

        var cfg = BuildConfig(failLoud: true);
        var factory = new ProductDbContextFactory(
            this.paths, new TestHttpContextAccessor(), logger: null, cfg);

        using (ProductDbContextFactory.PushAsyncLocalSlug("code-intelligence"))
        {
            var ctx = factory.CreateForCurrentRequest();
            Assert.Contains("code-intelligence.db", ctx.Database.GetDbConnection().ConnectionString);
        }
    }

    [Fact]
    public void MarkerPresent_FailLoudFlagOn_WithHttpHeader_DoesNotThrow()
    {
        StampMarker();
        SplitMarkerCache.ResetForTesting();
        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);

        var http = new TestHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        http.HttpContext!.Items[ProductDbContextFactory.SlugItemKey] = "code-intelligence";

        var cfg = BuildConfig(failLoud: true);
        var factory = new ProductDbContextFactory(this.paths, http, logger: null, cfg);

        var ctx = factory.CreateForCurrentRequest();
        Assert.Contains("code-intelligence.db", ctx.Database.GetDbConnection().ConnectionString);
    }

    private static IConfiguration BuildConfig(bool failLoud)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:FailLoudOnUnscoped"] = failLoud ? "true" : "false",
        }).Build();
    }

    private void StampMarker()
    {
        using var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText =
            "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
        create.ExecuteNonQuery();
        using var ins = conn.CreateCommand();
        ins.CommandText =
            "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES ($k, $at);";
        ins.Parameters.AddWithValue("$k", SplitMarkerCache.MarkerKey);
        ins.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        ins.ExecuteNonQuery();
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
