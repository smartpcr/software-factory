// -----------------------------------------------------------------------
// <copyright file="Stage11WorkstreamCategoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 1.1 — schema migration + EF mapping contract
/// for the <c>workstreams.Category</c> discriminator
/// (<c>"impl"</c> | <c>"qa-e2e"</c>).
///
/// <para>
/// Migration unit tests (the first three <c>[Fact]</c>s) use a hand-rolled
/// SQLite connection that reconstructs a "pre-Phase79" <c>workstreams</c>
/// table from raw SQL — only the columns the migration cares about (Id,
/// Status, StorySpawnId) plus enough scaffolding to insert a backfill
/// fixture row. The connection stays open so successive calls reuse the
/// same in-memory database.
/// </para>
/// <para>
/// The fourth <c>[Fact]</c> is an EF round-trip integration test that
/// boots the full Forge.Api host via <see cref="ForgeWebApplicationFactory"/>
/// so the production migration runner has applied Phase 79 by the time
/// the EF read runs. Mirrors the Phase 18 pattern.
/// </para>
/// </summary>
public class Stage11WorkstreamCategoryTests
{
    // ── Test plumbing (mirrors Phase18MigrationAndModelTests) ────────────

    /// <summary>
    /// Build a minimal "pre-Phase79" <c>workstreams</c> table on a fresh
    /// in-memory SQLite connection — only the columns the migration cares
    /// about (Id PK, Status, StorySpawnId) are present, so the migration's
    /// ALTER TABLE / CREATE INDEX is the contract under test.
    /// </summary>
    private static async Task<SqliteConnection> NewPrePhase79ConnectionAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE workstreams (
              Id TEXT PRIMARY KEY,
              Status TEXT NOT NULL DEFAULT '',
              StorySpawnId TEXT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private static ProductDbContext WrapInProductContext(SqliteConnection conn)
    {
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        return new ProductDbContext(opts);
    }

    private static ControlDbContext WrapInControlContext(SqliteConnection conn)
    {
        var opts = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(conn)
            .Options;
        return new ControlDbContext(opts);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<int> CountColumnsNamedAsync(SqliteConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var r = await cmd.ExecuteReaderAsync();
        var matches = 0;
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                matches++;
        }
        return matches;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection conn, string indexName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        cmd.Parameters.AddWithValue("@name", indexName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── 1. [unit] Fresh DB migration creates Category with default 'impl' ──

    [Fact]
    public async Task Migration_Adds_Category_Column_To_Pre_Phase79_Schema()
    {
        await using var conn = await NewPrePhase79ConnectionAsync();
        Assert.False(await ColumnExistsAsync(conn, "workstreams", "Category"));

        await using var control = WrapInControlContext(conn);
        await using (var db = WrapInProductContext(conn))
        {
            await Phase79Migration.ApplyAsync(control, db);
        }

        // Column exists.
        Assert.True(await ColumnExistsAsync(conn, "workstreams", "Category"));

        // A freshly inserted row that doesn't specify Category gets the
        // SQL DEFAULT 'impl' — confirms the DEFAULT clause was applied,
        // not just the ALTER TABLE.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO workstreams (Id, Status, StorySpawnId) VALUES ('ws-fresh', 'planning', 'spawn-1');";
            await insert.ExecuteNonQueryAsync();
        }
        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT Category FROM workstreams WHERE Id = 'ws-fresh';";
        var value = (string?)await read.ExecuteScalarAsync();
        Assert.Equal("impl", value);

        // Composite index for DAG-render query exists.
        Assert.True(await IndexExistsAsync(conn, "IX_workstreams_StorySpawnId_Category"));
    }

    // ── 2. [unit] Idempotent re-run ──────────────────────────────────────

    [Fact]
    public async Task Migration_Is_Idempotent()
    {
        await using var conn = await NewPrePhase79ConnectionAsync();
        await using var control = WrapInControlContext(conn);
        await using var db = WrapInProductContext(conn);

        // First call adds the column + index.
        await Phase79Migration.ApplyAsync(control, db);
        Assert.True(await ColumnExistsAsync(conn, "workstreams", "Category"));
        Assert.True(await IndexExistsAsync(conn, "IX_workstreams_StorySpawnId_Category"));

        // Second call must not throw.
        await Phase79Migration.ApplyAsync(control, db);

        // Third call for good measure.
        await Phase79Migration.ApplyAsync(control, db);

        // Column must appear exactly once (no duplicate ALTER).
        Assert.Equal(1, await CountColumnsNamedAsync(conn, "workstreams", "Category"));

        // Index still exists (CREATE INDEX IF NOT EXISTS is a no-op on
        // re-runs, but a regression that dropped + recreated would still
        // pass this assertion — that's fine, the guard against drop is
        // the SQL syntax itself).
        Assert.True(await IndexExistsAsync(conn, "IX_workstreams_StorySpawnId_Category"));
    }

    // ── 3. [unit] Existing rows backfill to 'impl' ───────────────────────

    [Fact]
    public async Task Migration_Backfills_Existing_Rows_To_Impl()
    {
        await using var conn = await NewPrePhase79ConnectionAsync();

        // Insert two legacy rows BEFORE the migration — they have no
        // Category column at all, so the migration's ALTER TABLE
        // … DEFAULT 'impl' clause is what gives them their value.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO workstreams (Id, Status, StorySpawnId) VALUES
                  ('ws-legacy-1', 'merging',  'spawn-old'),
                  ('ws-legacy-2', 'complete', 'spawn-old');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using var control = WrapInControlContext(conn);
        await using (var db = WrapInProductContext(conn))
        {
            await Phase79Migration.ApplyAsync(control, db);
        }

        // Every pre-existing row now reports Category = 'impl' — the
        // refactor's invariant that legacy data reads as plain impl
        // workstreams (never accidentally as qa-e2e).
        await using var query = conn.CreateCommand();
        query.CommandText =
            "SELECT Id, Category FROM workstreams WHERE Id IN ('ws-legacy-1', 'ws-legacy-2') ORDER BY Id;";
        await using var r = await query.ExecuteReaderAsync();

        Assert.True(await r.ReadAsync());
        Assert.Equal("ws-legacy-1", r.GetString(0));
        Assert.Equal("impl", r.GetString(1));

        Assert.True(await r.ReadAsync());
        Assert.Equal("ws-legacy-2", r.GetString(0));
        Assert.Equal("impl", r.GetString(1));

        Assert.False(await r.ReadAsync());
    }

    // ── 4. [unit] POCO default ───────────────────────────────────────────
    //
    // Mirrors the Phase 18 `CrewRow_Default_Shift_Is_Day` smoke check —
    // an in-memory caller (test fixtures, seed code, the deterministic
    // strategy's test double) doesn't need to know about the field to
    // get the correct default. Belt-and-braces with the SQL DEFAULT
    // tested above: the EF write path serialises the POCO default, the
    // SQL DEFAULT covers DBA-style INSERTs.

    [Fact]
    public void WorkstreamRow_Default_Category_Is_Impl()
    {
        var row = new WorkstreamRow();
        Assert.Equal("impl", row.Category);
    }

    // ── 5. [integration] EF round-trip via boot factory ──────────────────
    //
    // The headline integration scenario from Stage 1.1's Test Scenarios:
    // "A spawn restart loads WorkstreamRow.Category correctly via EF, no
    // SqliteException." Boots the full Forge.Api host so production
    // PhaseMigrationRunner applies Phase 79, then writes + reads a
    // workstream row through ProductDbContext to confirm the EF mapping
    // is live (HasIndex + the new property).

    [Fact]
    public async Task WorkstreamRow_Category_RoundTrips_Through_EF()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot to apply migrations */ }

        // Write 'qa-e2e' in one scope.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-cat-roundtrip",
                Name = "category round-trip",
                Branch = "ws/test/cat-roundtrip",
                Base = "main",
                Status = "planning",
                StorySpawnId = "spawn-cat-roundtrip",
                Category = "qa-e2e",
            });
            await db.SaveChangesAsync();
        }

        // Read in a fresh tracker scope so we exercise the actual SELECT
        // (not a tracked in-memory hit).
        using (var scope2 = f.Services.CreateScope())
        {
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var read = await db2.Workstreams
                .AsNoTracking()
                .FirstAsync(w => w.Id == "ws-cat-roundtrip");
            Assert.Equal("qa-e2e", read.Category);
        }

        // Write a second row WITHOUT setting Category — the POCO default
        // must persist as 'impl' so legacy callers (every existing call
        // site of `new WorkstreamRow { … }`) don't accidentally write null
        // or empty.
        using (var scope3 = f.Services.CreateScope())
        {
            var db3 = scope3.ServiceProvider.GetRequiredService<ProductDbContext>();
            db3.Workstreams.Add(new WorkstreamRow
            {
                Id = "ws-cat-default",
                Name = "category default",
                Branch = "ws/test/cat-default",
                Base = "main",
                Status = "planning",
                StorySpawnId = "spawn-cat-roundtrip",
                // Category intentionally omitted.
            });
            await db3.SaveChangesAsync();
        }

        using (var scope4 = f.Services.CreateScope())
        {
            var db4 = scope4.ServiceProvider.GetRequiredService<ProductDbContext>();
            var read = await db4.Workstreams
                .AsNoTracking()
                .FirstAsync(w => w.Id == "ws-cat-default");
            Assert.Equal("impl", read.Category);
        }

        // Index sanity — the EF model must produce the composite index
        // post-EnsureCreated/migration. Probe sqlite_master directly via
        // an open connection from the DbContext.
        using (var scope5 = f.Services.CreateScope())
        {
            var db5 = scope5.ServiceProvider.GetRequiredService<ProductDbContext>();
            var conn = (SqliteConnection)db5.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'IX_workstreams_StorySpawnId_Category'";
            Assert.NotNull(await cmd.ExecuteScalarAsync());
        }
    }
}
