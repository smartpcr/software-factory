// -----------------------------------------------------------------------
// <copyright file="ChatInboundTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 95 Phase 4 — inbound: /link binding, allow-list enforcement, answer
/// correlation (choice callback), and the long-poll offset cursor.
/// </summary>
public class ChatInboundTests
{
    private static WebApplicationFactory<Program> WithFakeTransport(ForgeWebApplicationFactory f, FakeChatTransport fake) =>
        f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
            // Explicit legacy opt-in: these tests cover the existing interactive paths.
            s.PostConfigure<ChatBridgeOptions>(o => { o.Enabled = true; o.AllowRemoteActions = true; });
        }));

    private static ChatInbound TextUpdate(long id, string chatId, string text) =>
        new(id, chatId, chatId, text, CallbackData: null, ReplyToMessageId: null);

    [Fact]
    public async Task Link_Binds_Chat_To_A_Pending_Subscriber()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        await app.EnrollAsync("user-1");
        var token = app.Services.GetRequiredService<HostIdentityStore>().IssueLinkChallenge("user-1", "telegram", "555", "555");

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(TextUpdate(1, "555", "/link " + token), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var sub = await control.ChatSubscribers.AsNoTracking().SingleAsync(s => s.ChatId == "555");
            Assert.Equal("555", sub.ChatId);
            Assert.NotNull(sub.LinkedAt);
            Assert.Null(sub.LinkToken);
        }

        Assert.Contains(fake.Sent, m => m.Text.Contains("Linked"));
    }

    [Fact]
    public async Task Invalid_Link_Token_Is_Rejected()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(TextUpdate(1, "555", "/link NOPE"), default);
        }

        Assert.Empty(fake.Sent);
        using var s2 = app.Services.CreateScope();
        var control = s2.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Equal(0, await control.ChatSubscribers.CountAsync(x => x.ChatId == "555"));
    }

    [Fact]
    public async Task Unlinked_Chat_Inbound_Is_Ignored()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(TextUpdate(1, "999", "hello there"), default);
        }

        // No reply, no state change — an unlinked chat id can't drive Forge.
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Choice_Callback_Answers_Question_And_Edits_Message()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        await app.SeedProductAsync();
        await app.EnrollAsync("user-1", productId: "p");
        app.Link("user-1", tenant: "123");

        const string Wi = "wi-inbound-answer";
        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.ChatSubscribers.Add(new ChatSubscriberRow
            {
                Id = "sub-a", Platform = "telegram", ChatId = "123",
                ForgeUserId = "user-1", LinkedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            });
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n-a", Kind = "open-questions", ProductId = "p", WorkItemId = Wi,
                DedupeKey = $"oq:{Wi}:1", Title = "Pick a backend", Status = ChatNotificationStatuses.Sent,
                Platform = "telegram", ChatId = "123", ChatMessageId = "m1", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.WorkItems.Add(new WorkItemRow { Id = Wi, RepoId = "repo-p", ExecutionStatus = ExecutionStatuses.AwaitingAnswers });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = Wi, ExternalId = "persistence", Iter = 1, Text = "Which backend?",
                Kind = "choice", ChoicesJson = "[\"asz store\",\"legacy\"]", CreatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            // Button tap: callback_data oq|persistence|0, on message m1. FromId
            // matches the subscriber's identity (ChatId), as chat.id == user.id in a DM.
            await dispatcher.DispatchAsync(
                new ChatInbound(5, "123", "123", Text: null, CallbackData: "oq|persistence|0", ReplyToMessageId: "m1"),
                default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var q = await db.DeliverableQuestions.AsNoTracking().SingleAsync(x => x.WorkItemId == Wi);
            Assert.Equal("asz store", q.Answer);
            var notif = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == "n-a");
            Assert.Equal(ChatNotificationStatuses.Answered, notif.Status);
        }

        Assert.Contains(fake.Edits, e => e.Text.Contains("Answered: asz store"));
    }

    [Fact]
    public async Task Free_Text_In_Spawn_Topic_Answers_That_Spawns_Question_Without_Reply()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        await app.SeedProductAsync();
        await app.EnrollAsync("user-1", productId: "p");
        app.Link("user-1", tenant: "42");

        const string Wi = "wi-topic-answer";
        const string Spawn = "spawn-route-5";
        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.ChatSubscribers.Add(new ChatSubscriberRow
            {
                Id = "sub-t", Platform = "telegram", ChatId = "123",
                ForgeUserId = "user-1", LinkedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            });
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n-t", Kind = "open-questions", ProductId = "p", WorkItemId = Wi, SpawnId = Spawn,
                DedupeKey = $"oq:{Wi}", Title = "Pick a backend", Status = ChatNotificationStatuses.Sent,
                Platform = "telegram", ChatId = "42", ChatMessageId = "m9", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow { Id = Spawn, StoryId = "story-topic", RepoId = "repo-p" });
            db.Workstreams.Add(new WorkstreamRow { Id = Wi, StorySpawnId = Spawn, RepoId = "repo-p", Status = "stalled" });
            db.WorkItems.Add(new WorkItemRow { Id = Wi, RepoId = "repo-p", ExecutionStatus = ExecutionStatuses.AwaitingAnswers });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = Wi, ExternalId = "persistence", Iter = 1, Text = "Which backend?",
                Kind = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            // A plain message typed IN the spawn's forum topic — no reply id.
            // The channel route (resolved by the pump from the thread) carries
            // the product + spawn, which correlates to the spawn's alert.
            await dispatcher.HandleAsync(
                new ChatInboundMessage(
                    new ChatInbound(6, "42", "123", "use asz store", CallbackData: null, ReplyToMessageId: null, ThreadId: "88"),
                    ForgeChatRoutes.Spawn("p", Spawn)),
                default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var q = await db.DeliverableQuestions.AsNoTracking().SingleAsync(x => x.WorkItemId == Wi);
            Assert.Equal("use asz store", q.Answer);
            var notif = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == "n-t");
            Assert.Equal(ChatNotificationStatuses.Answered, notif.Status);
        }

        Assert.Contains(fake.Edits, e => e.Text.Contains("Answered: use asz store"));
    }

    [Fact]
    public async Task AllowListed_User_Is_Bootstrapped_From_The_Group_Without_Link()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
            // Explicit legacy opt-in: these tests cover the existing interactive paths.
            s.PostConfigure<ChatBridgeOptions>(o => { o.Enabled = true; o.AllowRemoteActions = true; });
            s.Configure<ChatBridgeOptions>(o =>
            {
                o.Telegram.SupergroupChatId = 42;
                o.Telegram.AllowedUserIds = new List<long> { 777 };
            });
        }));
        using (app.CreateClient()) { }

        // Inbound from allow-listed user 777, sent in the shared supergroup (chat
        // 42). No prior /link — the allow-list bootstraps the subscriber.
        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(new ChatInbound(1, "42", "777", "/help", null, null), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            Assert.False(await control.ChatSubscribers.AnyAsync());
        }

        // Authorized → /help was answered.
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Non_AllowListed_User_In_Group_Is_Ignored()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
            // Explicit legacy opt-in: these tests cover the existing interactive paths.
            s.PostConfigure<ChatBridgeOptions>(o => { o.Enabled = true; o.AllowRemoteActions = true; });
            s.Configure<ChatBridgeOptions>(o =>
            {
                o.Telegram.SupergroupChatId = 42;
                o.Telegram.AllowedUserIds = new List<long> { 777 };
            });
        }));
        using (app.CreateClient()) { }

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(new ChatInbound(1, "42", "999", "/help", null, null), default);
        }

        Assert.Empty(fake.Sent);
        using var s2 = app.Services.CreateScope();
        var control = s2.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Equal(0, await control.ChatSubscribers.CountAsync());
    }

    [Fact]
    public async Task RunInboundOnce_Advances_Offset_Cursor()
    {
        var fake = new FakeChatTransport();
        fake.Inbox.Enqueue(new ChatInbound(41, "999", "999", "unlinked ignored", null, null));
        fake.Inbox.Enqueue(new ChatInbound(42, "999", "999", "also ignored", null, null));

        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        var pump = app.Services.GetRequiredService<ChatInboundPump>();
        var count = await pump.RunOnceAsync(default);
        Assert.Equal(2, count);

        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var state = await control.ChatBridgeState.AsNoTracking().SingleAsync();
        Assert.Equal(42, state.LastUpdateId);
    }
}
