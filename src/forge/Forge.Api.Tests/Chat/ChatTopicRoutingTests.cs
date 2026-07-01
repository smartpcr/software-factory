// -----------------------------------------------------------------------
// <copyright file="ChatTopicRoutingTests.cs" company="Microsoft Corp.">
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
/// Phase 95 (Part B) — per-spawn forum-topic routing: the resolver creates +
/// caches a topic per spawn, and the outbound pump sends a spawn-scoped alert
/// into that topic's thread. Group mode is gated on <c>SupergroupChatId</c>.
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
            new StaticOptionsMonitor<ChatBridgeOptions>(GroupOptions()));
        services.AddScoped<IChatTopicResolver, ChatTopicResolver>();
        this.provider = services.BuildServiceProvider();

        using var scope = this.provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.conn.DisposeAsync();
    }

    private static ChatBridgeOptions GroupOptions()
    {
        var opt = new ChatBridgeOptions { Enabled = true };
        opt.Telegram.SupergroupChatId = Supergroup;
        return opt;
    }

    private ChatBridgeService NewPump() => new(
        this.provider.GetRequiredService<IServiceScopeFactory>(),
        this.transport,
        this.provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ChatBridgeOptions>>(),
        NullLogger<ChatBridgeService>.Instance);

    [Fact]
    public async Task Resolver_Creates_Then_Caches_A_Topic_Per_Spawn()
    {
        string? first, second;
        using (var scope = this.provider.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IChatTopicResolver>();
            first = await resolver.EnsureTopicAsync("spawn-1", "prod", "Story A", default);
        }

        using (var scope = this.provider.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IChatTopicResolver>();
            second = await resolver.EnsureTopicAsync("spawn-1", "prod", "Story A", default);
        }

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, second);

        // Created exactly once, against the supergroup.
        Assert.Single(this.transport.TopicsCreated);
        Assert.Equal(Supergroup.ToString(), this.transport.TopicsCreated[0].ChatId);

        using var check = this.provider.CreateScope();
        var control = check.ServiceProvider.GetRequiredService<ControlDbContext>();
        var row = await control.ChatSpawnTopics.AsNoTracking().SingleAsync(t => t.SpawnId == "spawn-1");
        Assert.Equal(first, row.ThreadId);
    }

    [Fact]
    public async Task Resolver_Returns_Null_In_Single_Chat_Mode()
    {
        using var scope = this.provider.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var resolver = new ChatTopicResolver(
            control,
            this.transport,
            new StaticOptionsMonitor<ChatBridgeOptions>(new ChatBridgeOptions { Enabled = true }), // SupergroupChatId == 0
            NullLogger<ChatTopicResolver>.Instance);

        var threadId = await resolver.EnsureTopicAsync("spawn-1", "prod", "Story A", default);

        Assert.Null(threadId);
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
