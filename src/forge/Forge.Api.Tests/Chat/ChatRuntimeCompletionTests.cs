// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeCompletionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatRuntimeCompletionTests
{
    [Fact]
    public async Task Mixed_connection_batch_cannot_rewind_a_duplicate_connections_cursor()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var alpha = new ChatInboundEnvelope("FakeAlpha", "alpha-one", "old", new("alpha-one", "tenant", "channel"), "operator", "text", "1", null, "safe", null, f.Clock.GetUtcNow());
        await f.ReceiveAsync(alpha, "1"); await f.ReceiveAsync(alpha with { DeliveryKey = "new" }, "2");
        var beta = alpha with { ProviderId = "FakeBeta", ConnectionId = "beta", Address = alpha.Address with { ConnectionId = "beta" } };
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            var revisions = await control.ChatConnections.ToDictionaryAsync(c => c.Id, c => c.Revision);
            Assert.Equal(ChatInboundAcceptance.Accepted, await sp.GetRequiredService<ChatInboxService>().AcceptBatchAsync([alpha, beta], [new("alpha-one", "1"), new("beta", "B")], revisions));
        });
        var cursors = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnectionStates.ToDictionaryAsync(s => s.ConnectionId, s => s.Cursor));
        Assert.Equal("2", cursors["alpha-one"]); Assert.Equal("B", cursors["beta"]);
    }

    [Fact]
    public async Task Failed_earlier_action_blocks_only_its_subscription_until_explicit_skip()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync();
        f.Alpha.Failures["alpha-one"] = new(ChatFailureKind.Unauthorized, "permission"); await f.TickAsync();
        var failed = (await f.DeliveriesAsync()).Single(d => d.ConnectionId == "alpha-one");
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.DeliverableQuestions.Add(new() { WorkItemId = "child-1", ExternalId = "next", Iter = 1, Text = "Choose next" }); await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", "next-child", "p", "shared", "child-1", Revision: 2));
        });
        await f.TickAsync();
        Assert.Equal(1, f.Alpha.SendCalls); Assert.Equal(2, f.Beta.SendCalls);
        Assert.Contains(await f.DeliveriesAsync(), d => d.ConnectionId == "alpha-one" && d.LastError == "blocked-by-earlier-action");
        var actor = await f.ProviderActorAsync("123", "owner-p");
        await f.WithAsync(sp => sp.GetRequiredService<ChatStatusService>().SkipAsync(actor, failed.Id, default));
        f.Clock.Advance(TimeSpan.FromSeconds(6)); await f.TickAsync();
        Assert.Equal(2, f.Alpha.SendCalls); Assert.Equal(2, f.Beta.SendCalls);
    }

    [Fact]
    public async Task Large_channel_backlog_does_not_starve_another_connection()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var original = await db.ChatNotifications.SingleAsync(n => n.Platform == "plugins");
            var copy = await db.ChatDeliveries.SingleAsync(d => d.ConnectionId == "alpha-one");
            for (var i = 0; i < 510; i++)
            {
                var notification = new ChatNotificationRow(); db.Entry(notification).CurrentValues.SetValues(original);
                notification.Id = Guid.NewGuid().ToString("N"); notification.DedupeKey = "backlog-" + i; db.ChatNotifications.Add(notification);
                var delivery = new ChatDeliveryRow(); db.Entry(delivery).CurrentValues.SetValues(copy);
                delivery.Id = Guid.NewGuid().ToString("N"); delivery.NotificationId = notification.Id; delivery.CreatedAt -= 1000;
                db.ChatDeliveries.Add(delivery); db.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id });
            }
            await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatDeliveryService>().RunOnceAsync();
        });
        Assert.Equal(1, f.Alpha.SendCalls); Assert.Equal(1, f.Beta.SendCalls);
    }

    [Fact]
    public async Task Reenable_does_not_replay_but_explicit_resend_revalidates_and_dedupes_request()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var parent = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        parent = await f.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision, false)));
        await f.EnqueueAsync();
        parent = await f.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision)));
        await f.TickAsync(); Assert.Empty(await f.DeliveriesAsync());
        var id = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Where(n => n.Platform == "plugins").Select(n => n.Id).SingleAsync());
        var actor = await f.ProviderActorAsync("123", "owner-p");
        await f.WithAsync(sp => sp.GetRequiredService<ChatStatusService>().ResendAsync(actor, id, "request-1", default));
        await f.WithAsync(sp => sp.GetRequiredService<ChatStatusService>().ResendAsync(actor, id, "request-1", default));
        Assert.Equal(2, (await f.DeliveriesAsync()).Count);
        Assert.DoesNotContain(await f.DeliveriesAsync(), d => d.NotificationId == id);
        await f.TickAsync(); Assert.Equal(1, f.Alpha.SendCalls); Assert.Equal(1, f.Beta.SendCalls);
    }

    [Fact]
    public async Task Restart_confirmation_is_short_actor_bound_and_expired_actions_never_mutate()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            var child = await db.Workstreams.SingleAsync(w => w.Id == "child-0"); child.Status = "stalled"; await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("workstream-stalled", "stalled", "p", "shared", WorkstreamId: "child-0"));
        });
        await f.TickAsync(); var sent = Assert.Single(f.Alpha.Sent);
        var restart = sent.Notification.Actions.Single(a => a.Kind == "workstream.restart");
        Assert.Matches("^a1_[A-Za-z0-9_-]{22}$", restart.Token);
        var envelope = new ChatInboundEnvelope("FakeAlpha", "alpha-one", "restart", sent.Address, "operator", "action", "incoming", sent.MessageId, null, restart.Token, f.Clock.GetUtcNow());
        await f.ReceiveAsync(envelope); await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        await f.TickAsync();
        var confirmation = f.Alpha.Sent.Last(); var confirmToken = Assert.Single(confirmation.Notification.Actions).Token;
        var stored = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatActionTokens.SingleAsync(t => t.Hash == ChatEventCatalog.Hash(confirmToken)));
        Assert.Equal("owner-p", stored.ActorId); Assert.Equal(f.Clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds(), stored.ExpiresAt);
        await f.EnrollAsync("other-owner", productId: "p");
        var identities = f.Services.GetRequiredService<HostIdentityStore>();
        var link = identities.IssueLinkChallenge("other-owner", "FakeAlpha", "tenant", "other-owner");
        Assert.NotNull(identities.RedeemLinkChallenge(link, "FakeAlpha", "tenant", "other-owner"));
        await f.ReceiveAsync(envelope with { DeliveryKey = "wrong-actor", SenderId = "other-owner", Address = confirmation.Address, ReplyToMessageId = confirmation.MessageId, ActionToken = confirmToken });
        await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        Assert.Equal("confirmation-actor-mismatch", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "wrong-actor").Select(i => i.Result).SingleAsync()));
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        await f.ReceiveAsync(envelope with { DeliveryKey = "expired", Address = confirmation.Address, ReplyToMessageId = confirmation.MessageId, ActionToken = confirmToken });
        await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        Assert.Equal("stale-action", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "expired").Select(i => i.Result).SingleAsync()));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
    }

    [Fact]
    public async Task Seed_scrub_reports_dry_run_then_retires_exact_deleted_product_scope()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>(); var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            var preview = await ForgeSeedSpawnScrub.ScrubMockedSpawnsAsync(control, product, [], [], true);
            Assert.Contains("shared", preview.StorySpawnIdsDeleted); Assert.All(await control.ChatSubscriptions.ToListAsync(), s => Assert.Null(s.RetiredAt));
            var applied = await ForgeSeedSpawnScrub.ScrubMockedSpawnsAsync(control, product, [], [], false);
            Assert.Equal(preview.StorySpawnIdsDeleted, applied.StorySpawnIdsDeleted);
            Assert.Empty(await product.StorySpawns.ToListAsync());
            Assert.Single(await sp.GetRequiredService<IProductDbContextFactory>().CreateFor("q").StorySpawns.ToListAsync());
        });
        Assert.All(await f.DeliveriesAsync(), d => Assert.Equal("cancelled", d.State));
    }

    [Fact]
    public async Task Retention_obeys_content_deadlines_batch_limit_and_hash_expiry()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync(); await f.TickAsync();
        string expiredHash = ""; var tokensBefore = 0;
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var now = f.Clock.GetUtcNow();
            for (var i = 0; i < 105; i++) db.ChatNotifications.Add(new() { Id = "old-" + i, Platform = "plugins", Kind = "queue-strain", ProductId = "", DedupeKey = "old-" + i,
                Title = "old", Body = "old", RouteState = "unrouted", CreatedAt = now.AddDays(-8) });
            foreach (var (key, state, age) in new[] { ("processed", "processed", 8), ("failed-young", "dead-letter", 8), ("failed-old", "dead-letter", 31), ("unprocessed", "pending", 31) })
                db.ChatInbox.Add(new() { ConnectionId = "alpha-one", ProviderId = "FakeAlpha", DeliveryKey = key, State = state, EnvelopeJson = "{}",
                    ReceivedAt = now.AddDays(-age).ToUnixTimeMilliseconds(), TerminalAt = state == "pending" ? null : now.AddDays(-age).ToUnixTimeMilliseconds() });
            db.ChatLinkChallenges.Add(new() { Hash = "expired", ExpiresAt = now.AddDays(-2).ToUnixTimeMilliseconds() });
            var token = await db.ChatActionTokens.FirstAsync(); token.ExpiresAt = now.AddDays(-2).ToUnixTimeMilliseconds();
            expiredHash = token.Hash; tokensBefore = await db.ChatActionTokens.CountAsync();
            await db.SaveChangesAsync(); await sp.GetRequiredService<ChatRetentionService>().RunOnceAsync();
        });
        Assert.Equal(5, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.CountAsync(n => n.Title == "old")));
        var inbox = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.ToDictionaryAsync(i => i.DeliveryKey));
        Assert.Null(inbox["processed"].EnvelopeJson); Assert.NotNull(inbox["failed-young"].EnvelopeJson); Assert.Null(inbox["failed-old"].EnvelopeJson);
        Assert.Equal("expired", inbox["unprocessed"].State); Assert.Null(inbox["unprocessed"].EnvelopeJson);
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatLinkChallenges.CountAsync()));
        // Only the expired token is purged (questions also carry a Dismiss token since 2026-11-19).
        var tokens = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatActionTokens.ToListAsync());
        Assert.Equal(tokensBefore - 1, tokens.Count); Assert.DoesNotContain(tokens, t => t.Hash == expiredHash);
    }

    [Fact]
    public async Task Parent_status_selects_exact_targets_and_product_owner_purge_is_scoped()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync(); await f.TickAsync();
        var actor = await f.ProviderActorAsync("123", "owner-p");
        var status = JsonSerializer.SerializeToElement(await f.ReadAsync(sp => sp.GetRequiredService<ChatStatusService>().SpawnAsync(actor, "p", "shared", default)));
        Assert.Equal(32, status.GetProperty("Children").GetArrayLength()); Assert.Equal(1, status.GetProperty("PendingQuestionCount").GetInt32());
        Assert.True(status.GetProperty("Children")[0].GetProperty("ExpectedRevision").GetInt64() > 0);
        var envelope = f.Answer(f.Alpha, "alpha-one", "pending-answer"); await f.ReceiveAsync(envelope);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.ChatNotifications.Add(new() { Id = "other-product", Platform = "plugins", ProductId = "q", SpawnId = "shared", DedupeKey = "q", Body = "preserve", ScopeKind = "spawn" }); await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatRetentionService>().PurgeSpawnContentAsync(actor, "p", "shared", default);
        });
        Assert.Equal("preserve", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Where(n => n.Id == "other-product").Select(n => n.Body).SingleAsync()));
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Select(i => i.EnvelopeJson).SingleAsync()));
        await f.ReceiveAsync(envelope with { DeliveryKey = "after-purge" });
        await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
    }

    [Fact]
    public async Task Resolved_event_content_expires_at_thirty_days_and_keeps_correlations()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync(); await f.TickAsync();
        var tokens = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatActionTokens.ToListAsync());
        Assert.All(tokens, t => Assert.Equal(f.Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds(), t.ExpiresAt));
        await f.ReceiveAsync(f.Answer(f.Alpha, "alpha-one", "answer")); await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        await f.TickAsync();
        var notification = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.SingleAsync(n => n.Platform == "plugins"));
        Assert.Equal("resolved", notification.Status); Assert.NotNull(notification.RespondedAt);
        f.Clock.Advance(TimeSpan.FromDays(31)); await f.WithAsync(sp => sp.GetRequiredService<ChatRetentionService>().RunOnceAsync());
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Where(n => n.Id == notification.Id).Select(n => n.PayloadJson).SingleAsync()));
        Assert.Equal(2, (await f.DeliveriesAsync()).Count); Assert.All(await f.MessagesAsync(), m => Assert.Null(m.RenderedJson));
    }

    [Fact]
    public async Task Health_checkpoint_keeps_both_activity_stamps_and_retires_dead_receiver_ownership()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var runtime = f.Services.GetRequiredService<ChatRuntimeState>(); var supervisor = f.Services.GetRequiredService<ChatConnectionSupervisor>();
        runtime.RecordHealth("alpha-one", "ready", null, sent: 11); runtime.RecordHealth("alpha-one", "ready", null, received: 22);
        await supervisor.CheckpointHealthAsync(default);
        var health = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnectionStates.SingleAsync(s => s.ConnectionId == "alpha-one"));
        Assert.Equal(11, health.LastSendAt); Assert.Equal(22, health.LastReceiveAt);
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<ControlDbContext>(); db.ChatReceiverStates.Add(new() { ProviderId = "FakeAlpha", ReceptionKey = "retired-installation", State = "receiving", OwnerProcessInstanceId = "dead" }); await db.SaveChangesAsync(); });
        await supervisor.RefreshAsync(default);
        var retired = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatReceiverStates.SingleAsync(s => s.ReceptionKey == "retired-installation"));
        Assert.Equal("stopped", retired.State); Assert.NotNull(retired.RetiredAt);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); runtime.RecordHealth("alpha-one", "degraded", "receiver-disconnected"); await supervisor.CheckpointHealthAsync(default);
        var summaries = await f.ReadAsync(async sp =>
        {
            var spawn = await sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.SingleAsync();
            return await ChatSummaryService.ReadAsync(sp.GetRequiredService<ControlDbContext>(), new[] { (spawn, "p") });
        });
        Assert.Equal("degraded", Assert.Single(summaries).Value.State);
    }
}