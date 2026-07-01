// -----------------------------------------------------------------------
// <copyright file="ChatAckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

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
/// Phase 95 Phase 5.2 + 5.3 — queue-strain [Confirm] and auth/human-interaction
/// [Retry|Done|Snooze] ack buttons, plus the human-interaction enqueue API.
/// </summary>
public class ChatAckTests
{
    private static WebApplicationFactory<Program> WithFakeTransport(ForgeWebApplicationFactory f, FakeChatTransport fake) =>
        f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
        }));

    private static async Task SeedAsync(WebApplicationFactory<Program> app, string notifId, string kind)
    {
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.ChatSubscribers.Add(new ChatSubscriberRow
        {
            Id = "sub-" + notifId, Platform = "telegram", ChatId = "123",
            ForgeUserId = "user-1", LinkedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        control.ChatNotifications.Add(new ChatNotificationRow
        {
            Id = notifId, Kind = kind, ProductId = "p", DedupeKey = kind + ":x",
            Title = "alert", Status = ChatNotificationStatuses.Sent,
            Platform = "telegram", ChatId = "123", ChatMessageId = "m1", CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();
    }

    // FromId matches the subscriber's identity ("123") — chat.id == user.id in a DM.
    private static ChatInbound Tap(string cb) => new(3, "123", "123", null, cb, "m1");

    [Fact]
    public async Task HumanInteraction_Enqueue_Creates_A_Row()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        using (var scope = f.Services.CreateScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();
            var ok = await notifier.EnqueueHumanInteractionAsync(
                new HumanInteractionAlert("", null, "ado-signin:me@x", "🔐 ADO sign-in needed", "do it", null), default);
            Assert.True(ok);
        }

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var row = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Kind == "human-interaction");
            Assert.Equal("interaction:ado-signin:me@x", row.DedupeKey);
        }
    }

    [Fact]
    public async Task Strain_Confirm_Acks_And_Edits()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }
        await SeedAsync(app, "n-strain", "queue-strain");

        using (var scope = app.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(Tap("strain|confirm|crew-1"), default);
        }

        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var n = await control.ChatNotifications.AsNoTracking().SingleAsync(x => x.Id == "n-strain");
            Assert.Equal(ChatNotificationStatuses.Acted, n.Status);
        }
        Assert.Contains(fake.Edits, e => e.Text.Contains("Acknowledged"));
    }

    [Fact]
    public async Task HumanInteraction_Done_Acks_But_Snooze_Does_Not()
    {
        var fake = new FakeChatTransport();
        await using var f = new ForgeWebApplicationFactory();
        var app = WithFakeTransport(f, fake);
        using (app.CreateClient()) { }
        await SeedAsync(app, "n-hi", "human-interaction");

        // Snooze must NOT resolve the alert.
        using (var scope = app.Services.CreateScope())
        {
            var d = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await d.DispatchAsync(Tap("hi|snooze|ado-signin:me"), default);
        }
        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var n = await control.ChatNotifications.AsNoTracking().SingleAsync(x => x.Id == "n-hi");
            Assert.Equal(ChatNotificationStatuses.Sent, n.Status); // still outstanding
        }

        // Done resolves it.
        using (var scope = app.Services.CreateScope())
        {
            var d = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await d.DispatchAsync(Tap("hi|done|ado-signin:me"), default);
        }
        using (var scope = app.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var n = await control.ChatNotifications.AsNoTracking().SingleAsync(x => x.Id == "n-hi");
            Assert.Equal(ChatNotificationStatuses.Acted, n.Status);
        }
        Assert.Contains(fake.Edits, e => e.Text.Contains("Snoozed"));
        Assert.Contains(fake.Edits, e => e.Text.Contains("done", StringComparison.OrdinalIgnoreCase));
    }
}
