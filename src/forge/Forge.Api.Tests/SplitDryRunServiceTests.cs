// -----------------------------------------------------------------------
// <copyright file="SplitDryRunServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase B — unit tests for <see cref="SplitDryRunService"/>. Seeds a real
/// SQLite file with known rows and asserts the analyzer returns the right
/// partition counts + orphan warnings + unknown-table detections.
///
/// <para>The tests bypass EF Core and the migration runners — they just
/// CREATE the minimal schema the catalog references. That keeps the
/// fixture small and forces the test to mirror the actual SQL the
/// analyzer emits, catching schema drift between
/// <c>Entities.cs</c> and <c>DbBoundary.cs</c>.</para>
/// </summary>
public class SplitDryRunServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-splitrun-{Guid.NewGuid():N}");
    private readonly string dbPath;
    private readonly SplitDryRunService analyzer;

    public SplitDryRunServiceTests()
    {
        Directory.CreateDirectory(this.testRoot);
        this.dbPath = Path.Combine(this.testRoot, "forge.db");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = this.testRoot,
        }).Build();
        var paths = new ForgeDbPaths(cfg);
        this.analyzer = new SplitDryRunService(paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyDbFile_ReturnsEmptyReport()
    {
        var report = await this.analyzer.AnalyzeAsync();
        Assert.Equal(this.dbPath, report.ControlDbPath);
        Assert.Equal(0, report.TotalRows);
        Assert.Equal(0, report.ControlRows);
        Assert.Equal(0, report.ProductRows);
        Assert.Empty(report.Tables);
        Assert.Empty(report.Products);
    }

    [Fact]
    public async Task AnalyzeAsync_PartitionsRowsAcrossProducts()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE repos (Id TEXT PRIMARY KEY, ProductId TEXT)");
            Exec(conn, "CREATE TABLE workstreams (Id TEXT PRIMARY KEY, RepoId TEXT)");
            Exec(conn, "INSERT INTO products VALUES ('atlas','atlas'), ('failover','failover-cluster')");
            Exec(conn, "INSERT INTO repos VALUES ('r1','atlas'), ('r2','atlas'), ('r3','failover')");
            // 3 workstreams in atlas, 2 in failover-cluster.
            Exec(conn, "INSERT INTO workstreams VALUES ('w1','r1'), ('w2','r1'), ('w3','r2'), ('w4','r3'), ('w5','r3')");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var wsTable = report.Tables.Single(t => t.TableName == "workstreams");

        Assert.Equal("product", wsTable.Kind);
        Assert.Equal("viarepoid", wsTable.Strategy);
        Assert.Equal(5, wsTable.TotalRows);
        Assert.Equal(5, wsTable.ClassifiedRows);
        Assert.Equal(0, wsTable.OrphanRows);

        var atlas = report.Products.Single(p => p.ProductId == "atlas");
        var failover = report.Products.Single(p => p.ProductId == "failover");
        Assert.Equal(3, atlas.RowsPerTable["workstreams"]);
        Assert.Equal(2, failover.RowsPerTable["workstreams"]);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsBrokenForeignKeyAsOrphan()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE repos (Id TEXT PRIMARY KEY, ProductId TEXT)");
            Exec(conn, "CREATE TABLE workstreams (Id TEXT PRIMARY KEY, RepoId TEXT)");
            Exec(conn, "INSERT INTO products VALUES ('atlas','atlas')");
            Exec(conn, "INSERT INTO repos VALUES ('r1','atlas')");
            // w-orphan-1 + w-orphan-2 reference a repo that doesn't exist.
            Exec(conn, "INSERT INTO workstreams VALUES ('w1','r1'), ('w-orphan-1','r-missing'), ('w-orphan-2','r-also-missing')");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var wsTable = report.Tables.Single(t => t.TableName == "workstreams");

        Assert.Equal(3, wsTable.TotalRows);
        Assert.Equal(1, wsTable.ClassifiedRows);
        Assert.Equal(2, wsTable.OrphanRows);

        var orphan = report.Orphans.Single(o => o.TableName == "workstreams");
        Assert.Equal("broken-foreign-key", orphan.Kind);
        Assert.Equal(2, orphan.Count);
        Assert.Equal(2, report.OrphanRows);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsUnknownProductAsOrphan()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE product_stories (Id TEXT PRIMARY KEY, ProductId TEXT)");
            Exec(conn, "INSERT INTO products VALUES ('atlas','atlas')");
            // s1 → atlas (good). s2 → 'ghost' which is not in products.
            Exec(conn, "INSERT INTO product_stories VALUES ('s1','atlas'), ('s2','ghost')");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var unknown = report.Orphans.Single(o => o.TableName == "product_stories");
        Assert.Equal("unknown-product", unknown.Kind);
        Assert.Equal(1, unknown.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsNoPartitionKeyTablesAsBucket()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE idempotency (Key TEXT PRIMARY KEY, Value TEXT)");
            Exec(conn, "INSERT INTO idempotency VALUES ('k1','{}'), ('k2','{}')");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var idemp = report.Tables.Single(t => t.TableName == "idempotency");
        Assert.Equal("nopartitionkey", idemp.Strategy);
        Assert.Equal(2, idemp.TotalRows);
        Assert.Equal(0, idemp.ClassifiedRows);
        Assert.Equal(2, idemp.OrphanRows);

        var warn = report.Orphans.Single(o => o.TableName == "idempotency");
        Assert.Equal("no-partition-strategy", warn.Kind);
        Assert.Contains("idempotency", warn.Detail);
    }

    [Fact]
    public async Task AnalyzeAsync_ChainedFkResolvesViaWorkItem()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE repos (Id TEXT PRIMARY KEY, ProductId TEXT)");
            Exec(conn, "CREATE TABLE work_items (Id TEXT PRIMARY KEY, RepoId TEXT)");
            Exec(conn, "CREATE TABLE deliverables (Id TEXT PRIMARY KEY, WorkItemId TEXT)");
            Exec(conn, "INSERT INTO products VALUES ('atlas','atlas'), ('failover','failover-cluster')");
            Exec(conn, "INSERT INTO repos VALUES ('r1','atlas'), ('r2','failover')");
            Exec(conn, "INSERT INTO work_items VALUES ('wi-1','r1'), ('wi-2','r1'), ('wi-3','r2')");
            // 2 deliverables for atlas (via wi-1, wi-2), 3 for failover (wi-3), 1 orphan.
            Exec(conn, "INSERT INTO deliverables VALUES ('d1','wi-1'), ('d2','wi-1'), ('d3','wi-3'), ('d4','wi-3'), ('d5','wi-3'), ('d-orphan','wi-deleted')");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var del = report.Tables.Single(t => t.TableName == "deliverables");
        Assert.Equal("viaworkitemid", del.Strategy);
        Assert.Equal(6, del.TotalRows);
        Assert.Equal(5, del.ClassifiedRows);
        Assert.Equal(1, del.OrphanRows);

        var atlas = report.Products.Single(p => p.ProductId == "atlas");
        var failover = report.Products.Single(p => p.ProductId == "failover");
        Assert.Equal(2, atlas.RowsPerTable["deliverables"]);
        Assert.Equal(3, failover.RowsPerTable["deliverables"]);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsUnknownTablesNotInCatalog()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            // Table not in DbBoundary.AllTables — should surface as unknown.
            Exec(conn, "CREATE TABLE legacy_thing (Id TEXT PRIMARY KEY, Stuff TEXT)");
            Exec(conn, "INSERT INTO legacy_thing VALUES ('a','x'), ('b','y')");
            // SQLite system tables + EF migration history must be filtered out.
            Exec(conn, "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT)");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var legacy = report.UnknownTables.Single();
        Assert.Equal("legacy_thing", legacy.TableName);
        Assert.Equal(2, legacy.RowCount);
        Assert.DoesNotContain(report.UnknownTables, t => t.TableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.UnknownTables, t => string.Equals(t.TableName, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_DirectProductId_NullCountsAsOrphan()
    {
        await SeedAsync(conn =>
        {
            Exec(conn, "CREATE TABLE products (Id TEXT PRIMARY KEY, Slug TEXT)");
            Exec(conn, "CREATE TABLE epics (Id TEXT PRIMARY KEY, ProductId TEXT)");
            Exec(conn, "INSERT INTO products VALUES ('atlas','atlas')");
            Exec(conn, "INSERT INTO epics VALUES ('e1','atlas'), ('e2','atlas'), ('e3',NULL)");
        });

        var report = await this.analyzer.AnalyzeAsync();
        var epics = report.Tables.Single(t => t.TableName == "epics");
        Assert.Equal("directproductid", epics.Strategy);
        Assert.Equal(3, epics.TotalRows);
        Assert.Equal(2, epics.ClassifiedRows);
        Assert.Equal(1, epics.OrphanRows);
        Assert.Single(report.Orphans, o => o.TableName == "epics" && o.Kind == "broken-foreign-key");
    }

    [Fact]
    public async Task DbBoundary_CatalogHasNoDuplicateTableNames()
    {
        var dupes = DbBoundary.AllTables
            .GroupBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void DbBoundary_NoPartitionKeyTables_HaveNullColumn()
    {
        var bad = DbBoundary.AllTables
            .Where(t => t.Strategy == PartitionStrategy.NoPartitionKey && t.PartitionColumn is not null)
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void DbBoundary_ControlTables_AreGlobal()
    {
        var bad = DbBoundary.AllTables
            .Where(t => t.Kind == DbKind.Control && t.Strategy != PartitionStrategy.Global)
            .ToList();
        Assert.Empty(bad);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task SeedAsync(Action<SqliteConnection> seed)
    {
        await using var conn = new SqliteConnection($"Data Source={this.dbPath}");
        await conn.OpenAsync();
        seed(conn);
        await conn.CloseAsync();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
