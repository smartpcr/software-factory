// -----------------------------------------------------------------------
// <copyright file="SplitResumeServiceTests.cs" company="Microsoft Corp.">
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
/// Phase 73 Stage 2C — tests for <see cref="SplitApplyService.ResumeAsync"/>.
/// Distinct from <see cref="SplitApplyServiceTests"/> in that Resume is
/// the explicit recovery path for a HALF-APPLIED split (per-product
/// files exist but marker is unstamped and source tables still hold
/// rows that landed there after the partial apply).
/// </summary>
[Collection("Phase73SerialTests")]
public class SplitResumeServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-resumetest-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;
    private readonly SplitDryRunService analyzer;
    private readonly SplitApplyService applier;

    public SplitResumeServiceTests()
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
    public async Task ResumeAsync_OnFreshDb_ReturnsErrorWhenPerProductFilesMissing()
    {
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();

        // Resume requires per-product files; on a fresh setup they don't
        // exist, so it must refuse with an actionable error rather than
        // silently fall through.
        var result = await this.applier.ResumeAsync();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResumeAsync_OnAlreadyAppliedDb_ReturnsAlreadyApplied()
    {
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();
        await this.applier.ApplyAsync();

        var result = await this.applier.ResumeAsync();

        Assert.True(result.AlreadyApplied);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ResumeAsync_OnHalfApplied_CompletesAndStampsMarker()
    {
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();

        // Simulate a half-applied state: apply normally, then UNDO the
        // marker stamp + UNDO the source DROPs by re-seeding the source
        // rows. This mirrors the production half-apply where the
        // partition copy + per-product file creation succeeded but the
        // marker stamp + source DROP didn't.
        await this.applier.ApplyAsync();

        // Knock the marker back out.
        await using (var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}"))
        {
            await conn.OpenAsync();
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase73-split-applied';";
            await del.ExecuteNonQueryAsync();
        }

        // Re-create the dropped source product tables and re-seed them
        // (mirrors a half-apply where DROP never ran and writers kept
        // landing rows in the source).
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();
        SplitMarkerCache.ResetForTesting();

        var result = await this.applier.ResumeAsync();

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.False(result.AlreadyApplied);

        // Marker is stamped post-resume.
        await using (var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM forge_seed_state WHERE Key = 'phase73-split-applied';";
            var o = await cmd.ExecuteScalarAsync();
            Assert.True(Convert.ToInt32(o) > 0);
        }

        // Source has product tables dropped (post-resume == post-apply).
        await using (var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()};Mode=ReadOnly"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='workstreams';";
            var o = await cmd.ExecuteScalarAsync();
            Assert.Null(o);
        }
    }

    [Fact]
    public async Task ResumeAsync_RoutesNewSourceRowsIntoPerProductDb()
    {
        await this.SeedSchemaAsync();
        await this.SeedDataAsync();
        await this.applier.ApplyAsync();

        // Knock the marker back out + re-create source product tables
        // (mirrors half-apply where source DROP never ran). Use the
        // schema replay path to keep the tests honest.
        await using (var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}"))
        {
            await conn.OpenAsync();
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM forge_seed_state WHERE Key = 'phase73-split-applied';";
            await del.ExecuteNonQueryAsync();
        }
        await this.SeedSchemaAsync(); // re-create product tables on source
        await this.SeedDataAsync();   // re-seed rows on source (simulates writer drift)
        SplitMarkerCache.ResetForTesting();

        var result = await this.applier.ResumeAsync();
        Assert.True(result.Success, string.Join("\n", result.Errors));

        // Atlas DB has its 3 workstreams; raft DB has its 1.
        var atlasWs = await CountRowsAsync(this.paths.ProductDbPath("atlas"), "workstreams");
        var raftWs = await CountRowsAsync(this.paths.ProductDbPath("raft"), "workstreams");
        Assert.Equal(3, atlasWs);
        Assert.Equal(1, raftWs);
    }

    // ── helpers (mirror SplitApplyServiceTests) ────────────────────────

    private async Task SeedSchemaAsync()
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
                if (trimmed.Length == 0) continue;
                try { await ctl.Database.ExecuteSqlRawAsync(trimmed); }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    // Tolerate re-runs after a partial drop+recreate (Resume tests).
                }
            }
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
                if (trimmed.Length == 0) continue;
                try { await prod.Database.ExecuteSqlRawAsync(trimmed); }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    // Tolerate re-runs after a partial drop+recreate (Resume tests).
                }
            }
        }
    }

    private async Task SeedDataAsync()
    {
        var ctlOpt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using var ctl = new ControlDbContext(ctlOpt);

        if (!await ctl.Products.AnyAsync(p => p.Id == "atlas"))
        {
            ctl.Products.AddRange(
                new ProductRow { Id = "atlas", Slug = "atlas", Name = "Atlas" },
                new ProductRow { Id = "raft", Slug = "raft", Name = "Raft" });
            await ctl.SaveChangesAsync();
        }

        var prodOpt = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite($"Data Source={this.paths.ControlDbPath()}")
            .Options;
        await using var prod = new ProductDbContext(prodOpt);

        // Phase 80.B — repos live on the product DB now. Guarded separately:
        // resume re-enters after the split moved product tables out, but repos
        // (control-classified) stays in place, so a blind re-add would trip the
        // unique RemoteUrl index.
        if (!await prod.Repos.AnyAsync())
        {
            prod.Repos.AddRange(
                new RepoRow { Id = "r-atlas-1", ProductId = "atlas", Kind = "github", RemoteUrl = "https://x/a1", DisplayName = "a1", DefaultBranch = "main", CloneRoot = "C:\\a1" },
                new RepoRow { Id = "r-raft-1", ProductId = "raft", Kind = "github", RemoteUrl = "https://x/r1", DisplayName = "r1", DefaultBranch = "main", CloneRoot = "C:\\r1" });
            await prod.SaveChangesAsync();
        }

        var existing = await prod.Workstreams.AnyAsync();
        if (existing) return;

        prod.Workstreams.AddRange(
            new WorkstreamRow { Id = "ws-atlas-1", Name = "a1", Branch = "ws/a1", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-atlas-2", Name = "a2", Branch = "ws/a2", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-atlas-3", Name = "a3", Branch = "ws/a3", Epic = "E", Status = "active", RepoId = "r-atlas-1" },
            new WorkstreamRow { Id = "ws-raft-1", Name = "r1", Branch = "ws/r1", Epic = "E", Status = "active", RepoId = "r-raft-1" });

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
