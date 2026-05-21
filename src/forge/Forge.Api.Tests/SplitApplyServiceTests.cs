// -----------------------------------------------------------------------
// <copyright file="SplitApplyServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase F — end-to-end tests for <see cref="SplitApplyService"/>.
/// Builds a multi-product seed DB via EF Core (so the schema matches
/// production), runs the apply, then asserts:
/// <list type="bullet">
///   <item>per-product <c>{slug}.db</c> files exist with partitioned rows;</item>
///   <item>source <c>forge.db</c> has only control tables left;</item>
///   <item>row totals across products equal the source's classified rows;</item>
///   <item>backup file exists at the expected path;</item>
///   <item>a second call returns <c>AlreadyApplied=true</c> without writing.</item>
/// </list>
/// </summary>
public class SplitApplyServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-applytest-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;
    private readonly SplitDryRunService analyzer;
    private readonly SplitApplyService applier;

    public SplitApplyServiceTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
        this.analyzer = new SplitDryRunService(this.paths);
        this.applier = new SplitApplyService(this.paths, this.analyzer);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ApplyAsync_OnEmptyDb_ReportsNoData()
    {
        // Build an empty schema.
        await this.SeedSchemaAsync();

        var result = await this.applier.ApplyAsync();

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.False(result.AlreadyApplied);
        Assert.NotNull(result.BackupPath);
        Assert.Empty(result.ProductDbs);
        Assert.Equal(0, result.RowsCopied);
    }

    [Fact]
    public async Task ApplyAsync_SecondCall_ReturnsAlreadyApplied()
    {
        await this.SeedSchemaAsync();
        await this.applier.ApplyAsync();

        var second = await this.applier.ApplyAsync();
        Assert.True(second.AlreadyApplied);
        Assert.True(second.Success);
        Assert.Empty(second.ProductDbs);
    }

    [Fact]
    public async Task ApplyAsync_PartitionsRowsAcrossProducts()
    {
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();

        var preReport = await this.analyzer.AnalyzeAsync();
        var preWorkstreams = preReport.Tables.First(t => t.TableName == "workstreams").TotalRows;
        var preWorkItems = preReport.Tables.First(t => t.TableName == "work_items").TotalRows;
        Assert.True(preWorkstreams > 0);
        Assert.True(preWorkItems > 0);

        var result = await this.applier.ApplyAsync();
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(2, result.ProductDbs.Count); // atlas, raft

        // Per-product DB files exist.
        Assert.True(File.Exists(this.paths.ProductDbPath("atlas")));
        Assert.True(File.Exists(this.paths.ProductDbPath("raft")));

        // Source forge.db has product tables dropped (workstreams gone).
        await using var srcConn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}");
        await srcConn.OpenAsync();
        await using var checkCmd = srcConn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workstreams';";
        Assert.Equal(0L, await checkCmd.ExecuteScalarAsync());

        // Per-product DBs have the right partitioned rows.
        var atlasRows = await CountRowsAsync(this.paths.ProductDbPath("atlas"), "workstreams");
        var raftRows = await CountRowsAsync(this.paths.ProductDbPath("raft"), "workstreams");
        Assert.Equal(preWorkstreams, atlasRows + raftRows);
    }

    [Fact]
    public async Task ApplyAsync_BackupFileIsCreated()
    {
        await this.SeedSchemaAsync();
        var result = await this.applier.ApplyAsync();
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task SplitApplyResult_SuccessImpliesNoErrors()
    {
        var ok = new SplitApplyResult(false, "/x", new[] { "a.db" }, 5, Array.Empty<string>());
        var bad = new SplitApplyResult(false, "/x", Array.Empty<string>(), 0, new[] { "boom" });

        Assert.True(ok.Success);
        Assert.False(bad.Success);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task SeedSchemaAsync()
    {
        // Phase G.1 — lay down both control + product schema in the same
        // forge.db file. EnsureCreated is one-shot per file (it skips
        // creation if any table exists), so we run ControlDbContext's
        // EnsureCreated and then execute ProductDbContext's
        // GenerateCreateScript directly against the same connection.
        var ctlOpt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using (var ctl = new ControlDbContext(ctlOpt))
        {
            await ctl.Database.EnsureCreatedAsync();
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
        }
    }

    private async Task SeedDataAsync()
    {
        var ctlOpt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using var ctl = new ControlDbContext(ctlOpt);

        ctl.Products.AddRange(
            new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" },
            new ProductRow { Id = "raft", Slug = "raft", Name = "Raft" });
        await ctl.SaveChangesAsync();

        var prodOpt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using var prod = new ProductDbContext(prodOpt);

        // Phase 80.B — repos live on the product DB now.
        prod.Repos.AddRange(
            new RepoRow { Id = "r-atlas-1", ProductId = "atlas", Kind = "github", RemoteUrl = "https://x/a1", DisplayName = "a1", DefaultBranch = "main", CloneRoot = "C:\\a1" },
            new RepoRow { Id = "r-raft-1", ProductId = "raft", Kind = "github", RemoteUrl = "https://x/r1", DisplayName = "r1", DefaultBranch = "main", CloneRoot = "C:\\r1" });

        // 3 workstreams in atlas, 1 in raft.
        prod.Workstreams.AddRange(
            new WorkstreamRow { Id = "ws-atlas-1", Name = "a1", Branch = "ws/a1", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-atlas-2", Name = "a2", Branch = "ws/a2", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-atlas-3", Name = "a3", Branch = "ws/a3", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-raft-1", Name = "r1", Branch = "ws/r1", Epic = "E", Status = "active", RepoId = "r-raft-1" });

        // 2 work items in atlas, 1 in raft.
        prod.WorkItems.AddRange(
            new WorkItemRow { Id = "wi-atlas-1", Title = "wi-a1", Type = "task", State = "active", RepoId = "r-atlas-1" },
            new WorkItemRow { Id = "wi-atlas-2", Title = "wi-a2", Type = "task", State = "active", RepoId = "r-atlas-1" },
            new WorkItemRow { Id = "wi-raft-1", Title = "wi-r1", Type = "task", State = "active", RepoId = "r-raft-1" });

        await prod.SaveChangesAsync();
    }

    private static async Task<int> CountRowsAsync(string dbPath, string tableName)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        var o = await cmd.ExecuteScalarAsync();
        return o is null ? 0 : Convert.ToInt32(o);
    }
}
