// -----------------------------------------------------------------------
// <copyright file="ChatNotifierTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
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
            Assert.StartsWith("oq:wi-1:", row.DedupeKey); // per-WI prefix + content hash
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

            // Same work item, same question set, any iter → still deduped.
            Assert.False(await notifier.EnqueueOpenQuestionsAsync(Oq(iter: 1), default));
            Assert.Equal(1, await db.ChatNotifications.CountAsync());

            // A DIFFERENT work item is a different dedupe key → not deduped.
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(Oq(wi: "wi-2"), default));
            Assert.Equal(2, await db.ChatNotifications.CountAsync());
        }
    }

    [Fact]
    public async Task Enqueue_Changed_Question_Set_Supersedes_Open_Alert_And_Realerts()
    {
        // Regression (release RELEASE-PROVIDER, 2026-07-17): the per-WI dedupe
        // key had no content component, so the first alert held the slot while
        // new questions accumulated unseen — a changed set must supersede the
        // stale open alert and send a fresh message.
        var (conn, db) = await FreshDbAsync();
        await using var _ = conn;
        using (db)
        {
            var notifier = NewNotifier(db);
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(Oq(iter: 13), default));

            // Simulate the first alert having been delivered.
            var first = await db.ChatNotifications.SingleAsync();
            first.Status = ChatNotificationStatuses.Sent;
            first.ChatMessageId = "68";
            await db.SaveChangesAsync();

            var changed = Oq(iter: 30) with { Body = "Pick a persistence backend.\nAccept the Windows atomic-replace?" };
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(changed, default));

            // NB: no OrderBy(CreatedAt) — EF SQLite can't translate DateTimeOffset
            // ordering (documented Forge gotcha); select by identity instead.
            var rows = await db.ChatNotifications.ToListAsync();
            Assert.Equal(2, rows.Count);
            var staleRow = rows.Single(r => r.Id == first.Id);
            var freshRow = rows.Single(r => r.Id != first.Id);
            Assert.Equal(ChatNotificationStatuses.Superseded, staleRow.Status); // stale alert freed its slot
            Assert.Equal(ChatNotificationStatuses.Pending, freshRow.Status);    // current set will send
            Assert.NotEqual(staleRow.DedupeKey, freshRow.DedupeKey);

            // Re-emitting the changed set again stays deduped.
            Assert.False(await notifier.EnqueueOpenQuestionsAsync(changed, default));
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
