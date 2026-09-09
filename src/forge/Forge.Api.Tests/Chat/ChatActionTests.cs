// -----------------------------------------------------------------------
// <copyright file="ChatActionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
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
/// Phase 95 Phase 5.1 — stalled-workstream action buttons: a callback
/// (<c>act|{action}|{wsId}</c>) drives <c>IOperatorActionService</c>; Restart is
/// gated behind a second confirm tap.
/// </summary>
public class ChatActionTests
{
    private static WebApplicationFactory<Program> WithFakeTransport(ForgeWebApplicationFactory f, FakeChatTransport fake) =>
        f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
            // Explicit legacy opt-in: these tests cover the existing interactive paths.
            s.PostConfigure<ChatBridgeOptions>(o => { o.Enabled = true; o.AllowRemoteActions = true; });
        }));

    private static async Task SeedStalledAsync(WebApplicationFactory<Program> app, string wsId)
    {
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        control.ChatSubscribers.Add(new ChatSubscriberRow
        {
            Id = "sub-" + wsId, Platform = "telegram", ChatId = "123",
            ForgeUserId = "user-1", LinkedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        control.ChatNotifications.Add(new ChatNotificationRow
        {
            Id = "n-" + wsId, Kind = "workstream-stalled", ProductId = "p", WorkstreamId = wsId,
            DedupeKey = $"stall:{wsId}:x", Title = "⚠ Workstream stalled", Status = ChatNotificationStatuses.Sent,
            Platform = "telegram", ChatId = "123", ChatMessageId = "m1", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Workstreams.Add(new WorkstreamRow { Id = wsId, Status = "stalled", StallReason = "idle" });
        db.WorkItems.Add(new WorkItemRow { Id = wsId, ExecutionStatus = ExecutionStatuses.Live });
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }

    // FromId matches the subscriber's identity ("123") — chat.id == user.id in a DM.
    private static ChatInbound Tap(string callbackData) =>
        new(7, "123", "123", Text: null, CallbackData: callbackData, ReplyToMessageId: "m1");

    [Fact]
    public async Task Cancel_Action_Cancels_Workstream_And_Marks_Acted()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        const string Ws = "ws-act-cancel";
        await SeedStalledAsync(app, Ws);

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(Tap($"act|cancel|{Ws}"), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Ws);
            Assert.Equal("cancelled", ws.Status);
            var notif = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == "n-" + Ws);
            Assert.Equal(ChatNotificationStatuses.Acted, notif.Status);
        }

        Assert.Contains(fake.Edits, e => e.Text.Contains("cancel done"));
    }

    [Fact]
    public async Task Action_With_Long_WsId_Uses_TwoPart_Callback_And_Resolves_From_Notification()
    {
        // Regression: Telegram caps callback_data at 64 bytes. Long workstream
        // ids blew past it when embedded (BUTTON_DATA_INVALID → alert stuck
        // unsent). The button now carries only "act|cancel" and the ws id is
        // resolved from the correlated notification.
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        const string Ws = "ws-local-net10-migration-phase-de-risk-and-feed-foundation-stage-component-mapping-and-persistence-spike-e2e";
        Assert.True(("act|cancel|" + Ws).Length > 64); // the old inline form was over the limit
        await SeedStalledAsync(app, Ws);

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            // 2-part callback (no ws id) — well under 64 bytes.
            await dispatcher.DispatchAsync(new ChatInbound(9, "123", "123", Text: null, CallbackData: "act|cancel", ReplyToMessageId: "m1"), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Ws);
            Assert.Equal("cancelled", ws.Status);
            var notif = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == "n-" + Ws);
            Assert.Equal(ChatNotificationStatuses.Acted, notif.Status);
        }
    }

    [Fact]
    public async Task Restart_Requires_A_Second_Confirm_Tap()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }

        const string Ws = "ws-act-restart";
        await SeedStalledAsync(app, Ws);

        // First tap: arms the confirm — must NOT restart.
        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(Tap($"act|restart|{Ws}"), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Ws);
            Assert.Equal("stalled", ws.Status); // unchanged — only armed
        }
        Assert.Contains(fake.Edits, e => e.Text.Contains("Tap to confirm"));

        // Second tap: confirm → restart executes.
        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(Tap($"act|restart-confirm|{Ws}"), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var ws = await db.Workstreams.AsNoTracking().SingleAsync(w => w.Id == Ws);
            Assert.Equal("active", ws.Status); // restart reset it
        }
    }
}
