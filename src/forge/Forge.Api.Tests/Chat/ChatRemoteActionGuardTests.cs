// -----------------------------------------------------------------------
// <copyright file="ChatRemoteActionGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

public class ChatRemoteActionGuardTests
{
    public static IEnumerable<object?[]> DisabledInputs()
    {
        var updates = new (string? Text, string? Callback)[]
        {
            ("/link TOKEN", null), ("/mute", null), ("/unmute", null),
            ("/help", null), ("/status", null), ("answer", null),
            (null, "oq|question|0"), (null, "act|cancel|ws"),
            (null, "act|retry|ws"), (null, "act|accept|ws"),
            (null, "act|restart|ws"), (null, "act|restart-confirm|ws"),
            (null, "strain|confirm|queue"), (null, "hi|retry|host"),
            (null, "hi|done|host"), (null, "hi|snooze|host"),
        };
        foreach (var (text, callback) in updates)
        {
            yield return new object?[] { true, false, text, callback };
            yield return new object?[] { false, true, text, callback };
        }
    }

    [Theory]
    [MemberData(nameof(DisabledInputs))]
    public async Task Guard_Returns_Before_Any_State_Or_Transport_Access(
        bool enabled, bool allowRemoteActions, string? text, string? callback)
    {
        var opt = new ChatBridgeOptions { Enabled = enabled, AllowRemoteActions = allowRemoteActions };
        opt.Telegram.AllowedUserIds.Add(123); // Even an allow-listed sender cannot bootstrap.
        var transport = new Mock<IChatTransport>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        // No DB exists: any attempt to access it escapes as an error. A strict
        // logger also catches the dispatcher's catch-and-log error path.
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<ChatInboundDispatcher>>(MockBehavior.Strict);
        var dispatcher = new ChatInboundDispatcher(null!, scopes.Object, transport.Object,
            new StaticOptionsMonitor<ChatBridgeOptions>(opt), logger.Object, null!, null!, null!);

        await dispatcher.HandleAsync(new ChatInboundMessage(
            new ChatInbound(1, "42", "123", text, callback, "m1", ThreadId: "88"),
            ForgeChatRoutes.Spawn("p", "spawn")), default);

        transport.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Notification_Only_Pump_Consumes_Old_Buttons_Without_Applying_Them()
    {
        await using var factory = new ForgeWebApplicationFactory(new()
        {
            ["Forge:ChatBridge:Enabled"] = "true",
        });
        var fake = new FakeChatTransport();
        fake.Inbox.Enqueue(new ChatInbound(77, "123", "123", null, "hi|done|host", "m1"));
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChatTransport>();
            s.AddSingleton<IChatTransport>(fake);
        }));
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.ChatSubscribers.Add(new ChatSubscriberRow
        {
            Id = "sub", Platform = "telegram", ChatId = "123", ForgeUserId = "operator",
            LinkedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        control.ChatNotifications.Add(new ChatNotificationRow
        {
            Id = "n", Kind = "human-interaction", DedupeKey = "host", Title = "Host alert",
            Platform = "telegram", ChatId = "123", ChatMessageId = "m1",
            Status = ChatNotificationStatuses.Sent, CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();

        Assert.Equal(1, await app.Services.GetRequiredService<ChatInboundPump>().RunOnceAsync(default));

        control.ChangeTracker.Clear();
        Assert.Equal(ChatNotificationStatuses.Sent, (await control.ChatNotifications.SingleAsync()).Status);
        Assert.False((await control.ChatSubscribers.SingleAsync()).Muted);
        Assert.Equal(77, (await control.ChatBridgeState.SingleAsync()).LastUpdateId);
        Assert.Empty(fake.Sent);
        Assert.Empty(fake.Edits);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Linking_Is_Unavailable_Without_Both_Switches(bool enabled, bool allowRemoteActions)
    {
        await using var factory = new ForgeWebApplicationFactory(new()
        {
            ["Forge:ChatBridge:Enabled"] = enabled.ToString(),
            ["Forge:ChatBridge:AllowRemoteActions"] = allowRemoteActions.ToString(),
        });
        await factory.EnrollAsync("test-operator");
        using var client = await factory.PairedClientAsync("test-operator");
        using var response = await client.PostAsJsonAsync("/api/chat/link-token", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatSubscribers.ToListAsync());
    }
}
