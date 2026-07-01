// -----------------------------------------------------------------------
// <copyright file="ChatNotifierTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 95 stage 2.1 — the enqueue API: dedupe-keyed pending rows +
/// resolution flips.
/// </summary>
public class ChatNotifierTests
{
    private static async Task<(SqliteConnection Conn, ControlDbContext Db)> FreshDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var db = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private static ChatNotifier NewNotifier(ControlDbContext db, long? allowedChatId = 123)
    {
        var opt = new ChatBridgeOptions { Platform = "telegram", NotificationTtl = TimeSpan.FromDays(1) };
        if (allowedChatId is { } id)
        {
            opt.Telegram.AllowedChatIds.Add(id);
        }

        return new ChatNotifier(db, new StaticOptionsMonitor<ChatBridgeOptions>(opt), NullLogger<ChatNotifier>.Instance);
    }

    private static OpenQuestionsAlert Oq(string wi = "wi-1", int iter = 0) => new(
        ProductId: "prod-1",
        RepoId: "repo-1",
        WorkItemId: wi,
        Iter: iter,
        Title: "3 open questions",
        Body: "Pick a persistence backend.",
        Buttons: new[] { new ChatButton("A", "a:wi-1:persistence:0"), new ChatButton("B", "a:wi-1:persistence:1") });

    [Fact]
    public async Task Enqueue_Writes_Pending_Row_With_Correct_Fields()
    {
        var (conn, db) = await FreshDbAsync();
        await using var _ = conn;
        using (db)
        {
            var ok = await NewNotifier(db).EnqueueOpenQuestionsAsync(Oq(), default);
            Assert.True(ok);

            var row = await db.ChatNotifications.SingleAsync();
            Assert.Equal("open-questions", row.Kind);
            Assert.Equal(ChatNotificationStatuses.Pending, row.Status);
            Assert.Equal("prod-1", row.ProductId);
            Assert.Equal("repo-1", row.RepoId);
            Assert.Equal("wi-1", row.WorkItemId);
            Assert.Equal("oq:wi-1", row.DedupeKey);
            Assert.Equal("telegram", row.Platform);
            Assert.Equal("123", row.ChatId);
            Assert.NotNull(row.ActionsJson);
            Assert.Contains("a:wi-1:persistence:0", row.ActionsJson);
            Assert.NotNull(row.ExpiresAt);
        }
    }

    [Fact]
    public async Task Enqueue_Dedupes_Second_Open_Row()
    {
        var (conn, db) = await FreshDbAsync();
        await using var _ = conn;
        using (db)
        {
            var notifier = NewNotifier(db);
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(Oq(iter: 0), default));
            Assert.False(await notifier.EnqueueOpenQuestionsAsync(Oq(iter: 0), default));

            Assert.Equal(1, await db.ChatNotifications.CountAsync());

            // Same work item, any iter → still deduped (one outstanding alert per WI).
            Assert.False(await notifier.EnqueueOpenQuestionsAsync(Oq(iter: 1), default));
            Assert.Equal(1, await db.ChatNotifications.CountAsync());

            // A DIFFERENT work item is a different dedupe key → not deduped.
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(Oq(wi: "wi-2"), default));
            Assert.Equal(2, await db.ChatNotifications.CountAsync());
        }
    }

    [Fact]
    public async Task MarkResolved_Sent_Row_Goes_Resolving_And_Stamps_RespondedAt()
    {
        var (conn, db) = await FreshDbAsync();
        await using var _ = conn;
        using (db)
        {
            db.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n-sent",
                Kind = "open-questions",
                WorkItemId = "wi-9",
                DedupeKey = "oq:wi-9:0",
                Status = ChatNotificationStatuses.Sent,
                ChatId = "123",
                ChatMessageId = "m1",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var affected = await NewNotifier(db).MarkOpenQuestionsResolvedAsync("wi-9", default);

            Assert.Equal(1, affected);
            var row = await db.ChatNotifications.SingleAsync();
            Assert.Equal(ChatNotificationStatuses.Resolving, row.Status);
            Assert.NotNull(row.RespondedAt);
        }
    }

    [Fact]
    public async Task MarkResolved_Pending_Row_Goes_Terminal_Without_Edit()
    {
        var (conn, db) = await FreshDbAsync();
        await using var _ = conn;
        using (db)
        {
            db.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n-pending",
                Kind = "open-questions",
                WorkItemId = "wi-8",
                DedupeKey = "oq:wi-8:0",
                Status = ChatNotificationStatuses.Pending,
                ChatId = "123",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            await NewNotifier(db).MarkOpenQuestionsResolvedAsync("wi-8", default);

            var row = await db.ChatNotifications.SingleAsync();
            // Never sent (no ChatMessageId) → straight to terminal, no edit owed.
            Assert.Equal(ChatNotificationStatuses.Answered, row.Status);
            Assert.NotNull(row.RespondedAt);
        }
    }
}
