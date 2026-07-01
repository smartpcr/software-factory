// -----------------------------------------------------------------------
// <copyright file="ChatBridgeOutboundTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 95 stages 2.2 + 2.3 — the outbound pump: send/edit passes, retry,
/// TTL/expiry, and failure isolation.
/// </summary>
public class ChatBridgeOutboundTests : IAsyncLifetime
{
    private SqliteConnection conn = null!;
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        this.conn = new SqliteConnection("DataSource=:memory:");
        await this.conn.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<ControlDbContext>(o => o.UseSqlite(this.conn));
        this.provider = services.BuildServiceProvider();
        using var scope = this.provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.conn.DisposeAsync();
    }

    private ChatBridgeService NewService(FakeChatTransport transport, int maxAttempts = 5)
    {
        var opt = new ChatBridgeOptions { Enabled = true, MaxAttempts = maxAttempts };
        return new ChatBridgeService(
            this.provider.GetRequiredService<IServiceScopeFactory>(),
            transport,
            new StaticOptionsMonitor<ChatBridgeOptions>(opt),
            NullLogger<ChatBridgeService>.Instance);
    }

    private async Task SeedAsync(params ChatNotificationRow[] rows)
    {
        using var scope = this.provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        db.ChatNotifications.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<ChatNotificationRow> GetAsync(string id)
    {
        using var scope = this.provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        return await db.ChatNotifications.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static ChatNotificationRow Pending(string id, string? chatId = "123", string kind = "open-questions") => new()
    {
        Id = id,
        Kind = kind,
        WorkItemId = "wi",
        DedupeKey = $"k:{id}",
        Status = ChatNotificationStatuses.Pending,
        ChatId = chatId,
        Title = "Alert " + id,
        Body = "body",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    [Fact]
    public async Task Send_Pass_Sends_Each_Pending_Once_And_Marks_Sent()
    {
        await this.SeedAsync(Pending("a"), Pending("b"));
        var transport = new FakeChatTransport();

        var result = await this.NewService(transport).RunOutboundOnceAsync(default);

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, transport.Sent.Count);
        var a = await this.GetAsync("a");
        Assert.Equal(ChatNotificationStatuses.Sent, a.Status);
        Assert.False(string.IsNullOrEmpty(a.ChatMessageId));
        Assert.NotNull(a.SentAt);
    }

    [Fact]
    public async Task Sent_Row_Not_Resent_On_Next_Tick()
    {
        await this.SeedAsync(Pending("a"));
        var transport = new FakeChatTransport();
        var svc = this.NewService(transport);

        await svc.RunOutboundOnceAsync(default);
        var second = await svc.RunOutboundOnceAsync(default);

        Assert.Equal(0, second.Sent);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task Rendering_Includes_Title_Body_And_Buttons()
    {
        var row = Pending("a");
        row.ActionsJson = ChatActions.Serialize(new[] { new ChatButton("Retry", "act:retry"), new ChatButton("Cancel", "act:cancel") });
        await this.SeedAsync(row);
        var transport = new FakeChatTransport();

        await this.NewService(transport).RunOutboundOnceAsync(default);

        var (chatId, text, buttons) = transport.Sent[0];
        Assert.Equal("123", chatId);
        Assert.Contains("Alert a", text);
        Assert.Contains("body", text);
        Assert.NotNull(buttons);
        Assert.Equal(2, buttons!.Count);
        Assert.Equal("act:retry", buttons[0].CallbackData);
    }

    [Fact]
    public async Task Resolving_Row_Edited_Exactly_Once_Then_Terminal()
    {
        await this.SeedAsync(new ChatNotificationRow
        {
            Id = "r",
            Kind = "open-questions",
            DedupeKey = "k:r",
            Status = ChatNotificationStatuses.Resolving,
            ChatId = "123",
            ChatMessageId = "m1",
            Title = "Q",
            CreatedAt = DateTimeOffset.UtcNow,
            RespondedAt = DateTimeOffset.UtcNow,
        });
        var transport = new FakeChatTransport();
        var svc = this.NewService(transport);

        var first = await svc.RunOutboundOnceAsync(default);
        Assert.Equal(1, first.Edited);
        Assert.Single(transport.Edits);
        Assert.Equal(ChatNotificationStatuses.Answered, (await this.GetAsync("r")).Status);

        var second = await svc.RunOutboundOnceAsync(default);
        Assert.Equal(0, second.Edited);
        Assert.Single(transport.Edits);
    }

    [Fact]
    public async Task Transient_Failure_Retries_Then_Succeeds()
    {
        await this.SeedAsync(Pending("a"));
        var transport = new FakeChatTransport { FailSends = true };
        var svc = this.NewService(transport, maxAttempts: 5);

        var t1 = await svc.RunOutboundOnceAsync(default);
        Assert.Equal(0, t1.Sent);
        var afterFail = await this.GetAsync("a");
        Assert.Equal(ChatNotificationStatuses.Pending, afterFail.Status);
        Assert.Equal(1, afterFail.Attempts);
        Assert.False(string.IsNullOrEmpty(afterFail.LastError));

        transport.FailSends = false;
        var t2 = await svc.RunOutboundOnceAsync(default);
        Assert.Equal(1, t2.Sent);
        Assert.Equal(ChatNotificationStatuses.Sent, (await this.GetAsync("a")).Status);
    }

    [Fact]
    public async Task Exceeding_MaxAttempts_Marks_Failed_And_Stops()
    {
        await this.SeedAsync(Pending("a"));
        var transport = new FakeChatTransport { FailSends = true };
        var svc = this.NewService(transport, maxAttempts: 2);

        await svc.RunOutboundOnceAsync(default); // attempt 1
        var t2 = await svc.RunOutboundOnceAsync(default); // attempt 2 → failed

        Assert.Equal(1, t2.Failed);
        Assert.Equal(ChatNotificationStatuses.Failed, (await this.GetAsync("a")).Status);

        var t3 = await svc.RunOutboundOnceAsync(default);
        Assert.Equal(0, t3.Failed);
        Assert.Equal(0, t3.Sent);
    }

    [Fact]
    public async Task Expired_Pending_Row_Is_Marked_Expired_Never_Sent()
    {
        var row = Pending("a");
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await this.SeedAsync(row);
        var transport = new FakeChatTransport();

        var result = await this.NewService(transport).RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Expired);
        Assert.Equal(0, result.Sent);
        Assert.Empty(transport.Sent);
        Assert.Equal(ChatNotificationStatuses.Expired, (await this.GetAsync("a")).Status);
    }

    [Fact]
    public async Task Row_That_Throws_Is_Isolated_Others_Still_Send()
    {
        await this.SeedAsync(Pending("bad", chatId: "999"), Pending("good", chatId: "123"));
        var transport = new FakeChatTransport();
        transport.ThrowOnChatId.Add("999");

        var result = await this.NewService(transport).RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Sent);
        Assert.Equal(ChatNotificationStatuses.Sent, (await this.GetAsync("good")).Status);
        var bad = await this.GetAsync("bad");
        Assert.Equal(ChatNotificationStatuses.Pending, bad.Status);
        Assert.Equal(1, bad.Attempts);
        Assert.False(string.IsNullOrEmpty(bad.LastError));
    }
}
