// -----------------------------------------------------------------------
// <copyright file="ChatValidationMatrixTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatValidationMatrixTests
{
    [Fact]
    public async Task All_four_notifier_kinds_deliver_only_to_their_explicit_parent_or_host_scope()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(conversation: "topic");
        await f.RunAsync("admin", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Host("test-host"), "host-beta", new(0, 0, "dest-beta", "flat")));
        await f.WithAsync(async sp =>
        {
            var notifier = sp.GetRequiredService<IChatNotifier>();
            Assert.True(await notifier.EnqueueOpenQuestionsAsync(new("p", "repo-p", "child-0", 1, "Questions", "Choose a safe value 1", null, "shared"), default));
            Assert.True(await notifier.EnqueueWorkstreamStalledAsync(new("p", "repo-p", "child-1", "waiting", "Stalled", "Waiting", null, "shared"), default));
            Assert.True(await notifier.EnqueueQueueStrainAsync(new("q", "repo-q", "crew", "queue", "Queue", "Waiting", null), default));
            Assert.True(await notifier.EnqueueHumanInteractionAsync(new("q", "repo-q", "signin", "Sign in", "Host interaction required", null), default));
        });
        for (var tick = 0; tick < 6; tick++) await f.TickAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            var events = await db.ChatNotifications.Where(n => n.Platform == "plugins").ToListAsync();
            Assert.Equal(ChatEventCatalog.SpawnDefaults.Concat(ChatEventCatalog.HostDefaults).Order(), events.Select(n => n.Kind).Order());
            foreach (var notification in events)
            {
                var host = ChatEventCatalog.HostDefaults.Contains(notification.Kind);
                Assert.Equal(host ? "host" : "spawn", notification.ScopeKind);
                if (host) Assert.Equal("test-host", notification.HostId);
                else { Assert.Equal("p", notification.ProductId); Assert.Equal("shared", notification.SpawnId); }
                var copies = await db.ChatDeliveries.Where(d => d.NotificationId == notification.Id).ToListAsync();
                Assert.Equal(host ? 1 : 2, copies.Count); Assert.All(copies, d => Assert.Equal("sent", d.State));
                foreach (var delivery in copies)
                {
                    var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
                    Assert.Equal(notification.ScopeKind, target.ScopeKind);
                    if (host) Assert.Equal("host-beta", target.SubscriptionId);
                    else { Assert.Equal("p", target.ProductId); Assert.Equal("shared", target.SpawnId); }
                }
            }
        });
    }

    [Fact]
    public async Task All_32_children_and_a_later_child_share_one_parent_conversation_per_destination()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(conversation: "topic");
        await f.WithAsync(async sp =>
        {
            var notifier = sp.GetRequiredService<IChatNotifier>();
            for (var i = 0; i < 32; i++)
                Assert.True(await notifier.EnqueueWorkstreamStalledAsync(new("p", "repo-p", "child-" + i, "waiting", "Waiting", "Waiting", null, "shared"), default));
        });
        for (var tick = 0; tick < 40; tick++)
        {
            await f.TickAsync();
            if ((await f.DeliveriesAsync()).All(d => d.State == "sent")) break;
        }
        Assert.Equal(64, (await f.DeliveriesAsync()).Count); Assert.All(await f.DeliveriesAsync(), d => Assert.Equal("sent", d.State));
        var initialBindings = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.Id).ToListAsync());
        Assert.Equal(2, initialBindings.Count);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.Workstreams.Add(new() { Id = "later-child", RepoId = "repo-p", StorySpawnId = "shared", Status = "running" }); await db.SaveChangesAsync();
            Assert.True(await sp.GetRequiredService<IChatNotifier>().EnqueueWorkstreamStalledAsync(new("p", "repo-p", "later-child", "waiting", "Waiting", "Waiting", null, "shared"), default));
        });
        await f.TickAsync();
        var deliveries = await f.DeliveriesAsync(); Assert.Equal(66, deliveries.Count);
        Assert.All(deliveries, d => { Assert.Equal("sent", d.State); Assert.Contains(d.BindingId, initialBindings); });
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            Assert.Equal(2, await control.ChatSubscriptions.CountAsync()); Assert.Equal(2, await control.ChatConversationBindings.CountAsync());
            var messages = await control.ChatDeliveryMessages.ToListAsync(); Assert.Equal(66, messages.Count);
            foreach (var group in messages.GroupBy(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson)!.ConnectionId))
                Assert.Single(group.Select(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson)!.ConversationId).Distinct());
            var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            Assert.Equal(33, await product.Workstreams.CountAsync()); Assert.All(await product.Workstreams.ToListAsync(), w => Assert.Equal("running", w.Status));
        });
    }
}