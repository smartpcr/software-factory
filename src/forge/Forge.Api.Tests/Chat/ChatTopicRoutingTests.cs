// -----------------------------------------------------------------------
// <copyright file="ChatTopicRoutingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 95 (Part B) — per-spawn forum-topic routing via the Common.Chat
/// channel map + Forge's spawn-topic store: the map creates + caches a topic
/// per spawn route, resolves an inbound thread back to its spawn route, and
/// the outbound pump sends a spawn-scoped alert into that topic's thread.
/// Group mode is gated on <c>SupergroupChatId</c>.
/// </summary>
public class ChatTopicRoutingTests : IAsyncLifetime
{
    private const long Supergroup = 424242;

    private SqliteConnection conn = null!;
    private ServiceProvider provider = null!;
    private FakeChatTransport transport = null!;

    public async Task InitializeAsync()
    {
        this.conn = new SqliteConnection("DataSource=:memory:");
        await this.conn.OpenAsync();
        this.transport = new FakeChatTransport();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlDbContext>(o => o.UseSqlite(this.conn));
        services.AddSingleton<IChatTransport>(this.transport);
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ChatBridgeOptions>>(
            new StaticOptionsMonitor<ChatBridgeOptions>(new ChatBridgeOptions { Enabled = true }));
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ChatTransportOptions>>(
            new StaticOptionsMonitor<ChatTransportOptions>(GroupOptions()));
        services.AddChatChannelMap<ForgeChatChannelStore>();
        this.provider = services.BuildServiceProvider();

        using var scope = this.provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.conn.DisposeAsync();
    }

    private static ChatTransportOptions GroupOptions()
    {
        var opt = new ChatTransportOptions { Enabled = true };
        opt.Telegram.SupergroupChatId = Supergroup;
        return opt;
    }

    private ChatBridgeService NewPump() => new(
        this.provider.GetRequiredService<IServiceScopeFactory>(),
        this.transport,
        this.provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ChatBridgeOptions>>(),
        this.provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ChatTransportOptions>>(),
        NullLogger<ChatBridgeService>.Instance);

    [Fact]
    public void Spawn_Route_Key_Round_Trips()
    {
        var key = ForgeChatRoutes.Spawn("prod-1", "spawn-9");
        Assert.True(ForgeChatRoutes.TryParseSpawn(key, out var productId, out var spawnId));
        Assert.Equal("prod-1", productId);
        Assert.Equal("spawn-9", spawnId);

        Assert.False(ForgeChatRoutes.TryParseSpawn(null, out _, out _));
        Assert.False(ForgeChatRoutes.TryParseSpawn("spawn/only-one-part", out _, out _));
        Assert.False(ForgeChatRoutes.TryParseSpawn("other/x/y", out _, out _));
    }

    [Fact]
    public async Task Map_Creates_Then_Caches_A_Topic_Per_Spawn_Route()
    {
        var route = ForgeChatRoutes.Spawn("prod", "spawn-1");
        ChatChannelAddress? first, second;
        using (var scope = this.provider.CreateScope())
        {
            var map = scope.ServiceProvider.GetRequiredService<IChatChannelMap>();
            first = await map.EnsureChannelAsync(route, "Story A", default);
        }

        using (var scope = this.provider.CreateScope())
        {
            var map = scope.ServiceProvider.GetRequiredService<IChatChannelMap>();
            second = await map.EnsureChannelAsync(route, "Story A", default);
        }

        Assert.False(string.IsNullOrEmpty(first?.ThreadId));
        Assert.Equal(first!.ThreadId, second?.ThreadId);

        // Created exactly once, against the supergroup.
        Assert.Single(this.transport.TopicsCreated);
        Assert.Equal(Supergroup.ToString(), this.transport.TopicsCreated[0].ChatId);

        using var check = this.provider.CreateScope();
        var control = check.ServiceProvider.GetRequiredService<ControlDbContext>();
        var row = await control.ChatSpawnTopics.AsNoTracking().SingleAsync(t => t.SpawnId == "spawn-1");
        Assert.Equal(first.ThreadId, row.ThreadId);
        Assert.Equal("prod", row.ProductId);
    }

    [Fact]
    public async Task Map_Resolves_An_Inbound_Thread_Back_To_Its_Spawn_Route()
    {
        var route = ForgeChatRoutes.Spawn("prod", "spawn-7");
        ChatChannelAddress? address;
        using (var scope = this.provider.CreateScope())
        {
            var map = scope.ServiceProvider.GetRequiredService<IChatChannelMap>();
            address = await map.EnsureChannelAsync(route, "Story Seven", default);
        }

        using (var scope = this.provider.CreateScope())
        {
            var map = scope.ServiceProvider.GetRequiredService<IChatChannelMap>();
            var resolved = await map.ResolveRouteAsync(address!.ChatId, address.ThreadId, default);
            Assert.Equal(route, resolved);

            // Group root (no thread) and unknown threads resolve to no route.
            Assert.Null(await map.ResolveRouteAsync(address.ChatId, threadId: null, default));
            Assert.Null(await map.ResolveRouteAsync(address.ChatId, "does-not-exist", default));
        }
    }

    [Fact]
    public async Task Map_Returns_Null_In_Single_Chat_Mode()
    {
        using var scope = this.provider.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var map = new ChatChannelMap(
            new ForgeChatChannelStore(control),
            this.transport,
            new StaticOptionsMonitor<ChatTransportOptions>(new ChatTransportOptions { Enabled = true }), // SupergroupChatId == 0
            NullLogger<ChatChannelMap>.Instance);

        var address = await map.EnsureChannelAsync(ForgeChatRoutes.Spawn("prod", "spawn-1"), "Story A", default);

        Assert.Null(address);
        Assert.Empty(this.transport.TopicsCreated);
    }

    [Fact]
    public async Task Outbound_Routes_Spawn_Alert_Into_Its_Topic_Thread()
    {
        using (var scope = this.provider.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n1",
                Kind = "open-questions",
                ProductId = "prod",
                WorkItemId = "wi",
                SpawnId = "spawn-9",
                DedupeKey = "k:n1",
                Status = ChatNotificationStatuses.Pending,
                ChatId = Supergroup.ToString(),
                Title = "Story Nine",
                Body = "please answer",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await control.SaveChangesAsync();
        }

        var result = await this.NewPump().RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Sent);
        Assert.Single(this.transport.Sent);

        // Topic created for the spawn, and the send carried its thread id.
        Assert.Single(this.transport.TopicsCreated);
        var threadId = this.transport.SentThreadIds[0];
        Assert.False(string.IsNullOrEmpty(threadId));

        using var check = this.provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ControlDbContext>();
        var topic = await db.ChatSpawnTopics.AsNoTracking().SingleAsync(t => t.SpawnId == "spawn-9");
        Assert.Equal(threadId, topic.ThreadId);
    }

    [Fact]
    public async Task Outbound_Two_Spawns_Land_In_Two_Different_Topics()
    {
        using (var scope = this.provider.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            foreach (var (id, spawn) in new[] { ("nA", "spawn-A"), ("nB", "spawn-B") })
            {
                control.ChatNotifications.Add(new ChatNotificationRow
                {
                    Id = id,
                    Kind = "open-questions",
                    ProductId = "prod",
                    WorkItemId = "wi-" + spawn,
                    SpawnId = spawn,
                    DedupeKey = "k:" + id,
                    Status = ChatNotificationStatuses.Pending,
                    ChatId = Supergroup.ToString(),
                    Title = "Story " + spawn,
                    Body = "q",
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                });
            }

            await control.SaveChangesAsync();
        }

        var result = await this.NewPump().RunOutboundOnceAsync(default);

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, this.transport.TopicsCreated.Count);
        // Two distinct, non-null thread ids.
        var threads = this.transport.SentThreadIds.Where(t => t is not null).Distinct().ToList();
        Assert.Equal(2, threads.Count);
    }

    [Fact]
    public async Task Outbound_Topic_Is_Named_After_The_Spawn_Not_The_Alert_Title()
    {
        // Regression: the topic hosts every alert kind for the story's lifetime,
        // but it was named after whichever alert happened to create it — leaving
        // topics permanently titled "⚠ Stalled · Stage 1.1".
        using (var scope = this.provider.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n4",
                Kind = "workstream-stalled",
                ProductId = "prod",
                WorkstreamId = "ws-4",
                SpawnId = "spawn-release-RELEASE-PROVIDER",
                DedupeKey = "k:n4",
                Status = ChatNotificationStatuses.Pending,
                ChatId = Supergroup.ToString(),
                Title = "⚠ Stalled · Stage 1.1",
                Body = "stall reason",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await control.SaveChangesAsync();
        }

        var result = await this.NewPump().RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Sent);
        Assert.Single(this.transport.TopicsCreated);
        Assert.Equal("📖 release-RELEASE-PROVIDER", this.transport.TopicsCreated[0].Name);
    }

    [Fact]
    public async Task Outbound_Spawn_Alert_Without_ProductId_Falls_Back_To_Group_Root()
    {
        // Regression (release RELEASE-PROVIDER, 2026-07-17): a spawn-scoped
        // alert whose producer left ProductId empty rendered the malformed
        // route key `spawn//spawn-…`, which the channel store rejects — the
        // send threw and the alert burned all attempts to `failed`. Delivery
        // must not die on a topic-routing detail: fall back to the group root.
        using (var scope = this.provider.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n3",
                Kind = "open-questions",
                ProductId = string.Empty,
                WorkItemId = "wi-3",
                SpawnId = "spawn-3",
                DedupeKey = "k:n3",
                Status = ChatNotificationStatuses.Pending,
                ChatId = Supergroup.ToString(),
                Title = "Story Three",
                Body = "please answer",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await control.SaveChangesAsync();
        }

        var result = await this.NewPump().RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Sent);
        Assert.Single(this.transport.Sent);
        Assert.Null(this.transport.SentThreadIds[0]); // no product → no route key → group root.
        Assert.Empty(this.transport.TopicsCreated);

        using var check = this.provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ControlDbContext>();
        var row = await db.ChatNotifications.AsNoTracking().SingleAsync(r => r.Id == "n3");
        Assert.Equal(ChatNotificationStatuses.Sent, row.Status);
    }

    [Fact]
    public async Task Outbound_Spawnless_Alert_Goes_To_Group_Root_No_Thread()
    {
        using (var scope = this.provider.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.ChatNotifications.Add(new ChatNotificationRow
            {
                Id = "n2",
                Kind = "queue-strain",
                ProductId = "prod",
                DedupeKey = "k:n2",
                Status = ChatNotificationStatuses.Pending,
                ChatId = Supergroup.ToString(),
                Title = "Queue strain",
                Body = "crew backed up",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await control.SaveChangesAsync();
        }

        var result = await this.NewPump().RunOutboundOnceAsync(default);

        Assert.Equal(1, result.Sent);
        Assert.Null(this.transport.SentThreadIds[0]); // no spawn → no topic → group root.
        Assert.Empty(this.transport.TopicsCreated);
    }
}
