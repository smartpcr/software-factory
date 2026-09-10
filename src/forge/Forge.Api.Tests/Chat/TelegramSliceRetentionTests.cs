// -----------------------------------------------------------------------
// <copyright file="TelegramSliceRetentionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class TelegramSliceRetentionTests
{
    [Fact]
    public async Task Retention_Uses_Seven_Thirty_Ninety_Day_Windows_Batches_And_Retains_Uncertain_Correlation()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            foreach (var (id, status, age) in new[] { ("pending", "pending", 8), ("terminal", "sent", 31), ("young", "sent", 29),
                ("old", "failed", 91), ("uncertain", "uncertain", 91) })
                db.ChatNotifications.Add(new() { Id = id, Platform = ChatTelegramSliceService.Platform, DedupeKey = id,
                    Status = status, CreatedAt = now.AddDays(-age), SentAt = status == "sent" ? now.AddDays(-age) : null,
                    Title = "metadata", Body = "content", LastError = "safe-code", ChatMessageId = "receipt" });
            // Legacy content is owned by its selected engine and is never converted or purged by this slice.
            db.ChatNotifications.Add(new() { Id = "legacy", Platform = "telegram", DedupeKey = "legacy", CreatedAt = now.AddDays(-100), Body = "legacy", Status = "sent" });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(now);
        }
        var rows = await app.OutboxAsync(); Assert.DoesNotContain(rows, n => n.Id == "old");
        Assert.Equal("content", rows.Single(n => n.Id == "young").Body); Assert.Equal("legacy", rows.Single(n => n.Id == "legacy").Body);
        Assert.Equal("expired", rows.Single(n => n.Id == "pending").Status);
        foreach (var id in new[] { "pending", "terminal", "uncertain" })
        {
            var row = rows.Single(n => n.Id == id); Assert.Equal("", row.Body); Assert.Equal("", row.Title); Assert.Null(row.LastError); Assert.Equal("receipt", row.ChatMessageId);
        }
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            for (var i = 0; i < 105; i++) db.ChatNotifications.Add(new() { Id = "batch" + i, DedupeKey = "batch" + i, Platform = ChatTelegramSliceService.Platform,
                CreatedAt = now.AddDays(-31), Status = "sent", Body = "content" });
            await db.SaveChangesAsync(); await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(now);
        }
        Assert.Equal(5, (await app.OutboxAsync()).Count(n => n.Id.StartsWith("batch") && n.Body != ""));
    }

    [Fact]
    public async Task Tombstones_Purge_In_Dependency_Order_And_Unconfirmed_Send_Blocks_Configuration_Deletion()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        app.Http.OmitMessageReceipt = true; await app.TickAsync();
        await app.SliceAsync("owner-p", (s, a) => s.SaveAsync(a, "p", "shared", new(1, false, null)));
        await app.RunAsync("admin", async (s, a) => { await s.DeleteConnectionAsync(a, "test-bot", 1); return true; });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>(); Assert.Empty(await db.ChatConnectionProducts.ToListAsync());
            await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(DateTimeOffset.UtcNow.AddDays(91));
        }
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            Assert.Single(await db.ChatConversationBindings.ToListAsync()); Assert.Single(await db.ChatConnections.ToListAsync());
            Assert.Equal("", (await db.ChatNotifications.SingleAsync()).Body);
            // Explicit operator reconciliation of an uncertain external operation is the only way to retire its receipt.
            var row = await db.ChatNotifications.SingleAsync(); row.Status = "failed"; row.RespondedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(DateTimeOffset.UtcNow.AddDays(92));
        }
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            Assert.Empty(await db.ChatNotifications.ToListAsync()); Assert.Empty(await db.ChatConversationBindings.ToListAsync());
            Assert.Empty(await db.ChatSubscriptions.ToListAsync()); Assert.Empty(await db.SpawnPlugins.ToListAsync());
            Assert.Empty(await db.ChatDestinations.ToListAsync()); Assert.Empty(await db.ChatConnections.ToListAsync());
        }
    }

    [Fact]
    public async Task Missing_Parent_Retires_Only_Its_Route_And_Immediate_Purge_Requires_Host_Admin()
    {
        await using var app = new TelegramSliceTestFactory(); await app.SetupAsync(); await app.EnableAsync();
        using var owner = await app.PairedClientAsync("owner-p");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/chat/retention/purge-content", new { })).StatusCode);
        using var admin = await app.PairedClientAsync("admin");
        var response = await admin.PostAsJsonAsync("/api/chat/retention/purge-content", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(1, result.GetProperty("scrubbed").GetInt32());
        Assert.Equal(0, await app.TickAsync());
        Assert.Equal("route-needs-setup", (await app.SliceAsync("owner-p", (s, a) => s.GetAsync(a, "p", "shared"))).Reason);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.StorySpawns.Remove(await db.StorySpawns.SingleAsync()); await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(DateTimeOffset.UtcNow);
        }
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>(); var row = await db.SpawnPlugins.SingleAsync();
            Assert.False(row.Enabled); Assert.NotNull(row.RetiredAt);
            Assert.All(await db.ChatSubscriptions.ToListAsync(), s => Assert.NotNull(s.RetiredAt));
        }
    }

    [Fact]
    public async Task Phase99_Upgrades_Existing_Stage1_Table_Idempotently_Without_Changing_Its_Data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(connection).Options);
        await using var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(connection).Options);
        await control.Database.ExecuteSqlRawAsync("CREATE TABLE spawn_plugins (ProductId TEXT, SpawnId TEXT, PluginId TEXT, Enabled INTEGER); INSERT INTO spawn_plugins VALUES ('p','s','chat',0);");
        await Phase99Migration.ApplyAsync(control, product); await Phase99Migration.ApplyAsync(control, product);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM spawn_plugins WHERE ProductId = 'p' AND RetiredAt IS NULL";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        Assert.Equal(PhaseMigrationKind.Control, PhaseMigrationCatalog.ByPhase[99].Kind);
        Assert.Empty(PhaseMigrationCatalog.ByPhase[99].ProductTables);
    }

    [Theory]
    [InlineData("Bearer private-bearer")]
    [InlineData("token=private-token")]
    [InlineData("https://private-user:password@example.test/path")]
    [InlineData("https://example.test/path?token=private-token")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nprivate-key\n-----END PRIVATE KEY-----")]
    [InlineData("E:\\private\\source.cs")]
    [InlineData("\\\\server\\private\\source.cs")]
    [InlineData("/workspace/private/source.cs")]
    public void Redaction_Removes_Credentials_And_Absolute_Paths(string text)
    {
        var redacted = ChatContentPolicy.Redact(text, []);
        Assert.DoesNotContain("private", redacted); Assert.Contains("[redacted]", redacted);
        Assert.Equal("[redacted]", ChatContentPolicy.Redact("known-value", ["known-value"]));
    }
}