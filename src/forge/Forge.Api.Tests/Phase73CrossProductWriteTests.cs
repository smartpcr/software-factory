// -----------------------------------------------------------------------
// <copyright file="Phase73CrossProductWriteTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase 73 Stage 2E — end-to-end test that writes against two products
/// land in their respective per-product DBs after a clean
/// <see cref="SplitApplyService.ApplyAsync"/> run + marker stamp.
/// Catches regressions where a writer doesn't push the AsyncLocal slug
/// (would route to forge.db post-split and silently lose the row when
/// the source product tables are dropped).
/// </summary>
[Collection("Phase73SerialTests")]
public class Phase73CrossProductWriteTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-crossprodtest-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;
    private readonly SplitDryRunService analyzer;
    private readonly SplitApplyService applier;

    public Phase73CrossProductWriteTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
        this.analyzer = new SplitDryRunService(this.paths);
        this.applier = new SplitApplyService(this.paths, this.analyzer);
        SplitMarkerCache.ResetForTesting();
    }

    public void Dispose()
    {
        SplitMarkerCache.ResetForTesting();
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Writes_AfterSplitApplied_RouteToCorrectPerProductDb()
    {
        await this.SeedSchemaAndDataAsync();

        // Run the full Apply so the marker is stamped and the source
        // product tables are dropped.
        var applyResult = await this.applier.ApplyAsync();
        Assert.True(applyResult.Success, string.Join("\n", applyResult.Errors));

        // Clear cache so the factory sees the just-stamped marker.
        SplitMarkerCache.ResetForTesting();

        // Build a factory and write a workstream against atlas via slug push.
        var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());

        using (ProductDbContextFactory.PushAsyncLocalSlug("atlas"))
        {
            var ctx = factory.CreateForCurrentRequest();
            ctx.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-atlas-new",
                Name = "atlas-new",
                Branch = "ws/atlas-new",
                Epic = "E",
                Status = "active",
                RepoId = "r-atlas-1",
            });
            await ctx.SaveChangesAsync();
        }

        using (ProductDbContextFactory.PushAsyncLocalSlug("raft"))
        {
            var ctx = factory.CreateForCurrentRequest();
            ctx.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-raft-new",
                Name = "raft-new",
                Branch = "ws/raft-new",
                Epic = "E",
                Status = "active",
                RepoId = "r-raft-1",
            });
            await ctx.SaveChangesAsync();
        }

        // Asserts: atlas.db has both atlas WS (the seeded 3 + the new one);
        // raft.db has both raft WS (seeded 1 + the new one); neither WS
        // appears in the other DB; and forge.db has no workstreams table
        // anymore (dropped at the end of apply).
        var atlasIds = await ListIdsAsync(this.paths.ProductDbPath("atlas"), "workstreams");
        var raftIds = await ListIdsAsync(this.paths.ProductDbPath("raft"), "workstreams");

        Assert.Contains("ws-atlas-new", atlasIds);
        Assert.Contains("ws-atlas-1", atlasIds);
        Assert.DoesNotContain("ws-raft-1", atlasIds);
        Assert.DoesNotContain("ws-raft-new", atlasIds);

        Assert.Contains("ws-raft-new", raftIds);
        Assert.Contains("ws-raft-1", raftIds);
        Assert.DoesNotContain("ws-atlas-1", raftIds);
        Assert.DoesNotContain("ws-atlas-new", raftIds);

        // Source forge.db must NOT have the workstreams table anymore.
        Assert.False(await TableExistsAsync(this.paths.ControlDbPath(), "workstreams"));
    }

    private async Task SeedSchemaAndDataAsync()
    {
        var ctlOpt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using (var ctl = new ControlDbContext(ctlOpt))
        {
            var script = ctl.Database.GenerateCreateScript();
            foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = stmt.Trim();
                if (trimmed.Length > 0) await ctl.Database.ExecuteSqlRawAsync(trimmed);
            }

            ctl.Products.AddRange(
                new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" },
                new ProductRow { Id = "raft", Slug = "raft", Name = "Raft" });
            await ctl.SaveChangesAsync();
        }

        var prodOpt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using (var prod = new ProductDbContext(prodOpt))
        {
            var script = prod.Database.GenerateCreateScript();
            foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = stmt.Trim();
                if (trimmed.Length > 0) await prod.Database.ExecuteSqlRawAsync(trimmed);
            }

            // Phase 80.B — repos live on the product DB now.
            prod.Repos.AddRange(
                new RepoRow { Id = "r-atlas-1", ProductId = "atlas", Kind = "github", RemoteUrl = "https://x/a1", DisplayName = "a1", DefaultBranch = "main", CloneRoot = "C:\\a1" },
                new RepoRow { Id = "r-raft-1", ProductId = "raft", Kind = "github", RemoteUrl = "https://x/r1", DisplayName = "r1", DefaultBranch = "main", CloneRoot = "C:\\r1" });
            prod.Workstreams.AddRange(
                new WorkstreamRow { Id = "ws-atlas-1", Name = "a1", Branch = "ws/a1", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
                new WorkstreamRow { Id = "ws-atlas-2", Name = "a2", Branch = "ws/a2", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
                new WorkstreamRow { Id = "ws-atlas-3", Name = "a3", Branch = "ws/a3", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
                new WorkstreamRow { Id = "ws-raft-1", Name = "r1", Branch = "ws/r1", Epic = "E", Status = "active", RepoId = "r-raft-1" });
            await prod.SaveChangesAsync();
        }
    }

    private static async Task<HashSet<string>> ListIdsAsync(string dbPath, string tableName)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id FROM \"{tableName}\";";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetString(0));
        return ids;
    }

    private static async Task<bool> TableExistsAsync(string dbPath, string tableName)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", tableName);
        var o = await cmd.ExecuteScalarAsync();
        return o is not null && Convert.ToInt32(o) > 0;
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
