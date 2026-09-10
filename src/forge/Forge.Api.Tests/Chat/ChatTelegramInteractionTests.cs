// -----------------------------------------------------------------------
// <copyright file="ChatTelegramInteractionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net.Http;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatTelegramInteractionTests
{
    [Fact]
    public async Task Configured_flat_chat_supports_scoped_status_before_first_event()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(policy: "flat");
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "/status p:shared", topic: null)); await f.DispatchAsync(); await f.RespondAsync();
        Assert.Equal("status", (await f.InboxAsync()).Single().Result);
        Assert.DoesNotContain(f.Http.Calls, c => c.Method == "createForumTopic");
        Assert.False(f.Http.Calls.Single(c => c.Method == "sendMessage").Body.TryGetProperty("message_thread_id", out _));
        await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/status q:shared", topic: null)); await f.DispatchAsync();
        Assert.Equal("scope-off-paused-or-denied", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
    }

    [Fact]
    public async Task Telegram_only_registry_delivers_choices_and_partial_answer_keeps_other_controls_in_topic()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync();
        Assert.Single(f.Services.GetRequiredService<IChatProviderRegistry>().Providers);
        Assert.True(await f.EnqueueAsync()); await f.TickAsync(); await f.TickAsync();
        var messages = (await f.MessagesAsync()).OrderBy(m => m.QuestionId).ToArray();
        Assert.Equal(2, messages.Length);
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, f.ButtonFor("q1", 0), messages[0].MessageId!));
        await f.DispatchAsync(); await f.RespondAsync(); await f.TickAsync();
        Assert.Equal("answered-more-remain", (await f.InboxAsync()).Single().Result);
        var remaining = await f.MessagesAsync();
        Assert.True(remaining.Single(m => m.QuestionId == "q1").ControlsRetired); Assert.False(remaining.Single(m => m.QuestionId == "q2").ControlsRetired);
        Assert.Equal("First", await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.Where(q => q.ExternalId == "q1").Select(q => q.Answer).SingleAsync()));
        Assert.All(f.Http.Calls.Where(c => c.Method == "sendMessage"), call => Assert.Equal("7", call.Body.GetProperty("message_thread_id").GetString()));
        Assert.Equal("sent", (await f.InboxAsync()).Single().ResponseState);
    }

    [Fact]
    public async Task Custom_prompt_is_question_and_actor_bound_and_retains_other_question()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync(); await f.TickAsync();
        var q2 = (await f.MessagesAsync()).Single(m => m.QuestionId == "q2");
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, f.ButtonFor("q2", 2), q2.MessageId!));
        await f.DispatchAsync(); await f.TickAsync();
        var prompt = (await f.MessagesAsync()).Single(m => m.ActionKind == "question.prompt");
        Assert.Equal("q2", prompt.QuestionId); Assert.Equal("owner-p", prompt.ConfirmationActorId);
        Assert.Contains(f.Http.Calls, c => c.Method == "sendMessage" && c.Body.GetProperty("reply_markup").TryGetProperty("force_reply", out var force) && force.GetBoolean());
        var identities = f.Services.GetRequiredService<HostIdentityStore>();
        await f.EnrollAsync("second-operator", productId: "p");
        identities.RedeemLinkChallenge(identities.IssueLinkChallenge("second-operator", "telegram", "12345", "43"), "telegram", "12345", "43");
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Reply(2, prompt.MessageId!, "wrong actor", "43")); await f.DispatchAsync();
        Assert.Equal("confirmation-actor-mismatch", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Reply(3, prompt.MessageId!, "my custom answer")); await f.DispatchAsync(); await f.TickAsync();
        Assert.Equal("answered-more-remain", (await f.InboxAsync()).Single(i => i.DeliveryKey == "3").Result);
        Assert.False((await f.MessagesAsync()).Single(m => m.QuestionId == "q1").ControlsRetired);
    }

    [Fact]
    public async Task Ambiguous_text_lists_questions_and_explicit_answer_selects_exact_message()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync(); await f.TickAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "choose a value")); await f.DispatchAsync();
        Assert.Equal("select-one-question", (await f.InboxAsync()).Single().Result);
        var q2 = (await f.MessagesAsync()).Single(m => m.QuestionId == "q2");
        await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/answer@forge_test_bot " + q2.MessageId + " selected second question")); await f.DispatchAsync();
        Assert.Equal("answered-more-remain", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.Where(q => q.ExternalId == "q1").Select(q => q.Answer).SingleAsync()));
    }

    [Fact]
    public async Task Callback_cannot_be_replayed_on_another_message_or_topic()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        var q = (await f.MessagesAsync()).Single(m => m.State == "sent"); var button = f.Button(0, 0);
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, button, "999")); await f.DispatchAsync();
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(2, button, q.MessageId!, topic: "8")); await f.DispatchAsync();
        Assert.All(await f.InboxAsync(), i => Assert.Equal("unmatched-message", i.Result));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("off")]
    [InlineData("notifications")]
    public async Task Off_paused_and_notifications_only_scopes_deny_domain_actions(string mode)
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        var q = (await f.MessagesAsync()).Single(m => m.State == "sent"); var button = f.Button(0, 0);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var sub = await db.ChatSubscriptions.SingleAsync();
            if (mode == "pause") sub.Paused = true; else if (mode == "notifications") sub.Interactive = false;
            else (await db.SpawnPlugins.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        });
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, button, q.MessageId!)); await f.DispatchAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/pause")); await f.DispatchAsync();
        Assert.All(await f.InboxAsync(), i => Assert.DoesNotContain(i.Result, new[] { "accepted", "answered-all", "answered-more-remain", "parent-paused" }));
        Assert.Equal("implement", await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.Select(s => s.Phase).SingleAsync()));
    }

    [Fact]
    public async Task Status_and_pause_resume_use_parent_domain_core_and_leave_subscription_enabled()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "/status@forge_test_bot")); await f.DispatchAsync();
        var status = (await f.InboxAsync()).Single(); Assert.Equal("status", status.Result); Assert.Contains("Pending questions: 2", status.ResponseText);
        await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/pause")); await f.DispatchAsync();
        Assert.Equal("parent-paused", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
        Assert.Equal("paused", await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.Select(s => s.Phase).SingleAsync()));
        Assert.False(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatSubscriptions.Select(s => s.Paused).SingleAsync()));
        await f.ReceiveAsync(TelegramProviderFixture.Message(3, "/resume")); await f.DispatchAsync();
        Assert.Equal("parent-resumed", (await f.InboxAsync()).Single(i => i.DeliveryKey == "3").Result);
        Assert.Equal("implement", await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.Select(s => s.Phase).SingleAsync()));
    }

    [Fact]
    public async Task Link_start_is_hashed_preserves_other_challenges_and_relink_revocation_is_explicit()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(linked: false);
        async Task<string> Challenge(string sender) => await f.ReadAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p");
            var result = JsonSerializer.SerializeToElement(await sp.GetRequiredService<ChatLinkService>().BeginAsync(actor, "bot-one", "12345", sender, CancellationToken.None));
            Assert.StartsWith("https://t.me/forge_test_bot?start=", result.GetProperty("DeepLink").GetString());
            return result.GetProperty("Token").GetString()!;
        });
        var first = await Challenge("42"); var other = await Challenge("43");
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, "/start " + first)); await f.DispatchAsync();
        Assert.Equal("linked", (await f.InboxAsync()).Single().Result); Assert.DoesNotContain(first, (await f.InboxAsync()).Single().EnvelopeJson);
        Assert.NotNull(f.Services.GetRequiredService<HostIdentityStore>().RedeemLinkChallenge(other, "telegram", "12345", "43"));
        var relink = await Challenge("42"); await f.ReceiveAsync(TelegramProviderFixture.Message(2, "/start " + relink)); await f.DispatchAsync();
        Assert.Equal("linked", (await f.InboxAsync()).Single(i => i.DeliveryKey == "2").Result);
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatOperatorLinks.CountAsync(l => l.ExternalUserId == "42" && l.RevokedAt == null)));
        await f.ReceiveAsync(TelegramProviderFixture.Message(3, "/unlink")); await f.DispatchAsync();
        Assert.Null(f.Services.GetRequiredService<HostIdentityStore>().FindLink("telegram", "12345", "42"));
        await f.ReceiveAsync(TelegramProviderFixture.Message(4, "/start " + relink)); await f.DispatchAsync();
        Assert.Equal("link-invalid-or-expired", (await f.InboxAsync()).Single(i => i.DeliveryKey == "4").Result);
    }

    [Fact]
    public async Task Unsupported_and_sensitive_input_get_safe_feedback_only_in_bound_conversation()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        await f.ReceiveAsync(TelegramProviderFixture.Message(1, text: null)); await f.ReceiveAsync(TelegramProviderFixture.Message(2, text: null, topic: 99));
        await f.ReceiveAsync(TelegramProviderFixture.Message(3, "password=DO_NOT_KEEP")); await f.DispatchAsync(); await f.RespondAsync();
        var inbox = await f.InboxAsync();
        Assert.Equal("sent", inbox.Single(i => i.DeliveryKey == "1").ResponseState);
        Assert.Equal("none", inbox.Single(i => i.DeliveryKey == "2").ResponseState);
        Assert.Equal("safe-replacement-required", inbox.Single(i => i.DeliveryKey == "3").Result);
        Assert.DoesNotContain("DO_NOT_KEEP", JsonSerializer.Serialize(inbox));
    }

    [Fact]
    public async Task Response_checkpoint_failure_retries_storage_only_and_never_redispatches_action()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync(); await f.TickAsync();
        var q = (await f.MessagesAsync()).Single(m => m.State == "sent");
        await f.ReceiveAsync(ChatTelegramRuntimeTestFactory.Callback(1, f.Button(0, 0), q.MessageId!)); await f.DispatchAsync();
        f.Fault.Armed = "response"; await Assert.ThrowsAsync<InvalidOperationException>(() => f.RespondAsync());
        var calls = f.Http.Calls.Count(c => c.Method == "sendMessage"); await f.RespondAsync(); await f.DispatchAsync();
        Assert.Equal(calls, f.Http.Calls.Count(c => c.Method == "sendMessage"));
        Assert.Equal("sent", (await f.InboxAsync()).Single().ResponseState);
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync(r => r.Action == "question.answer")));
    }

    [Fact]
    public async Task Parent_topic_is_reused_by_future_children_and_setting_changes_and_closed_on_removal()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(questions: 1); await f.EnqueueAsync(); await f.TickAsync();
        var parent = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        await f.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "subscription",
            new(1, parent.Revision, "destination", "topic", Interactive: true, ContentMode: "redacted", Paused: true)));
        parent = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        await f.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "subscription",
            new(2, parent.Revision, "destination", "topic", Interactive: true, ContentMode: "redacted")));
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"); db.DeliverableQuestions.Add(new() { WorkItemId = "child-1", ExternalId = "future", Text = "Future child question" }); await db.SaveChangesAsync(); });
        await f.EnqueueAsync("future", "child-1"); await f.TickAsync();
        Assert.Single(f.Http.Calls, c => c.Method == "createForumTopic");
        Assert.Single(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.ToListAsync()));
        parent = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        await f.RunAsync("owner-p", async (s, a) => { await s.DeleteSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "subscription", 3, parent.Revision); return true; });
        await f.TickAsync(); Assert.Single(f.Http.Calls, c => c.Method == "closeForumTopic");
        Assert.Equal("closed", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.RetirementState).SingleAsync()));
    }

    [Fact]
    public async Task Unknown_topic_creation_has_durable_recovery_record_and_no_blind_retry()
    {
        await using var f = new ChatTelegramRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        f.Http.Override = (_, method, _, _) => method == "createForumTopic" ? throw new HttpRequestException("provider uncertainty") : Task.FromResult<HttpResponseMessage?>(null);
        await f.TickAsync(); await f.TickAsync();
        Assert.Single(f.Http.Calls, c => c.Method == "createForumTopic");
        Assert.Equal("uncertain", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.Select(b => b.ProvisioningState).SingleAsync()));
    }
}