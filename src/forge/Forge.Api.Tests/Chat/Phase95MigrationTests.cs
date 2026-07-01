// -----------------------------------------------------------------------
// <copyright file="Phase95MigrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 95 stage 1.2 — the control-side chat-bridge migration: creates the
/// tables, is idempotent, and enforces the open-state dedupe index.
/// </summary>
public class Phase95MigrationTests
{
    private static async Task<(SqliteConnection Conn, ControlDbContext Control, ProductDbContext Product)> FreshDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        return (conn, control, product);
    }

    private static async Task<bool> ObjectExistsAsync(SqliteConnection conn, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task InsertNotificationAsync(SqliteConnection conn, string id, string dedupeKey, string status)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO chat_notifications (Id, Kind, ProductId, DedupeKey, Status, Platform, CreatedAt) " +
            "VALUES (@id, 'open-questions', 'p', @dk, @st, 'telegram', '2026-06-30T00:00:00Z')";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@dk", dedupeKey);
        cmd.Parameters.AddWithValue("@st", status);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Creates_Tables_And_Indexes_On_Fresh_Db()
    {
        var (conn, control, product) = await FreshDbAsync();
        await using var _ = conn;
        using (control)
        using (product)
        {
            await Phase95Migration.ApplyAsync(control, product);

            Assert.True(await ObjectExistsAsync(conn, "chat_notifications"));
            Assert.True(await ObjectExistsAsync(conn, "chat_subscribers"));
            Assert.True(await ObjectExistsAsync(conn, "IX_chat_notifications_DedupeKey_open"));
            Assert.True(await ObjectExistsAsync(conn, "IX_chat_subscribers_platform_chatid"));
        }
    }

    [Fact]
    public async Task Is_Idempotent_When_Run_Twice()
    {
        var (conn, control, product) = await FreshDbAsync();
        await using var _ = conn;
        using (control)
        using (product)
        {
            await Phase95Migration.ApplyAsync(control, product);
            await Phase95Migration.ApplyAsync(control, product); // must not throw.

            Assert.True(await ObjectExistsAsync(conn, "chat_notifications"));
        }
    }

    [Fact]
    public async Task Dedupe_Index_Blocks_Second_Open_Row_But_Allows_Resolved()
    {
        var (conn, control, product) = await FreshDbAsync();
        await using var _ = conn;
        using (control)
        using (product)
        {
            await Phase95Migration.ApplyAsync(control, product);

            await InsertNotificationAsync(conn, "n1", "oq:wi-1:0", "pending");

            // Second OPEN row with the same dedupe key violates the partial index.
            await Assert.ThrowsAsync<SqliteException>(() =>
                InsertNotificationAsync(conn, "n2", "oq:wi-1:0", "pending"));

            // A resolved row with the same dedupe key is fine (outside the filter).
            await InsertNotificationAsync(conn, "n3", "oq:wi-1:0", "answered");
        }
    }
}
