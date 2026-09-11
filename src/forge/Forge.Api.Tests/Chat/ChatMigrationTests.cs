// -----------------------------------------------------------------------
// <copyright file="ChatMigrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class ChatMigrationTests
{
    [Fact]
    public async Task Preview_is_read_only_and_import_is_repeatable_with_original_states_and_timestamps()
    {
        var originals = new[] { "pending", "sent", "failed", "resolving", "answered", "acted", "expired", "superseded" }
            .Select(status => ChatMigrationTestFactory.Notification(status, status)).ToArray();
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(originals);
        var preview = await f.PreviewAsync();
        Assert.Equal("-100", preview.Source.GlobalChannel); Assert.Equal(41, preview.LegacyCursor);
        Assert.Equal(8, preview.Notifications.Values.Sum()); Assert.Single(preview.Scopes); Assert.Equal(1, preview.ConfirmedLinks);
        Assert.DoesNotContain(ChatMigrationTestFactory.Token, JsonSerializer.Serialize(preview));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnections.CountAsync()));
        Assert.Empty(f.Http.Calls);
        var first = await f.ImportAsync(); var second = await f.ImportAsync(); Assert.Equal(first.Revision + 1, second.Revision);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); Assert.False((await db.ChatConnections.SingleAsync()).Enabled);
            Assert.Single(await db.SpawnPlugins.ToListAsync()); Assert.False((await db.SpawnPlugins.SingleAsync()).Enabled);
            Assert.Equal(8, await db.ChatDeliveries.CountAsync()); Assert.Equal(8, await db.ChatDeliveryMessages.CountAsync());
            foreach (var original in originals)
            {
                var notification = await db.ChatNotifications.SingleAsync(n => n.Id == original.Id);
                var delivery = await db.ChatDeliveries.SingleAsync(d => d.NotificationId == original.Id);
                var part = await db.ChatDeliveryMessages.SingleAsync(m => m.DeliveryId == delivery.Id);
                Assert.Equal(original.Status, notification.Status); Assert.Equal(original.SentAt, notification.SentAt);
                Assert.Equal(original.RespondedAt, notification.RespondedAt); Assert.Equal(original.ChatMessageId, part.MessageId);
                Assert.Equal(original.CreatedAt.ToUnixTimeMilliseconds(), delivery.CreatedAt);
                Assert.Equal(original.Status == "resolving" ? "resolved" : original.Status, delivery.State);
            }
            Assert.All(await db.ChatMigrationItems.ToListAsync(), i => Assert.DoesNotContain(ChatMigrationTestFactory.Token, i.SnapshotJson));
        });
        Assert.Empty(f.Http.Calls);
    }

    [Fact]
    public async Task Failed_import_and_cutover_roll_back_every_write_and_can_be_retried()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("pending", "pending"));
        f.Fault.Armed = "import"; await Assert.ThrowsAsync<InvalidOperationException>(() => f.ImportAsync());
        Assert.Equal("not-imported", (await f.PreviewAsync()).Phase);
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnections.CountAsync()));
        await f.ImportAsync(); f.Fault.Armed = "cutover";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.CutoverAsync(new ChatMigrationSelection("p", "shared")));
        Assert.Equal("legacy", (await f.PreviewAsync()).Owner);
        Assert.Equal("telegram", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Select(n => n.Platform).SingleAsync()));
        var result = await f.CutoverAsync(new ChatMigrationSelection("p", "shared")); Assert.Equal("plugins", result.Owner); Assert.Equal(41, result.PluginCursor);
        Assert.Null(await f.Services.GetRequiredService<ChatEngineCoordinator>().EnterAsync("legacy"));
        Assert.Equal(0, (await f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default))).Sent);
        Assert.Empty(f.Http.Calls);
    }

    [Fact]
    public async Task Cutover_reuses_topics_sends_pending_once_and_new_children_inherit_only_the_selected_parent()
    {
        await using var f = new ChatMigrationTestFactory();
        await f.SetupAsync(ChatMigrationTestFactory.Notification("sent"), ChatMigrationTestFactory.Notification("pending", "pending"));
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); db.ChatNotifications.Add(ChatMigrationTestFactory.Notification("q", "sent", "workstream-stalled", "q"));
            await db.SaveChangesAsync();
        });
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared")); await f.TickAsync(); await f.TickAsync();
        Assert.Single(f.Http.Calls.Where(c => c.Method == "sendMessage"));
        Assert.DoesNotContain(f.Http.Calls, c => c.Method == "createForumTopic");
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.Workstreams.Add(new() { Id = "future-child", RepoId = "repo-p", StorySpawnId = "shared", Status = "running" }); await db.SaveChangesAsync();
            await sp.GetRequiredService<IChatNotifier>().EnqueueWorkstreamStalledAsync(new("p", "repo-p", "future-child", "waiting", "Alert", "Waiting", null, "shared"), default);
            await sp.GetRequiredService<IChatNotifier>().EnqueueWorkstreamStalledAsync(new("q", "repo-q", "child-0", "waiting", "Alert", "Waiting", null, "shared"), default);
        });
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var active = await db.SpawnPlugins.SingleAsync(p => p.ProductId == "p"); Assert.True(active.Enabled);
            Assert.False((await db.SpawnPlugins.SingleAsync(p => p.ProductId == "q")).Enabled);
            var routed = await db.ChatNotifications.SingleAsync(n => n.WorkstreamId == "future-child"); Assert.Equal("routed", routed.RouteState);
            var futureDelivery = await db.ChatDeliveries.SingleAsync(d => d.NotificationId == routed.Id);
            Assert.Equal("7", (await db.ChatConversationBindings.SingleAsync(b => b.Id == futureDelivery.BindingId)).ConversationId);
            Assert.Equal("disabled", (await db.ChatNotifications.SingleAsync(n => n.ProductId == "q" && n.Id != "q")).RouteReason);
            Assert.Equal(2, await db.ChatSubscriptions.CountAsync(s => s.ScopeKind == "spawn"));
        });
    }

    [Fact]
    public async Task Rollback_uses_latest_cursor_and_keeps_sent_messages_without_replay()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("sent"));
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared"));
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<ControlDbContext>(); (await db.ChatConnectionStates.SingleAsync()).Cursor = "97"; await db.SaveChangesAsync(); });
        var preview = await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a)); Assert.True(preview.Lossless); Assert.Equal(97, preview.LatestCursor);
        f.Fault.Armed = "rollback"; await Assert.ThrowsAsync<InvalidOperationException>(() => f.RollbackAsync());
        Assert.Equal("plugins", (await f.PreviewAsync()).Owner);
        var result = await f.RollbackAsync(); Assert.Equal("legacy", result.Owner); Assert.Equal(97, result.LegacyCursor);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var n = await db.ChatNotifications.SingleAsync();
            Assert.Equal("sent", n.Status); Assert.Equal("77-sent", n.ChatMessageId); Assert.Equal("old:sent", n.DedupeKey);
            Assert.Single(await db.ChatDeliveries.ToListAsync());
            Assert.Equal(0, (await sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default)).Sent);
            await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync();
        });
        Assert.Empty(f.Http.Calls.Where(c => c.Method == "sendMessage"));
        Assert.Null(await f.Services.GetRequiredService<ChatEngineCoordinator>().EnterAsync("plugins"));
        var again = await f.CutoverAsync(new ChatMigrationSelection("p", "shared")); Assert.Equal(97, again.PluginCursor);
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.CountAsync()));
    }
}