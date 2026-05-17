// -----------------------------------------------------------------------
// <copyright file="Phase73HalfAppliedGuardTests.cs" company="Microsoft Corp.">
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
/// Phase 73 — guard against half-applied splits.
///
/// <para>Pre-fix, the operator could trip into a state where the
/// <see cref="Forge.Api.Persistence.Split.SplitApplyService"/> created
/// per-product DB files but never stamped the
/// <c>phase73-split-applied</c> marker. The factory would still pick
/// up the per-product file on read, while writers (which fell through
/// <see cref="ProductDbContextFactory.CreateForCurrentRequest"/> to the
/// control DB) kept landing rows in <c>forge.db</c>. The UI saw a
/// frozen snapshot.</para>
///
/// <para>Stage 1 fix: <see cref="ProductDbContextFactory.CreateFor"/>
/// consults <see cref="SplitMarkerCache.IsPhase73Applied"/> as a hard
/// gate. Without the marker, even an existing per-product file is
/// ignored and reads route to control DB — exactly matching where
/// writers are still landing rows. Drift cannot accumulate.</para>
/// </summary>
[Collection("Phase73SerialTests")]
public class Phase73HalfAppliedGuardTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), $"forge-phase73guard-{Guid.NewGuid():N}");
    private readonly IForgeDbPaths paths;

    public Phase73HalfAppliedGuardTests()
    {
        Directory.CreateDirectory(this.testRoot);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        this.paths = new ForgeDbPaths(cfg);
        // Tests share the static cache by construction; clear it so
        // each test starts from a clean probe.
        SplitMarkerCache.ResetForTesting();
        // Tests don't run the Phase D migration chain, so create the
        // control DB explicitly with an empty schema so the cache's
        // probe doesn't short-circuit on a missing file.
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
    public void IsPhase73Applied_ReturnsFalse_OnFreshControlDb()
    {
        Assert.False(SplitMarkerCache.IsPhase73Applied(this.paths));
    }

    [Fact]
    public void IsPhase73Applied_ReturnsFalse_WhenSeedTableExistsButRowMissing()
    {
        // Create the table without the marker row.
        using (var conn = new SqliteConnection($"Data Source={this.paths.ControlDbPath()}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);"
                + "INSERT INTO forge_seed_state (Key, DoneAt) VALUES ('phase22-backfill-done', '2026-05-03T22:53:38.6212564+00:00');";
            cmd.ExecuteNonQuery();
        }
        SplitMarkerCache.ResetForTesting();

        Assert.False(SplitMarkerCache.IsPhase73Applied(this.paths));
    }

    [Fact]
    public void IsPhase73Applied_ReturnsTrue_AfterMarkerStamped()
    {
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        Assert.True(SplitMarkerCache.IsPhase73Applied(this.paths));
    }

    [Fact]
    public void IsPhase73Applied_CachesAfterFirstProbe()
    {
        // First probe: marker absent.
        Assert.False(SplitMarkerCache.IsPhase73Applied(this.paths));

        // Stamp marker — but DO NOT reset cache.
        StampMarker();

        // Cached result is still false; marker flip is invisible until
        // ResetForTesting (or, in prod, until process restart).
        Assert.False(SplitMarkerCache.IsPhase73Applied(this.paths));

        SplitMarkerCache.ResetForTesting();
        Assert.True(SplitMarkerCache.IsPhase73Applied(this.paths));
    }

    [Fact]
    public void Factory_RoutesToControlDb_WhenPerProductFileExistsButMarkerMissing()
    {
        // Half-applied state: per-product DB file exists but no marker.
        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);
        SplitMarkerCache.ResetForTesting();

        using var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());
        var ctx = factory.CreateFor("code-intelligence");

        var cs = ctx.Database.GetDbConnection().ConnectionString;
        Assert.Contains("forge.db", cs);
        Assert.DoesNotContain("code-intelligence.db", cs);
    }

    [Fact]
    public void Factory_RoutesToPerProductDb_WhenMarkerStamped()
    {
        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        using var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());
        var ctx = factory.CreateFor("code-intelligence");

        var cs = ctx.Database.GetDbConnection().ConnectionString;
        Assert.Contains("code-intelligence.db", cs);
    }

    [Fact]
    public void Factory_RoutesToControlDb_WhenMarkerStampedButFileMissing()
    {
        // Marker stamped but per-product file removed: factory falls
        // back to control DB. This mirrors the legacy Phase D interim
        // pre-Phase-F path so admin paths keep working if the
        // operator deletes a per-product file by mistake.
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        using var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());
        var ctx = factory.CreateFor("code-intelligence");

        var cs = ctx.Database.GetDbConnection().ConnectionString;
        Assert.Contains("forge.db", cs);
    }

    [Fact]
    public void Factory_DoesNotPickUpFileDroppedAfterFirstResolve()
    {
        // Marker absent. Factory resolves once -> control DB. Even if
        // someone drops a per-product file later, the same factory
        // instance keeps its cached control-DB context for that slug.
        // (A new factory or a new request would re-probe.)
        using var factory = new ProductDbContextFactory(this.paths, new TestHttpContextAccessor());
        var ctx1 = factory.CreateFor("code-intelligence");
        Assert.Contains("forge.db", ctx1.Database.GetDbConnection().ConnectionString);

        File.WriteAllText(this.paths.ProductDbPath("code-intelligence"), string.Empty);
        StampMarker();
        SplitMarkerCache.ResetForTesting();

        var ctx2 = factory.CreateFor("code-intelligence");
        Assert.Same(ctx1, ctx2);
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
        var k = ins.CreateParameter();
        k.ParameterName = "$k";
        k.Value = SplitMarkerCache.MarkerKey;
        ins.Parameters.Add(k);
        var at = ins.CreateParameter();
        at.ParameterName = "$at";
        at.Value = DateTimeOffset.UtcNow.ToString("O");
        ins.Parameters.Add(at);
        ins.ExecuteNonQuery();
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
