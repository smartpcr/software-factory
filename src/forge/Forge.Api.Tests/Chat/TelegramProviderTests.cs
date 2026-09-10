// -----------------------------------------------------------------------
// <copyright file="TelegramProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Common.Chat.Telegram;
using Xunit;

public sealed class TelegramProviderTests
{
    [Fact]
    public async Task Zero_update_cursor_is_acknowledged_and_dispose_stops_poll_before_releasing_bot()
    {
        using var f = new TelegramProviderFixture(); var s = await f.OpenAsync(); var sink = new TelegramBatchSink();
        f.Updates.Enqueue(TelegramProviderFixture.Message(0)); await s.PollOnceAsync(sink); await s.PollOnceAsync(sink);
        Assert.Equal(new long[] { 0, 1 }, f.Calls.Where(c => c.Method == "getUpdates").Select(c => c.Body.GetProperty("offset").GetInt64()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Override = async (_, method, _, ct) => { if (method == "getUpdates") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return null; };
        var receiving = s.ReceiveAsync(sink, CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await s.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        f.Override = null; await using var replacement = await f.OpenAsync(); Assert.NotNull(replacement);
    }

    [Fact]
    public async Task Serialized_payload_and_unicode_label_limits_fail_before_HTTP()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync();
        var tooLong = TelegramProviderFixture.Notification() with { Actions = [new(new string('界', 257), TelegramProviderFixture.Key, "question.answer")] };
        Assert.Equal("render-payload-limit:button-label:256", (await s.SendAsync(TelegramProviderFixture.Address(), tooLong)).Failure!.Code);
        var payload = TelegramProviderFixture.Notification(new string('界', 3900)) with { Actions = Enumerable.Range(0, 20).Select(_ => new ChatSemanticAction(new string('界', 256), TelegramProviderFixture.Key, "question.answer")).ToArray() };
        Assert.Equal("render-payload-limit:payload-bytes:32768", (await s.SendAsync(TelegramProviderFixture.Address(), payload)).Failure!.Code);
        Assert.DoesNotContain(f.Calls, c => c.Method == "sendMessage");
    }

    [Fact]
    public async Task Independent_bots_restore_cursors_and_discovery_uses_only_existing_receiver()
    {
        using var f = new TelegramProviderFixture();
        await using var one = await f.OpenAsync(); await using var two = await f.OpenAsync("bot-two", "67890");
        await one.RestoreCursorAsync("10"); await two.RestoreCursorAsync("200");
        var sink = new TelegramBatchSink(); f.Updates.Enqueue(TelegramProviderFixture.Message(11));
        Assert.True((await one.PollOnceAsync(sink)).Succeeded); await two.PollOnceAsync(sink);
        var polls = f.Calls.Where(c => c.Method == "getUpdates").ToArray();
        Assert.Equal(11, polls[0].Body.GetProperty("offset").GetInt64()); Assert.Equal(201, polls[1].Body.GetProperty("offset").GetInt64());
        Assert.Equal("bot-one", sink.Cursors.Single().ConnectionId);
        Assert.Single((await one.DiscoverAsync(null)).Value!.Items); Assert.Empty((await two.DiscoverAsync(null)).Value!.Items);
        Assert.Equal(2, f.Calls.Count(c => c.Method == "getUpdates"));
        Assert.Equal("duplicate-telegram-bot", (await f.Provider.OpenSessionAsync(f.Configuration("duplicate"), f)).Failure!.Code);
        await one.DisposeAsync(); await using var replacement = await f.OpenAsync();
        Assert.NotNull(replacement);
    }

    [Fact]
    public async Task Inbox_retry_keeps_offset_and_callback_is_acknowledged_only_after_commit()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync(); await s.RestoreCursorAsync("9");
        var sink = new TelegramBatchSink { Result = ChatInboundAcceptance.Retry }; f.Updates.Enqueue(TelegramProviderFixture.Callback(10));
        Assert.False((await s.PollOnceAsync(sink)).Succeeded); Assert.DoesNotContain(f.Calls, c => c.Method == "answerCallbackQuery");
        sink.Result = ChatInboundAcceptance.Accepted; f.Updates.Enqueue(TelegramProviderFixture.Callback(10));
        Assert.True((await s.PollOnceAsync(sink)).Succeeded); await s.PollOnceAsync(sink);
        Assert.Equal(new long[] { 10, 10, 11 }, f.Calls.Where(c => c.Method == "getUpdates").Select(c => c.Body.GetProperty("offset").GetInt64()));
        var ack = Assert.Single(f.Calls, c => c.Method == "answerCallbackQuery");
        Assert.Equal("callback-10", ack.Body.GetProperty("callback_query_id").GetString());
        Assert.All(sink.Envelopes, e => { Assert.Equal("callback-10", e.CallbackQueryId); Assert.Equal("101", e.ReplyToMessageId); Assert.Equal("7", e.Address.ConversationId); });
    }

    [Fact]
    public async Task Commands_addressed_to_another_bot_and_attachments_are_normalized_safely()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync(); var sink = new TelegramBatchSink();
        foreach (var update in new[] { TelegramProviderFixture.Message(1, commandBot: "forge_test_bot"), TelegramProviderFixture.Message(2, commandBot: "other_bot"),
            TelegramProviderFixture.Message(3, text: null), TelegramProviderFixture.Message(4, text: "/answer@forge_test_bot 123 safe choice"),
            TelegramProviderFixture.Message(5, text: "/start ABC") })
        { f.Updates.Enqueue(update); await s.PollOnceAsync(sink); }
        Assert.Equal(new[] { "command", "ignored", "unsupported", "command", "command" }, sink.Envelopes.Select(e => e.Kind));
        Assert.Equal("status", sink.Envelopes[0].Command); Assert.Equal("123", sink.Envelopes[3].CommandArgument);
        Assert.Equal("safe choice", sink.Envelopes[3].Text); Assert.Equal("ABC", sink.Envelopes[4].CommandArgument);
    }

    [Theory]
    [InlineData(401, ChatFailureKind.Unauthorized)]
    [InlineData(403, ChatFailureKind.Unauthorized)]
    [InlineData(409, ChatFailureKind.InvalidConfiguration)]
    [InlineData(429, ChatFailureKind.RateLimited)]
    [InlineData(503, ChatFailureKind.Unavailable)]
    public async Task Safe_failure_classification_and_retry_hints(int status, ChatFailureKind kind)
    {
        using var f = new TelegramProviderFixture();
        f.Override = (_, _, _, _) => Task.FromResult<HttpResponseMessage?>(TelegramProviderFixture.Error(status, "SECRET URL token=unsafe", 9));
        var result = await f.Provider.OpenSessionAsync(f.Configuration(), f);
        Assert.Equal(kind, result.Failure!.Kind); Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(9), result.Failure.RetryAfter);
    }

    [Fact]
    public async Task Webhook_conflict_is_visible_and_never_deleted()
    {
        using var f = new TelegramProviderFixture();
        f.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(method == "getWebhookInfo" ? TelegramProviderFixture.Ok(new { url = "https://example.test/owned" }) : null);
        Assert.Equal("telegram-webhook-conflict", (await f.Provider.OpenSessionAsync(f.Configuration(), f)).Failure!.Code);
        Assert.DoesNotContain(f.Calls, c => c.Method is "deleteWebhook" or "getUpdates");
    }

    [Theory]
    [InlineData("member", false)]
    [InlineData("left", true)]
    public async Task Membership_and_topic_admin_permissions_are_checked(string membership, bool deniedDestination)
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync();
        f.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(method == "getChatMember" ? TelegramProviderFixture.Ok(new { status = membership, can_manage_topics = false }) : null);
        var destination = await s.ValidateDestinationAsync(TelegramProviderFixture.Address(topic: null));
        Assert.Equal(!deniedDestination, destination.Succeeded);
        if (!deniedDestination) Assert.Equal(ChatFailureKind.Unauthorized, (await s.EnsureConversationAsync(destination.Value!, ChatConversationPolicy.Topic, "parent", "Forge")).Failure!.Kind);
        Assert.DoesNotContain(f.Calls, c => c.Method == "createForumTopic");
    }

    [Fact]
    public async Task Topic_flat_DM_sends_edits_and_retirement_keep_qualified_addresses()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync(); var flat = TelegramProviderFixture.Address(topic: null);
        var destination = (await s.ValidateDestinationAsync(flat)).Value!;
        var topic = (await s.EnsureConversationAsync(destination, ChatConversationPolicy.Topic, "one-parent", "Forge")).Value!;
        Assert.Equal("7", topic.Address.ConversationId);
        var sent = (await s.SendAsync(topic.Address, TelegramProviderFixture.Notification())).Value!;
        Assert.Equal(topic.Address, sent.Messages.Single().Address);
        Assert.True((await s.EditAsync(sent.Messages.Single(), TelegramProviderFixture.Notification() with { Actions = [] })).Succeeded);
        Assert.True((await s.RetireConversationAsync(topic)).Succeeded);
        var dm = (await s.ValidateDestinationAsync(flat with { ChannelId = "55" })).Value!;
        Assert.Equal("private", dm.Kind); Assert.False((await s.EnsureConversationAsync(dm, ChatConversationPolicy.Topic, "dm", "Forge")).Succeeded);
        Assert.Null((await s.EnsureConversationAsync(dm, ChatConversationPolicy.Flat, "dm", "Forge")).Value!.Address.ConversationId);
        Assert.False((await s.SendAsync(flat with { ConnectionId = "wrong" }, TelegramProviderFixture.Notification())).Succeeded);
        Assert.Equal("7", f.Calls.Single(c => c.Method == "sendMessage").Body.GetProperty("message_thread_id").GetString());
    }

    [Fact]
    public async Task Preflight_rejects_UTF8_callback_and_key_limits_without_sending()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync();
        foreach (var (token, field) in new[] { (new string('界', 22), "callback_data:64"), (new string('a', 25), "action-key:25"), ("a1_short", "action-key:25"), (TelegramProviderFixture.Key + "\n", "action-key:25") })
        {
            var result = await s.SendAsync(TelegramProviderFixture.Address(), TelegramProviderFixture.Notification() with { Actions = [new("Answer", token, "question.answer")] });
            Assert.Equal("render-payload-limit:" + field, result.Failure!.Code);
        }
        Assert.True(TelegramRendering.IsActionKey(TelegramProviderFixture.Key)); Assert.Equal(25, Encoding.UTF8.GetByteCount(TelegramProviderFixture.Key));
        Assert.DoesNotContain(f.Calls, c => c.Method == "sendMessage");
    }

    [Fact]
    public async Task Unicode_split_preserves_all_text_and_puts_buttons_on_last_message()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync();
        var text = new string('x', 4088) + "🙂" + new string('界', 4500);
        var value = TelegramProviderFixture.Notification(text);
        var result = await s.SendAsync(TelegramProviderFixture.Address(), value);
        Assert.True(result.Succeeded); Assert.Equal(3, result.Value!.Messages.Count);
        var sends = f.Calls.Where(c => c.Method == "sendMessage").ToArray();
        Assert.Equal("Forge\n\n" + text, string.Concat(sends.Select(c => c.Body.GetProperty("text").GetString())));
        Assert.All(sends, c => Assert.True(c.Body.GetProperty("text").GetString()!.Length <= 4096));
        Assert.Empty(sends[0].Body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
        Assert.Single(sends[^1].Body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
    }

    [Fact]
    public async Task Partial_send_retains_known_references_and_unknown_creation_is_not_retried()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync(); var count = 0;
        f.Override = (_, method, _, _) => method == "sendMessage" && ++count == 2 ? throw new HttpRequestException("redacted") : Task.FromResult<HttpResponseMessage?>(null);
        var sent = await s.SendAsync(TelegramProviderFixture.Address(), TelegramProviderFixture.Notification(new string('x', 6000)));
        Assert.Equal(ChatFailureKind.UnknownOutcome, sent.Failure!.Kind); Assert.Single(sent.Value!.Messages);
        f.Override = (_, method, _, _) => method == "createForumTopic" ? throw new HttpRequestException("redacted") : Task.FromResult<HttpResponseMessage?>(null);
        var topic = await s.EnsureConversationAsync(new(TelegramProviderFixture.Address(topic: null), "Forum", "forum"), ChatConversationPolicy.Topic, "claim", "title");
        Assert.Equal(ChatFailureKind.UnknownOutcome, topic.Failure!.Kind);
        Assert.Single(f.Calls, c => c.Method == "createForumTopic");
    }

    [Fact]
    public async Task Duplicate_poll_is_rejected_and_cancellation_releases_receiver()
    {
        using var f = new TelegramProviderFixture(); await using var s = await f.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Override = async (_, method, _, ct) => { if (method == "getUpdates") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return null; };
        using var cancel = new CancellationTokenSource();
        var receiving = s.ReceiveAsync(new TelegramBatchSink(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("duplicate-telegram-receiver", (await s.PollOnceAsync(new TelegramBatchSink())).Failure!.Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving);
        f.Override = null; Assert.True((await s.PollOnceAsync(new TelegramBatchSink())).Succeeded);
    }

    [Theory]
    [InlineData("{\"longPollSeconds\":\"bad\"}")]
    [InlineData("{\"longPollSeconds\":0}")]
    [InlineData("{\"unknown\":true}")]
    public void Settings_validation_fails_closed(string json)
    {
        using var f = new TelegramProviderFixture(); Assert.False(f.Provider.ValidateSettings(1, JsonSerializer.Deserialize<JsonElement>(json)).Valid);
    }
}