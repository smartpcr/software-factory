// -----------------------------------------------------------------------
// <copyright file="ChatMigrationBoundaryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class ChatMigrationBoundaryTests
{
    [Fact]
    public async Task Reused_parent_ids_do_not_rebind_earlier_topics_or_messages_to_a_new_run()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("old-run", "pending", "open-questions", "p"));
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.StorySpawns.SingleAsync()).CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1); await db.SaveChangesAsync();
        });
        var preview = await f.PreviewAsync(); Assert.Contains(preview.Issues, i => i.Reason == "topic-from-earlier-run");
        Assert.Contains(preview.Issues, i => i.Reason == "notification-from-earlier-run");
        await f.ImportAsync(); await Assert.ThrowsAsync<ChatConfigurationException>(() => f.CutoverAsync(new ChatMigrationSelection("p", "shared")));
        await f.CutoverAsync(); await f.TickAsync(); Assert.Empty(f.Http.Calls);
    }

    [Fact]
    public async Task Compatible_plugin_host_events_keep_their_dedupe_and_delivery_ids_through_rollback_and_recutover()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(); await f.ImportAsync(); await f.CutoverAsync();
        var alert = new QueueStrainAlert("", null, "plugin-crew", null, "Alert", "Queue waiting", null);
        Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(alert, default))); await f.TickAsync();
        var original = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.Select(d => d.Id).SingleAsync());
        Assert.True((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Lossless);
        await f.RollbackAsync();
        Assert.False(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(alert, default)));
        await f.CutoverAsync(); await f.TickAsync();
        Assert.Equal(original, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.Select(d => d.Id).SingleAsync()));
        Assert.Single(f.Http.Calls.Where(c => c.Method == "sendMessage"));
    }

    [Fact]
    public async Task Pending_legacy_questions_use_one_delivery_with_current_question_parts_and_can_roll_back_before_sending()
    {
        var old = ChatMigrationTestFactory.Notification("pending-questions", "pending", "open-questions", "p");
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(old);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var i = 1; i <= 2; i++) db.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q" + i, Iter = 1, Text = "Choose a safe value " + i });
            await db.SaveChangesAsync();
        });
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared", "redacted"));
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.CountAsync()));
        Assert.Equal(2, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.CountAsync()));
        Assert.True((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Lossless);
        await f.RollbackAsync(); Assert.Equal("pending", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Select(n => n.Status).SingleAsync()));
        await f.CutoverAsync(new ChatMigrationSelection("p", "shared", "redacted")); await f.TickAsync(); await f.TickAsync();
        Assert.Equal(2, f.Http.Calls.Count(c => c.Method == "sendMessage"));
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.ToListAsync()), p => Assert.NotNull(p.QuestionId));
        Assert.False((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Lossless);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_transport_unknown_outcomes_are_not_retried_before_or_after_cutover(bool missingReceipt)
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("uncertain", "pending"));
        f.Http.Override = (_, method, _, _) => method != "sendMessage" ? Task.FromResult<HttpResponseMessage?>(null)
            : missingReceipt ? Task.FromResult<HttpResponseMessage?>(TelegramProviderFixture.Ok(true))
            : throw new HttpRequestException("Ambiguous network disconnect");
        await f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default));
        Assert.Equal("uncertain", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Select(n => n.Status).SingleAsync()));
        await f.ImportAsync(); await f.CutoverAsync(); await f.TickAsync();
        Assert.Single(f.Http.Calls.Where(c => c.Method == "sendMessage"));
        Assert.Contains((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Blockers, b => b.Reason == "outcome-or-edit-not-settled");
    }

    [Fact]
    public async Task Receiver_handoff_waits_for_the_final_legacy_cursor_and_rollback_stops_the_plugin_poll()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(); await f.ImportAsync();
        var legacyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Override = async (_, method, _, ct) =>
        {
            if (method != "getUpdates") return null;
            legacyStarted.TrySetResult(); await legacyFinish.Task.WaitAsync(ct);
            return TelegramProviderFixture.Ok(new[] { TelegramProviderFixture.Message(55) });
        };
        var poll = f.ReadAsync(sp => sp.GetRequiredService<ChatInboundPump>().RunOnceAsync(default));
        await legacyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cutover = f.CutoverAsync(); Assert.False(cutover.IsCompleted); legacyFinish.TrySetResult();
        Assert.Equal(1, await poll); Assert.Equal(55, (await cutover).PluginCursor);
        var pluginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pluginStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Override = async (_, method, body, ct) =>
        {
            if (method != "getUpdates") return null;
            Assert.Equal("56", body.GetProperty("offset").ToString()); pluginStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }
            finally { pluginStopped.TrySetResult(); }
        };
        await f.Services.GetRequiredService<ChatConnectionSupervisor>().RefreshAsync(default);
        await pluginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ChatInboundPump>().RunOnceAsync(default)));
        var rollback = await f.RollbackAsync(); Assert.True(pluginStopped.Task.IsCompleted); Assert.Equal(55, rollback.LegacyCursor);
        f.Http.Override = null;
    }

    [Fact]
    public async Task Rollback_pauses_multipart_and_Slack_routes_and_retains_their_original_history()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(); await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared"));
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.ChatConnections.Add(new() { Id = "slack", ProviderId = "slack", InstallationId = "T1" });
            db.ChatDestinations.Add(new() { Id = "slack-channel", ConnectionId = "slack", ExternalTenantId = "T1", ChannelId = "C1" });
            db.ChatSubscriptions.Add(new() { Id = "slack-route", ScopeKind = "host", ScopeKey = ChatConfigurationScope.Host("test-host").Key, HostId = "test-host", DestinationId = "slack-channel", ConversationPolicy = "thread" });
            var product = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var i = 1; i <= 2; i++) product.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q" + i, Iter = 1, Text = "Question " + i });
            await product.SaveChangesAsync(); await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", "plugin-question", "p", "shared", "child-0"));
        });
        var preview = await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a));
        Assert.False(preview.Lossless); Assert.Empty(preview.Blockers); Assert.Equal(2, preview.UnsupportedSubscriptions.Count);
        await Assert.ThrowsAsync<ChatConfigurationException>(() => f.RollbackAsync()); await f.RollbackAsync(true);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            Assert.Equal("plugins", (await db.ChatNotifications.SingleAsync()).Platform);
            Assert.Equal(2, await db.ChatDeliveryMessages.CountAsync()); Assert.Single(await db.ChatDeliveries.ToListAsync());
            Assert.All(await db.ChatSubscriptions.Where(s => preview.UnsupportedSubscriptions.Contains(s.Id)).ToListAsync(), s => Assert.True(s.Paused));
        });
        Assert.Empty(f.Http.Calls);
    }

    [Fact]
    public async Task Reused_question_ids_and_changed_choice_values_do_not_gain_authority_at_import()
    {
        var old = ChatMigrationTestFactory.Notification("old", "sent", "open-questions", "p");
        old.ActionsJson = ChatActions.Serialize([new("Old", "oq|q1|0")]);
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(old);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q1", Iter = 1, Text = "Choose a safe value 1", ChoicesJson = "[\"Old\"]" });
            db.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q1", Iter = 2, Text = "Choose a safe value 1", ChoicesJson = "[\"Replacement\"]" });
            await db.SaveChangesAsync();
        });
        await f.ImportAsync(); var result = await f.CutoverAsync(new ChatMigrationSelection("p", "shared", "redacted"));
        Assert.Contains(result.Issues, i => i.Reason == "legacy-question-revision-unverified");
        await f.ReceiveAsync("50", "77-old", "oq|q1|0");
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.ToListAsync()), q => Assert.Null(q.Answer));
    }

    [Fact]
    public async Task Ambiguous_topics_are_held_for_repair_and_artifact_questions_use_verified_parent_ownership()
    {
        var ambiguous = ChatMigrationTestFactory.Notification("ambiguous", "pending", "workstream-stalled", "p");
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ambiguous);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.ChatSpawnTopics.Add(new() { ProductId = "missing", SpawnId = "unverified", ChatId = "-100", ThreadId = "7" });
            await db.SaveChangesAsync();
        });
        Assert.Contains((await f.PreviewAsync()).Issues, i => i.Reason == "ambiguous-topic");
        await f.ImportAsync();
        await Assert.ThrowsAsync<ChatConfigurationException>(() => f.CutoverAsync(new ChatMigrationSelection("p", "shared")));
        await f.CutoverAsync(); await f.TickAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var delivery = await db.ChatDeliveries.SingleAsync();
            Assert.Equal("blocked", delivery.State); Assert.Equal("ambiguous-topic", delivery.LastError);
            Assert.Equal("host", (await db.ChatNotifications.SingleAsync()).ScopeKind);
            Assert.Equal("7", JsonSerializer.Deserialize<ChatAddress>((await db.ChatDeliveryMessages.SingleAsync()).AddressJson)!.ConversationId);
        });
        Assert.DoesNotContain(f.Http.Calls, c => c.Method is "sendMessage" or "createForumTopic");
        await using var artifact = new ChatMigrationTestFactory();
        var alert = ChatMigrationTestFactory.Notification("artifact", "sent", "open-questions", "p");
        alert.WorkItemId = "plan-doc:shared:architecture.md";
        await artifact.SetupAsync(alert);
        await artifact.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.DeliverableQuestions.Add(new() { WorkItemId = alert.WorkItemId, ExternalId = "q1", Iter = 1, Text = "Choose a safe value 1" }); await db.SaveChangesAsync();
        });
        await artifact.ImportAsync(); await artifact.CutoverAsync(new ChatMigrationSelection("p", "shared"));
        var result = await artifact.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.SingleAsync());
        Assert.Equal("spawn", result.ScopeKind); Assert.Equal("shared", result.SpawnId); Assert.Equal("architecture", result.ArtifactKey);
    }

    [Fact]
    public async Task Imported_resolving_messages_edit_the_original_anchor_without_sending_again()
    {
        var resolving = ChatMigrationTestFactory.Notification("resolved", "resolving"); resolving.ChatMessageId = "77";
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(resolving); await f.ImportAsync(); await f.CutoverAsync();
        await f.TickAsync(); await f.TickAsync();
        Assert.DoesNotContain(f.Http.Calls, c => c.Method == "sendMessage");
        var edit = Assert.Single(f.Http.Calls.Where(c => c.Method == "editMessageText"));
        Assert.Equal("77", edit.Body.GetProperty("message_id").GetString());
        var part = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.SingleAsync());
        Assert.Equal("none", part.EditState); Assert.True(part.ControlsRetired);
        await f.RollbackAsync();
        Assert.Equal("acted", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Select(n => n.Status).SingleAsync()));
    }

    [Fact]
    public async Task Old_workstream_controls_check_the_bound_target_and_reject_a_later_domain_revision()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync();
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.Workstreams.SingleAsync(w => w.Id == "child-0")).Status = "stalled";
            db.Deliverables.Add(new() { WorkItemId = "child-0", Iter = 1, Verdict = "fail", CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
            var control = sp.GetRequiredService<ControlDbContext>(); control.ChatNotifications.Add(ChatMigrationTestFactory.Notification("stalled", "sent", "workstream-stalled", "p")); await control.SaveChangesAsync();
        });
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared", "redacted"));
        await f.ReceiveAsync("50", "77-stalled", "act|retry|child-1");
        await f.ReceiveAsync("51", "77-stalled", "act|retry|child-0");
        Assert.Equal("unmatched-message", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "50").Select(i => i.Result).SingleAsync()));
        Assert.Equal("accepted", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "51").Select(i => i.Result).SingleAsync()));
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.Deliverables.Add(new() { WorkItemId = "child-0", Iter = 2, Verdict = "fail", CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        });
        await f.ReceiveAsync("52", "77-stalled", "act|cancel");
        Assert.NotEqual("accepted", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "52").Select(i => i.Result).SingleAsync()));
        Assert.Single(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.ToListAsync()));
    }

    [Fact]
    public async Task Reemitting_the_same_legacy_alert_does_not_replay_after_cutover_and_rollback_keeps_future_parents_off()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync();
        var alert = new QueueStrainAlert("", null, "same-crew", null, "Alert", "Queue busy", null);
        Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(alert, default)));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default));
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared"));
        Assert.False(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(alert, default)));
        await f.TickAsync(); Assert.Single(f.Http.Calls.Where(c => c.Method == "sendMessage"));
        await f.RollbackAsync();
        Assert.False(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueWorkstreamStalledAsync(new("q", "repo-q", "child-0", "waiting", "Alert", "Waiting", null, "shared"), default)));
        Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueWorkstreamStalledAsync(new("p", "repo-p", "child-1", "waiting", "Alert", "Waiting", null, "shared"), default)));
    }

    [Fact]
    public async Task Interrupted_legacy_send_is_visible_and_never_becomes_a_fresh_send()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("interrupted", "sending"));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default));
        Assert.Equal("uncertain", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Select(n => n.Status).SingleAsync()));
        await f.ImportAsync(); await f.CutoverAsync(); await f.TickAsync();
        Assert.Equal("uncertain", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.Select(d => d.State).SingleAsync()));
        Assert.DoesNotContain(f.Http.Calls, c => c.Method == "sendMessage");
    }

    [Fact]
    public async Task Cutover_waits_for_legacy_io_and_imports_events_accepted_during_the_handoff()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("inflight", "pending"));
        await f.ImportAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Override = async (_, method, _, ct) =>
        {
            if (method != "sendMessage") return null;
            started.TrySetResult(); await finish.Task.WaitAsync(ct); return TelegramProviderFixture.Ok(new { message_id = 555 });
        };
        var send = f.ReadAsync(sp => sp.GetRequiredService<ChatBridgeService>().RunOutboundOnceAsync(default));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cutover = f.CutoverAsync(new ChatMigrationSelection("p", "shared"));
        try
        {
            Assert.False(cutover.IsCompleted);
            Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(new("", null, "during", null, "Alert", "Queue busy", null), default)));
            Assert.Equal("legacy", (await f.PreviewAsync()).Owner);
            Assert.DoesNotContain(f.Http.Calls, c => c.Method == "getUpdates");
        }
        finally { finish.TrySetResult(); }
        Assert.Equal(1, (await send).Sent); Assert.Equal("plugins", (await cutover).Owner);
        var imported = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveryMessages.ToListAsync());
        Assert.Equal(2, imported.Count); Assert.Contains(imported, m => m.MessageId == "555" && m.State == "sent");
        f.Http.Override = null; await f.TickAsync(); await f.TickAsync();
        Assert.Equal(2, f.Http.Calls.Count(c => c.Method == "sendMessage"));
    }

    [Fact]
    public async Task Old_question_buttons_and_replies_keep_the_original_anchor_choice_and_current_operator_authority()
    {
        var old = ChatMigrationTestFactory.Notification("questions", "sent", "open-questions", "p");
        old.ActionsJson = ChatActions.Serialize([new("First", "oq|q1|0"), new("Second", "oq|q1|1")]);
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(old);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            for (var i = 1; i <= 2; i++) db.DeliverableQuestions.Add(new() { WorkItemId = "child-0", ExternalId = "q" + i, Iter = 1,
                Text = "Choose a safe value " + i, ChoicesJson = "[\"First\",\"Second\"]" });
            await db.SaveChangesAsync();
        });
        await f.ImportAsync(); await f.CutoverAsync(new ChatMigrationSelection("p", "shared", "redacted"));
        await f.ReceiveAsync("50", "77-questions", text: "ambiguous");
        await f.ReceiveAsync("51", "77-questions", "oq|q1|1", channel: "-200");
        await f.ReceiveAsync("52", "77-questions", "oq|q1|1");
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            Assert.Equal("select-one-question", (await db.ChatInbox.SingleAsync(i => i.DeliveryKey == "50")).Result);
            Assert.Equal("legacy-alias-repair-required", (await db.ChatInbox.SingleAsync(i => i.DeliveryKey == "51")).Result);
            Assert.Equal("answered-more-remain", (await db.ChatInbox.SingleAsync(i => i.DeliveryKey == "52")).Result);
            var questions = await sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.ToListAsync();
            Assert.Equal("Second", questions.Single(q => q.ExternalId == "q1").Answer); Assert.Null(questions.Single(q => q.ExternalId == "q2").Answer);
        });
        f.Services.GetRequiredService<HostIdentityStore>().RevokeLinks("owner-p");
        await f.ReceiveAsync("53", "77-questions", text: "unauthorized");
        Assert.Equal("not-authorized", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "53").Select(i => i.Result).SingleAsync()));
        f.Link("owner-p", "-100", "42"); // A new confirmation is not the revoked migration proof.
        await f.ReceiveAsync("54", "77-questions", text: "still unauthorized");
        Assert.Equal("not-authorized", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == "54").Select(i => i.Result).SingleAsync()));
    }

    [Fact]
    public async Task Dpapi_import_keeps_the_original_reference_and_restart_uses_the_rotated_rollback_credential()
    {
        var folder = Path.Combine(Path.GetTempPath(), "forge-migration-" + Guid.NewGuid().ToString("N"));
        var store = new ChatDpapiSecretStore(folder); await store.WriteAsync("source", ChatMigrationTestFactory.Token);
        var path = Directory.GetFiles(folder).Single(); var original = await File.ReadAllBytesAsync(path);
        try
        {
            await using var f = new ChatMigrationTestFactory(path); await f.SetupAsync();
            Assert.Equal("dpapi:" + path, (await f.PreviewAsync()).Source.Credential.Key);
            await f.ImportAsync(); await f.ImportAsync(); await f.CutoverAsync();
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            var rotated = "12345:ROTATED_ABCDEFGHIJKLMNOPQRSTUVWXYZ_987654321";
            await f.WithAsync(async sp =>
            {
                var protectedStore = sp.GetServices<IChatSecretStore>().Single(s => s.Id == "dpapi");
                var reference = await protectedStore.WriteAsync("rotated", rotated);
                var db = sp.GetRequiredService<ControlDbContext>();
                (await db.ChatConnections.SingleAsync()).SecretReferencesJson = JsonSerializer.Serialize(new Dictionary<string, ChatSecretReference> { ["bot-token"] = reference });
                await db.SaveChangesAsync();
            });
            await f.RollbackAsync();
            var transport = f.Services.GetRequiredService<IOptionsMonitor<ChatTransportOptions>>(); transport.CurrentValue.Telegram.BotToken = "stale";
            var restarted = ActivatorUtilities.CreateInstance<ChatEngineCoordinator>(f.Services);
            await restarted.InitializeAsync();
            Assert.Equal(rotated, transport.CurrentValue.Telegram.BotToken);
            Assert.DoesNotContain(rotated, JsonSerializer.Serialize(await f.PreviewAsync()));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }

    [Fact]
    public async Task Compatibility_endpoints_use_the_migrated_group_and_retirement_keeps_host_alerts_active()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(); await f.ImportAsync(); await f.CutoverAsync();
        using var admin = await f.PairedClientAsync("admin"); using var user = await f.PairedClientAsync("owner-p");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/chat/migration/preview")).StatusCode);
        var response = await admin.PostAsJsonAsync("/api/chat/test-send", new { text = "Migration check" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("-100", check.GetProperty("chatId").GetString());
        Assert.False(string.IsNullOrEmpty(check.GetProperty("messageId").GetString()));
        Assert.Equal("-100", f.Http.Calls.Last(c => c.Method == "sendMessage").Body.GetProperty("chat_id").GetString());
        var selected = await admin.PostAsJsonAsync("/api/chat/test-send", new { chatId = "-200", text = "Selected destination" });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal("-200", f.Http.Calls.Last(c => c.Method == "sendMessage").Body.GetProperty("chat_id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/chat/test-send", new { chatId = "unregistered" })).StatusCode);
        var challenge = await user.PostAsJsonAsync("/api/chat/link-token", new { provider = "telegram", tenantId = "-100", providerUserId = "43" });
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode); Assert.True(challenge.Headers.CacheControl!.NoStore);
        Assert.Equal("12345", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatLinkChallenges.Select(c => c.ExternalTenantId).SingleAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync("/api/chat/link")).StatusCode);
        Assert.False(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatLinkChallenges.AnyAsync(c => c.State == "pending")));
        var revision = (await f.PreviewAsync()).Revision;
        await f.MigrateAsync((s, a) => s.RetireCompatibilityAsync(a, revision));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/chat/test-send", new { })).StatusCode);
        Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<IChatNotifier>().EnqueueQueueStrainAsync(new("", null, "new-host-event", null, "Alert", "Queue waiting", null), default)));
        await f.TickAsync();
        Assert.Equal(3, f.Http.Calls.Count(c => c.Method == "sendMessage"));
        Assert.False((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Lossless);
        Assert.Equal("plugins", f.Services.GetRequiredService<IOptions<ChatPluginOptions>>().Value.Engine);
    }

    [Fact]
    public async Task Rollback_refuses_unknown_sends_and_outstanding_edits_and_requires_explicit_pause_for_plugin_routes()
    {
        await using var f = new ChatMigrationTestFactory(); await f.SetupAsync(ChatMigrationTestFactory.Notification("unknown", "sending"));
        await f.ImportAsync(); await f.CutoverAsync();
        var unknown = await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a)); Assert.False(unknown.Lossless);
        Assert.Contains(unknown.Blockers, b => b.Reason == "outcome-or-edit-not-settled");
        await Assert.ThrowsAsync<ChatConfigurationException>(() => f.RollbackAsync(true));
        foreach (var editState in new[] { "pending", "editing" })
        {
            await f.WithAsync(async sp =>
            {
                var db = sp.GetRequiredService<ControlDbContext>(); var delivery = await db.ChatDeliveries.SingleAsync(); delivery.State = "sent";
                var part = await db.ChatDeliveryMessages.SingleAsync(); part.State = "sent"; part.MessageId = "991"; part.EditState = editState;
                await db.SaveChangesAsync();
            });
            Assert.Contains((await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a))).Blockers, b => b.Reason == "outcome-or-edit-not-settled");
            await Assert.ThrowsAsync<ChatConfigurationException>(() => f.RollbackAsync(true));
            Assert.Equal("plugins", f.Services.GetRequiredService<IOptions<ChatPluginOptions>>().Value.Engine);
        }
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); (await db.ChatDeliveryMessages.SingleAsync()).EditState = "none";
            var original = await db.ChatSubscriptions.FirstAsync();
            db.ChatDestinations.Add(new() { Id = "second-channel", ConnectionId = "legacy-telegram", ExternalTenantId = "12345", ChannelId = "-300", DisplayName = "Another channel" });
            db.ChatSubscriptions.Add(new() { Id = "plugin-only", ScopeKey = original.ScopeKey, ScopeKind = "host", HostId = original.HostId, DestinationId = "second-channel", ConversationPolicy = "flat" });
            await db.SaveChangesAsync();
        });
        var extra = await f.MigrateAsync((s, a) => s.RollbackPreviewAsync(a)); Assert.Contains("plugin-only", extra.UnsupportedSubscriptions); Assert.False(extra.Lossless);
        await Assert.ThrowsAsync<ChatConfigurationException>(() => f.RollbackAsync()); await f.RollbackAsync(true);
        Assert.True(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatSubscriptions.Where(s => s.Id == "plugin-only").Select(s => s.Paused).SingleAsync()));
    }
}