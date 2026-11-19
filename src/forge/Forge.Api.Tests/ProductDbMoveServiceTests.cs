// -----------------------------------------------------------------------
// <copyright file="ProductDbMoveServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Tests.Auth;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// T3 (docs/forge/post-split-product-dbs.md): moving a control-DB-fallback
/// product into its own {slug}.db. Fixture: products p (slug alpha) and q
/// (slug beta) with a repo, spawn, workstream and work item each, all in the
/// control DB, on a host stamped as split.
/// </summary>
public sealed class ProductDbMoveServiceTests
{
    private static readonly string[] CoreTables = { "repos", "story_spawns", "workstreams", "work_items" };

    [Fact]
    public async Task Dry_Run_Reports_The_Products_Rows_And_Writes_Nothing()
    {
        await using var fx = await Fixture.CreateAsync();
        var before = Directory.GetFiles(fx.Folder);

        var result = await fx.Service.MoveAsync("p", dryRun: true);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (var table in CoreTables) Assert.Equal(1, result.Tables.Single(t => t.Table == table).SourceRows);
        Assert.Equal(before.OrderBy(f => f), Directory.GetFiles(fx.Folder).OrderBy(f => f));
    }

    [Fact]
    public async Task Move_Copies_Only_That_Products_Rows_Removes_Them_From_Control_And_Routes_To_The_New_File()
    {
        await using var fx = await Fixture.CreateAsync();

        var result = await fx.Service.MoveAsync("p", dryRun: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(File.Exists(result.BackupPath));
        var alpha = fx.Paths.ProductDbPath("alpha");
        Assert.Equal(alpha, result.ProductDbPath);
        Assert.False(File.Exists(alpha + ProductDbMoveService.MovingSuffix));
        foreach (var table in CoreTables)
        {
            Assert.Equal(1L, Count(alpha, table));
            Assert.Equal(0L, fx.ControlCount("p", table));
            Assert.Equal(1L, fx.ControlCount("q", table));
        }
        Assert.Equal(5L, Count(alpha, "columns"));

        // Forge's own data layer now reads the moved rows from alpha.db.
        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("alpha");
        Assert.Contains("alpha.db", db.Database.GetConnectionString(), StringComparison.OrdinalIgnoreCase);
        var spawn = await db.StorySpawns.AsNoTracking().SingleAsync();
        Assert.Equal(("spawn-p", "story-p", "repo-p"), (spawn.Id, spawn.StoryId, spawn.RepoId));
    }

    [Fact]
    public async Task Columns_Are_Copied_By_Name_When_The_Template_Orders_Them_Differently()
    {
        await using var fx = await Fixture.CreateAsync();
        // Sibling product DB (beta) becomes the template; give it a reversed story_spawns.
        var beta = fx.Paths.ProductDbPath("beta");
        await fx.Provisioner.BuildAsync("beta", beta, CancellationToken.None);
        ReverseColumns(beta, "story_spawns");
        Assert.NotEqual(Columns(fx.ControlPath, "story_spawns")[0], Columns(beta, "story_spawns")[0]);

        var result = await fx.Service.MoveAsync("p", dryRun: false);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var alpha = fx.Paths.ProductDbPath("alpha");
        Assert.Equal(Columns(beta, "story_spawns"), Columns(alpha, "story_spawns"));
        using var conn = OpenReadOnly(alpha);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, StoryId, RepoId, Phase FROM story_spawns;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("spawn-p", "story-p", "repo-p", "implement"), (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    [Fact]
    public async Task An_Existing_Product_Db_File_Is_Refused_Before_Anything_Is_Written()
    {
        await using var fx = await Fixture.CreateAsync();
        var alpha = fx.Paths.ProductDbPath("alpha");
        await File.WriteAllTextAsync(alpha, "not ours");

        var result = await fx.Service.MoveAsync("p", dryRun: false);

        Assert.False(result.Success);
        Assert.Null(result.BackupPath);
        Assert.Equal("not ours", await File.ReadAllTextAsync(alpha));
        foreach (var table in CoreTables) Assert.Equal(1L, fx.ControlCount("p", table));
    }

    [Theory]
    [InlineData("after-copy")]
    [InlineData("before-delete-commit")]
    public async Task A_Failure_Leaves_The_Control_Db_Unchanged_And_No_Product_Db_File(string faultPoint)
    {
        await using var fx = await Fixture.CreateAsync();
        var before = fx.ControlSnapshot();
        fx.Service.FaultHook = point => point == faultPoint ? throw new IOException("injected at " + point) : Task.CompletedTask;

        var result = await fx.Service.MoveAsync("p", dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("injected at " + faultPoint, result.Errors.Single());
        Assert.True(File.Exists(result.BackupPath));
        var alpha = fx.Paths.ProductDbPath("alpha");
        Assert.False(File.Exists(alpha));
        Assert.False(File.Exists(alpha + ProductDbMoveService.MovingSuffix));
        Assert.Equal(before, fx.ControlSnapshot());
    }

    [Fact]
    public async Task A_Host_That_Was_Never_Split_Is_Refused()
    {
        await using var fx = await Fixture.CreateAsync(split: false);

        var result = await fx.Service.MoveAsync("p", dryRun: false);

        Assert.False(result.Success);
        Assert.False(File.Exists(fx.Paths.ProductDbPath("alpha")));
    }

    private static long Count(string path, string table)
    {
        using var conn = OpenReadOnly(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{table}\";";
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<string> Columns(string path, string table)
    {
        using var conn = OpenReadOnly(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = cmd.ExecuteReader();
        var cols = new List<string>();
        while (reader.Read()) cols.Add(reader.GetString(1));
        return cols;
    }

    private static void ReverseColumns(string path, string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        var defs = new List<string>();
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var def = $"\"{reader.GetString(1)}\" {reader.GetString(2)}";
                if (reader.GetInt32(5) > 0) def += " PRIMARY KEY";
                else if (reader.GetInt32(3) == 1) def += " NOT NULL" + (reader.IsDBNull(4) ? string.Empty : " DEFAULT " + reader.GetString(4));
                defs.Add(def);
            }
        }
        defs.Reverse();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE \"{table}\"; CREATE TABLE \"{table}\" ({string.Join(", ", defs)});";
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ForgeWebApplicationFactory app, IForgeDbPaths paths)
        {
            this.App = app;
            this.Paths = paths;
            this.Provisioner = new ProductDbProvisioner(paths);
            this.Service = new ProductDbMoveService(paths, this.Provisioner);
        }

        public ForgeWebApplicationFactory App { get; }

        public IForgeDbPaths Paths { get; }

        public ProductDbProvisioner Provisioner { get; }

        public ProductDbMoveService Service { get; }

        public string ControlPath => this.Paths.ControlDbPath();

        public string Folder => Path.GetDirectoryName(this.ControlPath)!;

        public static async Task<Fixture> CreateAsync(bool split = true)
        {
            var app = new ForgeWebApplicationFactory();
            await CapabilityParityTests.SeedTargetsAsync(app);
            var paths = app.Services.GetRequiredService<IForgeDbPaths>();
            if (split)
            {
                using var conn = new SqliteConnection($"Data Source={paths.ControlDbPath()}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);"
                    + "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES ('phase73-split-applied', '2026-11-19T00:00:00Z');";
                cmd.ExecuteNonQuery();
            }
            SplitMarkerCache.ResetForTesting();
            return new Fixture(app, paths);
        }

        public long ControlCount(string productId, string table)
        {
            var spec = ProductDbMoveService.MoveTables().Single(t => t.TableName == table);
            using var conn = OpenReadOnly(this.ControlPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) {ProductDbMoveService.SourceFrom(spec)};";
            cmd.Parameters.AddWithValue("$pid", productId);
            return (long)cmd.ExecuteScalar()!;
        }

        /// <summary>Every product table's full row count in the control DB, for before/after equality.</summary>
        public Dictionary<string, long> ControlSnapshot()
        {
            using var conn = OpenReadOnly(this.ControlPath);
            var tables = new List<string>();
            using (var list = conn.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var reader = list.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            var counts = new Dictionary<string, long>();
            foreach (var t in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT count(*) FROM \"{t}\";";
                counts[t] = (long)cmd.ExecuteScalar()!;
            }
            return counts;
        }

        public async ValueTask DisposeAsync()
        {
            SplitMarkerCache.ResetForTesting();
            await this.App.DisposeAsync();
        }
    }
}