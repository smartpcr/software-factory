// -----------------------------------------------------------------------
// <copyright file="ChatSlackRuntimeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatSlackRuntimeTests
{
    [Fact]
    public async Task Two_workspaces_share_one_live_socket_alongside_Telegram_and_disable_preserves_remaining_receiver()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(telegram: true, secondWorkspace: true);
        await f.EnqueueAsync(); await f.DeliverAsync(); await f.StartAsync();
        var socket = Assert.Single(f.Slack.Sockets);
        await SlackProviderFixture.UntilAsync(() => f.Telegram.Calls.Any(c => c.Method == "getUpdates"));
        await f.ReceiveAsync(SlackProviderFixture.Event("public", thread: (await f.AddressAsync()).ConversationId));
        await f.ReceiveAsync(SlackProviderFixture.Event("private", "T2", (await f.AddressAsync("slack-two")).ConversationId, channel: "G1"));
        Assert.Equal(new[] { "slack-one", "slack-two" }, (await f.InboxAsync()).Select(i => i.ConnectionId).Order().ToArray());
        await f.RunAsync("admin", async (s, a) =>
        {
            var config = await s.GetConnectionAsync(a, "slack-one");
            return await s.PutConnectionAsync(a, config.Id, new(config.Revision, config.ProviderId, config.InstallationId, config.DisplayName, false,
                config.SettingsVersion, config.Settings, config.SecretReferences, config.ProductIds));
        });
        await f.StartAsync(); Assert.False(socket.Disposed); Assert.Single(f.Slack.Sockets);
        await f.ReceiveAsync(SlackProviderFixture.Event("remaining", "T2", channel: "G1"));
        await f.ReceiveAsync(SlackProviderFixture.Event("disabled", "T1"));
        Assert.Equal(3, (await f.InboxAsync()).Count); // Disabled workspace is diagnosed, never dispatched as T2.
        var state = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatReceiverStates.AsNoTracking().SingleAsync(r => r.ProviderId == "slack"));
        Assert.Equal("slack-unknown-workspace", state.DiagnosticCode); Assert.Equal(1, state.DiagnosticCount); Assert.NotNull(state.DiagnosticAt);
        Assert.Equal("T1", state.DiagnosticTenantId);
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method is "conversations.replies" or "conversations.history");
    }

    [Fact]
    public async Task Ambiguous_thread_replies_never_guess_and_explicit_decimal_selector_is_scoped_and_deduplicated()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.DeliverAsync();
        var address = await f.AddressAsync(); var q2 = await f.MessageAsync("q2");
        await f.ReceiveAsync(SlackProviderFixture.Event("ambiguous", thread: address.ConversationId, text: "I choose this")); await f.DispatchAsync(); await f.RespondAsync();
        Assert.All(await f.QuestionsAsync(), q => Assert.Null(q.Answer)); Assert.Equal("select-one-question", Assert.Single(await f.InboxAsync()).Result);
        Assert.Contains(f.Slack.Calls, c => c.Method == "chat.postMessage" && c.Body.GetProperty("text").GetString()!.Contains("!forge answer", StringComparison.Ordinal));
        await f.ReceiveAsync(SlackProviderFixture.Event("explicit", thread: address.ConversationId, text: "!forge answer " + q2.MessageId + " precise answer"));
        await f.ReceiveAsync(SlackProviderFixture.Event("explicit", thread: address.ConversationId, text: "!forge answer " + q2.MessageId + " precise answer", envelopeId: "different-envelope"));
        await f.DispatchAsync(); Assert.Equal(2, (await f.InboxAsync()).Count);
        Assert.Equal("precise answer", (await f.QuestionsAsync()).Single(q => q.ExternalId == "q2").Answer);
        Assert.Null((await f.QuestionsAsync()).Single(q => q.ExternalId == "q1").Answer);
    }

    [Fact]
    public async Task Custom_selection_is_actor_bound_and_thread_text_answers_only_the_selected_question()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.DeliverAsync();
        var address = await f.AddressAsync(); var q2 = await f.MessageAsync("q2");
        await f.ReceiveAsync(SlackProviderFixture.Button("custom", q2.MessageId!, f.Button("q2", 2), thread: address.ConversationId));
        await f.DispatchAsync(); await f.DeliverAsync();
        var prompt = (await f.MessagesAsync()).Single(m => m.ActionKind == "question.prompt"); Assert.Equal("owner-p", prompt.ConfirmationActorId);
        await f.EnrollAsync("second-operator", productId: "p");
        var identities = f.Services.GetRequiredService<HostIdentityStore>();
        Assert.NotNull(identities.RedeemLinkChallenge(identities.IssueLinkChallenge("second-operator", "slack", "T1", "UOTHER"), "slack", "T1", "UOTHER"));
        await f.ReceiveAsync(SlackProviderFixture.Event("other-actor", thread: address.ConversationId, text: "not my selection", sender: "UOTHER"));
        await f.DispatchAsync(); Assert.All(await f.QuestionsAsync(), q => Assert.Null(q.Answer));
        await f.ReceiveAsync(SlackProviderFixture.Event("selected-answer", thread: address.ConversationId, text: "chosen custom answer")); await f.DispatchAsync();
        Assert.Equal("chosen custom answer", (await f.QuestionsAsync()).Single(q => q.ExternalId == "q2").Answer); Assert.Null((await f.QuestionsAsync()).Single(q => q.ExternalId == "q1").Answer);
    }

    [Fact]
    public async Task Slack_and_Telegram_answers_share_one_domain_receipt_and_retire_all_copies()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1, telegram: true, secondWorkspace: true);
        await f.EnqueueAsync(); await f.DeliverAsync(); var address = await f.AddressAsync(); var slack = await f.MessageAsync(); var telegram = await f.MessageAsync(connection: "bot-one");
        var telegramToken = f.Telegram.Calls.First(c => c.Method == "sendMessage").Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[1][0].GetProperty("callback_data").GetString()!;
        await f.StartAsync();
        f.Telegram.Updates.Enqueue(ChatTelegramRuntimeTestFactory.Callback(100, telegramToken, telegram.MessageId!));
        await f.ReceiveAsync(SlackProviderFixture.Button("choice", slack.MessageId!, f.Button(), thread: address.ConversationId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while ((await f.InboxAsync()).Count < 2) await Task.Delay(10, timeout.Token);
        await Task.WhenAll(f.DispatchAsync(), f.DispatchAsync());
        Assert.Contains(Assert.Single(await f.QuestionsAsync()).Answer, new[] { "First", "Second" });
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync(r => r.Action == "question.answer")));
        // New envelopes carrying the same logical action cannot create another answer.
        await f.ReceiveAsync(SlackProviderFixture.Button("choice-again", slack.MessageId!, f.Button(), thread: address.ConversationId)); await f.DispatchAsync(); await f.DeliverAsync();
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync(r => r.Action == "question.answer")));
        Assert.All(await f.MessagesAsync(), m => Assert.True(m.ControlsRetired));
        Assert.Equal(2, f.Slack.Calls.Count(c => c.Method == "chat.update")); Assert.Contains(f.Telegram.Calls, c => c.Method == "editMessageText");
    }

    [Fact]
    public async Task Child_notifications_reuse_one_parent_thread_and_split_receipts_correlate_to_the_same_question()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"); var question = await db.DeliverableQuestions.SingleAsync(); question.Text = new string('x', 5000);
            db.DeliverableQuestions.Add(new() { WorkItemId = "child-1", ExternalId = "q2", Iter = 1, Text = "Another child" }); await db.SaveChangesAsync();
        });
        await f.EnqueueAsync();
        // Producers intentionally use a 2,000-character summary. Exercise the durable correlation seam with a larger safe payload.
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var notification = await db.ChatNotifications.SingleAsync(n => n.Platform == "plugins");
            var payload = ChatPayloadConverter.Read(notification)!;
            notification.PayloadJson = JsonSerializer.Serialize(payload with { Questions = payload.Questions.Select(q => q with { Text = new string('x', 5000) }).ToArray() });
            await db.SaveChangesAsync();
        });
        await f.DeliverAsync(); await f.EnqueueAsync("child-two", "child-1"); await f.DeliverAsync();
        var messages = await f.MessagesAsync(); Assert.Equal(3, messages.Count); Assert.Equal(2, messages.Count(m => m.QuestionId == "q1"));
        Assert.Single(messages.Select(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson)!.ConversationId).Distinct());
        Assert.Equal(1, f.Slack.Calls.Count(c => c.Method == "chat.postMessage" && !c.Body.TryGetProperty("thread_ts", out _)));
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.CountAsync()));
    }

    [Theory]
    [InlineData("not_in_channel", "slack-membership-required")]
    [InlineData("is_archived", "slack-channel-archived")]
    [InlineData("token_revoked", "slack-bot-token-revoked")]
    public async Task Repairable_failures_are_connection_local_and_host_retry_recovers_without_recreating_a_known_root(string error, string reason)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1, telegram: true, secondWorkspace: true);
        await f.EnqueueAsync();
        // Fail a question after its root is known, so repair must reuse that binding.
        f.Slack.Override = (workspace, method, body, ct) => Task.FromResult<HttpResponseMessage?>(workspace == "T1" && method == "chat.postMessage" && body.TryGetProperty("thread_ts", out _)
            ? SlackProviderFixture.Error(error) : null);
        await f.DeliverAsync();
        var failed = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.AsNoTracking().SingleAsync(d => d.ConnectionId == "slack-one"));
        Assert.Equal("failed", failed.State); Assert.Equal(reason, failed.LastError);
        Assert.Equal(2, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.CountAsync(d => d.State == "sent")));
        f.Slack.Override = null;
        await f.WithAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            await sp.GetRequiredService<ChatStatusService>().RetryAsync(actor, failed.Id, default);
        });
        await f.DeliverAsync();
        Assert.Equal(3, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatDeliveries.CountAsync(d => d.State == "sent")));
        Assert.Equal(1, f.Slack.Calls.Count(c => c.Workspace == "T1" && c.Method == "chat.postMessage" && !c.Body.TryGetProperty("thread_ts", out _)));
    }

    [Fact]
    public async Task Channel_link_challenge_is_sender_workspace_bound_hashed_and_redeemable_without_DM_scopes()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1, linked: false);
        var challenge = await f.ReadAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            await Assert.ThrowsAsync<ChatConfigurationException>(() => sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "slack-one", "TOTHER", "U1", default));
            return JsonSerializer.SerializeToElement(await sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "slack-one", "T1", "U1", default));
        });
        var token = challenge.GetProperty("Token").GetString()!; Assert.Equal(JsonValueKind.Null, challenge.GetProperty("DeepLink").ValueKind);
        Assert.Equal("!forge link " + token, challenge.GetProperty("ChannelCommand").GetString());
        await f.ReceiveAsync(SlackProviderFixture.Event("wrong-sender", thread: null, text: "!forge link " + token, sender: "UOTHER")); await f.DispatchAsync();
        Assert.Null(f.Services.GetRequiredService<HostIdentityStore>().FindLink("slack", "T1", "U1"));
        await f.ReceiveAsync(SlackProviderFixture.Event("link", thread: null, text: "!forge link " + token)); await f.DispatchAsync(); await f.RespondAsync();
        Assert.NotNull(f.Services.GetRequiredService<HostIdentityStore>().FindLink("slack", "T1", "U1"));
        Assert.All(await f.InboxAsync(), row => Assert.DoesNotContain(token, row.EnvelopeJson));
        Assert.Contains(f.Slack.Calls, c => c.Method == "chat.postMessage" && c.Body.GetProperty("channel").GetString() == "C1");
        await f.ReceiveAsync(SlackProviderFixture.Event("unlink", thread: null, text: "!forge unlink")); await f.DispatchAsync();
        Assert.Null(f.Services.GetRequiredService<HostIdentityStore>().FindLink("slack", "T1", "U1"));
    }

    [Fact]
    public async Task Durable_receipt_is_committed_before_socket_ack_and_duplicate_envelopes_do_not_dispatch_twice()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.DeliverAsync(); await f.StartAsync();
        var message = await f.MessageAsync(); var address = await f.AddressAsync(); var socket = Assert.Single(f.Slack.Sockets);
        socket.BeforeAck = async (_, _) => Assert.Single(await f.InboxAsync());
        var frame = SlackProviderFixture.Button("durable", message.MessageId!, f.Button(), thread: address.ConversationId);
        await f.ReceiveAsync(frame); await f.ReceiveAsync(frame); Assert.Single(await f.InboxAsync()); await f.DispatchAsync();
        Assert.Equal("First", Assert.Single(await f.QuestionsAsync()).Answer);
    }

    [Fact]
    public async Task Migration105_is_control_only_idempotent_and_upgrades_existing_receiver_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(connection).Options);
        await using var product = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(connection).Options);
        await control.Database.ExecuteSqlRawAsync("CREATE TABLE chat_receiver_state (ProviderId TEXT, ReceptionKey TEXT); INSERT INTO chat_receiver_state VALUES ('slack','A1'); CREATE TABLE chat_conversation_bindings (Id TEXT); INSERT INTO chat_conversation_bindings VALUES ('existing');");
        await Phase105Migration.ApplyAsync(control, product); await Phase105Migration.ApplyAsync(control, product);
        Assert.Equal(0, await control.Database.SqlQueryRaw<long>("SELECT DiagnosticCount AS Value FROM chat_receiver_state").SingleAsync());
        Assert.Equal("active", await control.Database.SqlQueryRaw<string>("SELECT RootRenderedState AS Value FROM chat_conversation_bindings").SingleAsync());
        Assert.Equal(PhaseMigrationKind.Control, PhaseMigrationCatalog.ByPhase[105].Kind); Assert.Contains(105, PhaseMigrationRunner.ExecutionOrder);
        Assert.Equal(PartitionStrategy.Global, DbBoundary.TryGet("chat_receiver_state")!.Strategy);
        Assert.DoesNotContain(product.Model.GetEntityTypes(), e => e.GetTableName() == "chat_receiver_state");
    }

    [Fact]
    public async Task Several_custom_selections_require_explicit_selection_and_copied_buttons_cannot_change_the_address()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.DeliverAsync();
        var address = await f.AddressAsync(); var one = await f.MessageAsync(); var two = await f.MessageAsync("q2");
        await f.ReceiveAsync(SlackProviderFixture.Button("copied", one.MessageId!, f.Button(), thread: address.ConversationId, channel: "COTHER"));
        await f.DispatchAsync(); Assert.All(await f.QuestionsAsync(), q => Assert.Null(q.Answer));
        await f.ReceiveAsync(SlackProviderFixture.Button("custom-one", one.MessageId!, f.Button(index: 2), thread: address.ConversationId));
        await f.ReceiveAsync(SlackProviderFixture.Button("custom-two", two.MessageId!, f.Button("q2", 2), thread: address.ConversationId));
        await f.DispatchAsync(); await f.DeliverAsync();
        await f.ReceiveAsync(SlackProviderFixture.Event("ambiguous-custom", thread: address.ConversationId, text: "This matches neither uniquely")); await f.DispatchAsync();
        Assert.Equal("select-one-question", (await f.InboxAsync()).Single(i => i.DeliveryKey == "event:ambiguous-custom").Result);
        Assert.All(await f.QuestionsAsync(), q => Assert.Null(q.Answer));
    }

    [Fact]
    public async Task Unknown_root_creation_is_preserved_without_automatic_recreation()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync();
        f.Slack.Override = (_, method, _, _) => method == "chat.postMessage" ? throw new HttpRequestException("fixture lost response") : Task.FromResult<HttpResponseMessage?>(null);
        await f.DeliverAsync(); f.Slack.Override = null; f.Clock.Advance(TimeSpan.FromMinutes(2)); await f.DeliverAsync();
        Assert.Single(f.Slack.Calls.Where(c => c.Method == "chat.postMessage"));
        Assert.Equal("uncertain", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.ProvisioningState).SingleAsync()));
    }

    [Fact]
    public async Task Slack_credentials_are_redacted_even_when_not_the_current_connections_secret()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.DeliverAsync();
        var address = await f.AddressAsync();
        foreach (var (id, text) in new[] { ("app", "xapp-AOTHER-privatecredential"), ("bot", "xoxb-TOTHER-privatecredential") })
        {
            await f.ReceiveAsync(SlackProviderFixture.Event(id, thread: address.ConversationId, text: text)); await f.DispatchAsync();
            var row = (await f.InboxAsync()).Single(i => i.DeliveryKey == "event:" + id);
            Assert.DoesNotContain(text, row.EnvelopeJson); Assert.Equal("safe-replacement-required", row.Result);
        }
        Assert.Null(Assert.Single(await f.QuestionsAsync()).Answer);
    }

    [Fact]
    public async Task Parent_completion_and_reopening_update_the_same_root_and_retirement_edits_it_without_closing_a_channel()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.DeliverAsync();
        var root = (await f.AddressAsync()).ConversationId;
        foreach (var (phase, expected) in new[] { ("done", "completed"), ("implement", "active") })
        {
            await f.WithAsync(async sp =>
            {
                var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"); var parent = await db.StorySpawns.SingleAsync(s => s.Id == "shared");
                parent.Phase = phase; await db.SaveChangesAsync(); await sp.GetRequiredService<ChatScopeReconciler>().RunOnceAsync();
            });
            await f.TickAsync();
            Assert.Equal(expected, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.RootRenderedState).SingleAsync()));
            Assert.Equal(root, f.Slack.Calls.Last(c => c.Method == "chat.update").Body.GetProperty("ts").GetString());
        }
        await f.RunAsync("owner-p", async (s, a) =>
        {
            var parent = await s.GetPluginAsync(a, "p", "shared"); var sub = Assert.Single(parent.Subscriptions);
            await s.DeleteSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), sub.Id, sub.Revision, parent.Revision); return true;
        });
        await f.DeliverAsync();
        Assert.Equal("local", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.RetirementState).SingleAsync()));
        Assert.Equal(root, f.Slack.Calls.Last(c => c.Method == "chat.update").Body.GetProperty("ts").GetString());
        Assert.Equal(1, f.Slack.Calls.Count(c => c.Method == "chat.postMessage" && !c.Body.TryGetProperty("thread_ts", out _)));
    }

    [Fact]
    public async Task Inbox_storage_failure_withholds_ack_until_the_actual_receipt_commits()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.DeliverAsync(); await f.StartAsync();
        var socket = Assert.Single(f.Slack.Sockets); f.Fault.Armed = "inbox";
        var frame = SlackProviderFixture.Event("storage", thread: (await f.AddressAsync()).ConversationId);
        socket.Push(frame);
        await SlackProviderFixture.UntilAsync(() => f.Services.GetRequiredService<ChatRuntimeState>().Health.TryGetValue("slack-one", out var health) && health.Reason == "inbox-storage-unavailable");
        Assert.Empty(socket.Acknowledged); Assert.Empty(await f.InboxAsync());
        socket.Push(frame); await SlackProviderFixture.UntilAsync(() => socket.Acknowledged.Count == 1);
        Assert.Single(await f.InboxAsync());
    }
}