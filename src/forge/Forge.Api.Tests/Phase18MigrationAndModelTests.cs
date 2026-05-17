// -----------------------------------------------------------------------
// <copyright file="Phase18MigrationAndModelTests.cs" company="Microsoft Corp.">
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
/// Phase 18.1 + 18.2 — schema migration and domain-model contracts for
/// the per-crew shift band. Migration tests use a hand-rolled SQLite
/// connection so a "pre-Phase18" schema (no Shift column) can be
/// reconstructed from raw SQL; domain-model tests use a real
/// <see cref="ProductDbContext"/> for the EF round-trip.
/// </summary>
public class Phase18MigrationAndModelTests
{
    // ── 18.1 Migration ────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal "pre-Phase18" <c>crews</c> table on a fresh in-memory
    /// SQLite connection — only the columns the migration cares about (Id,
    /// Name) are present, and the connection stays open so successive calls
    /// reuse the same in-memory database.
    /// </summary>
    private static async Task<SqliteConnection> NewPrePhase18ConnectionAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE crews (Id TEXT PRIMARY KEY, Name TEXT NOT NULL DEFAULT '');";
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    /// <summary>
    /// Wrap the SqliteConnection in a real ProductDbContext so
    /// <c>Phase18Migration.ApplyAsync</c> (which expects a context) can run.
    /// The DbContext does not own the connection — caller disposes it.
    /// </summary>
    private static ProductDbContext WrapInContext(SqliteConnection conn)
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

    [Fact]
    public async Task Migration_Adds_Shift_Column_To_Pre_Phase18_Schema()
    {
        await using var conn = await NewPrePhase18ConnectionAsync();
        Assert.False(await ColumnExistsAsync(conn, "crews", "Shift"));

        await using var control = WrapInControlContext(conn);
        await using (var db = WrapInContext(conn))
        {
            await Phase18Migration.ApplyAsync(control, db);
        }

        Assert.True(await ColumnExistsAsync(conn, "crews", "Shift"));
    }

    [Fact]
    public async Task Migration_Is_Idempotent()
    {
        await using var conn = await NewPrePhase18ConnectionAsync();
        await using var control = WrapInControlContext(conn);
        await using var db = WrapInContext(conn);

        // First call adds the column.
        await Phase18Migration.ApplyAsync(control, db);
        Assert.True(await ColumnExistsAsync(conn, "crews", "Shift"));

        // Second call must not throw and must not duplicate the column.
        await Phase18Migration.ApplyAsync(control, db);

        // PRAGMA table_info returns one row per column — count Shift columns.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(crews);";
        await using var r = await cmd.ExecuteReaderAsync();
        var shiftRows = 0;
        while (await r.ReadAsync())
        {
            if (string.Equals(r.GetString(1), "Shift", StringComparison.OrdinalIgnoreCase))
                shiftRows++;
        }
        Assert.Equal(1, shiftRows);
    }

    [Fact]
    public async Task Migration_Backfills_Existing_Rows_With_Day()
    {
        await using var conn = await NewPrePhase18ConnectionAsync();

        // Insert a row before the migration — this row has no Shift column
        // at all, so the migration's ALTER TABLE … DEFAULT 'day' clause is
        // what gives it its band post-upgrade.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO crews (Id, Name) VALUES ('legacy', 'L');";
            await insert.ExecuteNonQueryAsync();
        }

        await using var control = WrapInControlContext(conn);
        await using (var db = WrapInContext(conn))
        {
            await Phase18Migration.ApplyAsync(control, db);
        }

        // The pre-existing row must now report Shift = 'day' (Q18.2 strict-
        // superset rule: behaviour pre/post upgrade is identical).
        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT Shift FROM crews WHERE Id = 'legacy';";
        var value = (string?)await query.ExecuteScalarAsync();
        Assert.Equal("day", value);
    }

    // ── 18.2 Domain model ────────────────────────────────────────────────

    [Fact]
    public void CrewRow_Default_Shift_Is_Day()
    {
        // A freshly-constructed CrewRow must default to "day" so an in-
        // memory caller (test fixtures, seed code, etc.) doesn't have to
        // know about the field. Mirrors the SQL DEFAULT in 18.1.
        var row = new CrewRow();
        Assert.Equal("day", row.Shift);
    }

    [Fact]
    public async Task CrewRow_Shift_RoundTrips_Through_EF()
    {
        // Write 'night', read back 'night' — confirms the EF mapping is in
        // place and not silently dropping the field at the persistence layer.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot to apply migrations */ }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        control.Crews.Add(new CrewRow
        {
            Id = "round-trip",
            Name = "RT",
            Summary = string.Empty,
            Model = "local-stub",
            ModelInputPricePerMTok = 1m,
            ModelOutputPricePerMTok = 1m,
            Shift = "night",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Read in a fresh tracker scope so we exercise the actual SELECT.
        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var read = await control_db2.Crews.AsNoTracking().FirstAsync(c => c.Id == "round-trip");
        Assert.Equal("night", read.Shift);
    }
}
