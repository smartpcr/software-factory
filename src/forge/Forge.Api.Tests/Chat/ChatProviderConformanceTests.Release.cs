// -----------------------------------------------------------------------
// <copyright file="ChatProviderConformanceTests.Release.cs" company="Microsoft Corp.">
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

public sealed partial class ChatProviderConformanceTests
{
    public static TheoryData<string> ReleaseProviders => new() { "FakeAlpha", "FakeBeta", "telegram", "slack" };

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_conversations_correlate_each_parent_and_retire_according_to_capabilities(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        var destination = (await f.Session.ValidateDestinationAsync(f.Address)).Value!;
        var policy = f.Capabilities.Topics ? ChatConversationPolicy.Topic : ChatConversationPolicy.Thread;
        f.DistinctTopics();
        var first = (await f.Session.EnsureConversationAsync(destination, policy, "parent-one", "One")).Value!;
        var second = (await f.Session.EnsureConversationAsync(destination, policy, "parent-two", "Two")).Value!;
        Assert.NotNull(first.Address.ConversationId);
        Assert.NotEqual(first.Address, second.Address);
        Assert.Equal(f.Address, first.Address with { ConversationId = null });
        var message = Assert.Single((await f.Session.SendAsync(first.Address, f.Notification)).Value!.Messages);
        Assert.Equal(first.Address, message.Address);
        Assert.True((await f.Session.EditAsync(message, f.Notification with { Body = "Resolved", Actions = [], Revision = 2 })).Succeeded);
        var before = f.RequestCount;
        Assert.False((await f.Session.SendAsync(first.Address with { ConnectionId = "foreign" }, f.Notification)).Succeeded);
        Assert.False((await f.Session.EditAsync(message with { Address = first.Address with { ConnectionId = "foreign" } }, f.Notification)).Succeeded);
        Assert.Equal(before, f.RequestCount);
        Assert.True((await f.Session.RetireConversationAsync(first)).Succeeded);
        Assert.Equal(f.Capabilities.Retirement == ChatConversationRetirement.RemoteClose, f.RemotelyClosed(first));
        if (f.Capabilities.Retirement == ChatConversationRetirement.LocalOnly)
            Assert.False((await f.Session.SendAsync(first.Address, f.Notification)).Succeeded);
        Assert.True((await f.Session.SendAsync(second.Address, f.Notification)).Succeeded);
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_discovery_drains_pages_without_losing_scope_or_starting_a_receiver(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        await f.SeedDiscoveryAsync();
        var destinations = new List<ChatDestination>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await f.Session.DiscoverAsync(cursor);
            if (!f.Capabilities.DestinationDiscovery)
            {
                Assert.Equal(ChatFailureKind.NotSupported, result.Failure!.Kind);
                return;
            }
            Assert.True(result.Succeeded);
            destinations.AddRange(result.Value!.Items);
            cursor = result.Value.NextCursor;
            if (cursor is not null) Assert.True(cursors.Add(cursor), "Discovery must advance its opaque cursor.");
            Assert.InRange(cursors.Count, 0, 4);
        } while (cursor is not null);
        Assert.NotEmpty(cursors);
        Assert.True(destinations.Count >= 2);
        Assert.Equal(destinations.Count, destinations.Select(d => d.Address).Distinct().Count());
        Assert.All(destinations, d =>
        {
            Assert.Equal(f.Address.ConnectionId, d.Address.ConnectionId);
            Assert.Equal(f.Address.ExternalTenantId, d.Address.ExternalTenantId);
            Assert.Null(d.Address.ConversationId);
        });
        Assert.DoesNotContain(f.Telegram.Calls, c => c.Method == "getUpdates");
        Assert.All(f.Slack.Sockets, socket => Assert.Empty(socket.Acknowledged));
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_rendering_preserves_unicode_and_enforces_wire_limits_before_sending(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        const string text = "<source>& café 🙂";
        Assert.True((await f.Session.SendAsync(f.Address, f.Notification with { Body = text, Actions = [] })).Succeeded);
        Assert.Equal(text, string.Concat(f.RenderedText()));
        f.AssertSafeWirePayloads();
        var before = f.SendCount;
        var token = new string('界', f.Capabilities.MaxActionBytes / 3 + 1);
        foreach (var invalid in new[]
        {
            f.Notification with { Actions = [new("Answer", token, "question.answer")] },
            f.Notification with { Body = new string('x', 70000) },
            f.PayloadByteOverflow(),
        }) Assert.Equal(ChatFailureKind.InvalidPayload, (await f.Session.SendAsync(f.Address, invalid)).Failure!.Kind);
        Assert.Equal(before, f.SendCount);
        var largeText = new string('x', f.Capabilities.MaxTextCharacters - 1) + "🙂" + new string('x', 20);
        var large = await f.Session.SendAsync(f.Address, f.Notification with { Body = largeText, Actions = [] });
        if (large.Succeeded)
        {
            Assert.True(large.Value!.Messages.Count > 1);
            Assert.Equal(largeText, string.Concat(f.RenderedText().Skip(before)));
            Assert.All(large.Value.Messages, message => Assert.Equal(f.Address, message.Address));
            f.AssertSafeWirePayloads();
        }
        else
        {
            Assert.Equal(ChatFailureKind.InvalidPayload, large.Failure!.Kind);
            Assert.Equal(before, f.SendCount);
        }
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_failures_are_classified_redacted_and_never_silently_retried(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        f.FailNext(ChatFailureKind.RateLimited);
        var limited = await f.Session.SendAsync(f.Address, f.Notification);
        Assert.Equal(ChatFailureKind.RateLimited, limited.Failure!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(9), limited.Failure.RetryAfter);
        Assert.Equal(1, f.SendCount);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(limited));
        f.FailNext(ChatFailureKind.Unavailable);
        var unavailable = await f.Session.ValidateDestinationAsync(f.Address);
        Assert.Equal(ChatFailureKind.Unavailable, unavailable.Failure!.Kind);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(unavailable));
        Assert.Equal(1, f.SendCount);
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_uncertain_send_keeps_an_unknown_outcome_without_a_second_attempt(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        f.FailNext(ChatFailureKind.UnknownOutcome);
        var result = await f.Session.SendAsync(f.Address, f.Notification);
        Assert.Equal(ChatFailureKind.UnknownOutcome, result.Failure!.Kind);
        Assert.Equal(1, f.SendCount);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_cancelled_operations_do_not_send_or_mutate_local_conversations(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        var destination = (await f.Session.ValidateDestinationAsync(f.Address)).Value!;
        var message = (await f.Session.SendAsync(f.Address, f.Notification)).Value!.Messages[0];
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var before = f.RequestCount;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.ValidateDestinationAsync(f.Address, cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.DiscoverAsync(null, cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.EnsureConversationAsync(destination, ChatConversationPolicy.Flat, "cancelled", "Title", cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.RetireConversationAsync(new(f.Address, null), cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.SendAsync(f.Address, f.Notification, cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.EditAsync(message, f.Notification, cancel.Token).AsTask());
        if (f.Session is IChatResumableSession resumable)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumable.RestoreCursorAsync("9", cancel.Token).AsTask());
        Assert.Equal(before, f.RequestCount);
        Assert.True((await f.Session.SendAsync(f.Address, f.Notification)).Succeeded);
    }

    [Theory]
    [MemberData(nameof(ReleaseProviders))]
    public async Task Release_inbound_identity_is_stable_and_acknowledgement_requires_durable_acceptance(string id)
    {
        await using var f = await ContractFixture.OpenAsync(id);
        var sink = new ContractSink(() => f.Acknowledgements);
        if (f.Session is TelegramSession telegram)
        {
            foreach (var unused in Enumerable.Range(0, 3))
            {
                f.Telegram.Updates.Enqueue(TelegramProviderFixture.Callback(10));
                await telegram.PollOnceAsync(sink);
            }
            Assert.Equal(new long[] { 0, 0, 11 }, f.Telegram.Calls.Where(c => c.Method == "getUpdates").Select(c => c.Body.GetProperty("offset").GetInt64()));
        }
        else if (id == "slack")
        {
            using var cancel = new CancellationTokenSource();
            var receiving = f.Session.ReceiveAsync(sink, cancel.Token);
            try
            {
                var socket = Assert.Single(f.Slack.Sockets);
                for (var i = 1; i <= 3; i++)
                {
                    socket.Push(SlackProviderFixture.Button("same-receipt", "1700000000.000200"));
                    await SlackProviderFixture.UntilAsync(() => sink.Envelopes.Count == i && f.Acknowledgements == Math.Max(0, i - 1));
                }
            }
            finally { cancel.Cancel(); await receiving.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        else await f.Session.ReceiveAsync(sink, CancellationToken.None);
        Assert.Equal(2, f.Acknowledgements);
        Assert.Equal(3, sink.Envelopes.Count);
        Assert.Single(sink.Envelopes.Select(e => e.DeliveryKey).Distinct());
        Assert.All(sink.Envelopes, envelope =>
        {
            Assert.Equal(id, envelope.ProviderId);
            Assert.Equal(f.Address.ConnectionId, envelope.ConnectionId);
            Assert.Equal(f.Address, envelope.Address with { ConversationId = null });
            Assert.False(string.IsNullOrWhiteSpace(envelope.SenderId));
            Assert.False(string.IsNullOrWhiteSpace(envelope.DeliveryKey));
        });
    }

    private sealed class ContractSink(Func<int> acknowledgements) : IChatDurableBatchSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<ChatInboundEnvelope> Envelopes { get; } = new();
        public ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(Math.Max(0, this.Envelopes.Count - 1), acknowledgements());
            this.Envelopes.Enqueue(envelope);
            return ValueTask.FromResult(this.Envelopes.Count switch { 1 => ChatInboundAcceptance.Retry, 2 => ChatInboundAcceptance.Accepted, _ => ChatInboundAcceptance.Duplicate });
        }
        public ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes, IReadOnlyList<ChatAcceptedCursor> cursors, CancellationToken ct = default)
        {
            Assert.Equal(Assert.Single(envelopes).ConnectionId, Assert.Single(cursors).ConnectionId);
            return this.AcceptAsync(envelopes[0], ct);
        }
    }

    private sealed class ContractFixture(string id) : IAsyncDisposable
    {
        public TelegramProviderFixture Telegram { get; } = new();
        public SlackProviderFixture Slack { get; } = new();
        public IChatConnectionSession Session { get; private set; } = null!;
        public ChatProviderCapabilities Capabilities { get; private set; } = null!;
        public ChatAddress Address => id == "telegram" ? TelegramProviderFixture.Address(topic: null) : id == "slack" ? SlackProviderFixture.Address(thread: null) : new("one", "tenant", "channel");
        public ChatSemanticNotification Notification => new("event", "open-questions", "", "Question", 1, [new("Answer", TelegramProviderFixture.Key, "question.answer")]);
        public int SendCount => id == "telegram" ? this.Telegram.Calls.Count(c => c.Method == "sendMessage") : id == "slack" ? this.Slack.Calls.Count(c => c.Method == "chat.postMessage") : ((FakeChatSession)this.Session).SendAttempts;
        public int RequestCount => this.Telegram.Calls.Count + this.Slack.Calls.Count + (this.Session is FakeChatSession fake ? fake.Rendered.Count : 0);
        public int Acknowledgements => id == "telegram" ? this.Telegram.Calls.Count(c => c.Method == "answerCallbackQuery") : id == "slack" ? this.Slack.Sockets.Sum(s => s.Acknowledged.Count) : ((FakeChatSession)this.Session).Acknowledged;
        public static async Task<ContractFixture> OpenAsync(string id)
        {
            var f = new ContractFixture(id);
            IChatProviderPlugin provider = id == "telegram" ? f.Telegram.Provider : id == "slack" ? f.Slack.Provider : new FakeChatProvider(id, id == "FakeAlpha");
            var config = id == "telegram" ? f.Telegram.Configuration() : id == "slack" ? f.Slack.Configuration() : new ChatConnectionConfiguration("one", id, "tenant", 1, 1, ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>());
            IChatSecretStore secrets = id == "telegram" ? f.Telegram : id == "slack" ? f.Slack : new FakeSecretStore();
            var result = await provider.OpenSessionAsync(config, secrets);
            Assert.True(result.Succeeded);
            f.Session = result.Value!; f.Capabilities = provider.Descriptor.Capabilities;
            return f;
        }
        public void DistinctTopics()
        {
            var topic = 6;
            this.Telegram.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(method == "createForumTopic" ? TelegramProviderFixture.Ok(new { message_thread_id = ++topic }) : null);
        }
        public bool RemotelyClosed(ChatConversation conversation) => id == "telegram"
            ? this.Telegram.Calls.Any(c => c.Method == "closeForumTopic" && c.Body.GetProperty("message_thread_id").GetString() == conversation.Address.ConversationId)
            : this.Session is FakeChatSession fake && fake.RemotelyClosed;
        public async Task SeedDiscoveryAsync()
        {
            this.Telegram.Override = (_, method, body, _) => Task.FromResult<HttpResponseMessage?>(method == "getChat" ? TelegramProviderFixture.Ok(new { id = long.Parse(body.GetProperty("chat_id").GetString()!), type = "supergroup" }) : null);
            if (id == "telegram")
                for (var i = 100; i <= 150; i++) Assert.True((await this.Session.ValidateDestinationAsync(this.Address with { ChannelId = "-" + i })).Succeeded);
            this.Slack.Override = (_, method, body, _) => Task.FromResult<HttpResponseMessage?>(method != "conversations.list" ? null : body.GetProperty("cursor").GetString() switch
            {
                "" => SlackProviderFixture.Json(new { ok = true, channels = Array.Empty<object>(), response_metadata = new { next_cursor = "opaque+/==" } }),
                "opaque+/==" => SlackProviderFixture.Json(new { ok = true, channels = new[] { SlackProviderFixture.Channel("C1"), SlackProviderFixture.Channel("C2") }, response_metadata = new { next_cursor = "" } }),
                _ => throw new InvalidOperationException("Cursor was not preserved."),
            });
        }
        public IEnumerable<string> RenderedText() => id == "telegram" ? this.Telegram.Calls.Where(c => c.Method == "sendMessage").Select(c => c.Body.GetProperty("text").GetString()!)
            : id == "slack" ? this.Slack.Calls.Where(c => c.Method == "chat.postMessage").Select(c => c.Body.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString()!)
            : ((FakeChatSession)this.Session).Rendered.Select(value => WebUtility.HtmlDecode(value));
        public ChatSemanticNotification PayloadByteOverflow() => id switch
        {
            "telegram" => this.Notification with { Body = new string('界', 3900), Actions = Enumerable.Repeat(new ChatSemanticAction(new string('界', 256), TelegramProviderFixture.Key, "question.answer"), 20).ToArray() },
            "slack" => this.Notification with { Body = new string('界', 2980) },
            _ => this.Notification with { Body = new string('界', 90) },
        };
        public void AssertSafeWirePayloads()
        {
            Assert.All(this.RenderedText(), value =>
            {
                Assert.InRange(value.Length, 1, this.Capabilities.MaxTextCharacters);
                Assert.Equal(value, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(value)));
            });
            foreach (var call in this.Telegram.Calls.Where(c => c.Method == "sendMessage"))
            {
                Assert.False(call.Body.TryGetProperty("parse_mode", out _));
                Assert.True(call.Body.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
                Assert.True(Encoding.UTF8.GetByteCount(call.Body.GetRawText()) <= this.Capabilities.MaxPayloadBytes);
            }
            foreach (var call in this.Slack.Calls.Where(c => c.Method == "chat.postMessage"))
            {
                Assert.False(call.Body.GetProperty("mrkdwn").GetBoolean());
                Assert.Equal("none", call.Body.GetProperty("parse").GetString());
                Assert.False(call.Body.GetProperty("unfurl_links").GetBoolean());
                Assert.Equal("plain_text", call.Body.GetProperty("blocks")[0].GetProperty("text").GetProperty("type").GetString());
                Assert.True(Encoding.UTF8.GetByteCount(call.Body.GetRawText()) <= this.Capabilities.MaxPayloadBytes);
            }
        }
        public void FailNext(ChatFailureKind kind)
        {
            if (this.Session is FakeChatSession fake) { fake.NextFailure = new(kind, "fixture-unavailable", kind == ChatFailureKind.RateLimited ? TimeSpan.FromSeconds(9) : null); return; }
            this.Telegram.Override = (_, _, _, _) => Task.FromResult<HttpResponseMessage?>(TelegramProviderFixture.Error(kind == ChatFailureKind.RateLimited ? 429 : 503, "SECRET https://invalid.test/?token=SECRET", 9));
            this.Slack.Override = (_, _, _, _) =>
            {
                var response = SlackProviderFixture.Error("SECRET");
                response.StatusCode = kind == ChatFailureKind.RateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable;
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(9));
                return Task.FromResult<HttpResponseMessage?>(response);
            };
        }
        public async ValueTask DisposeAsync()
        {
            if (this.Session is not null) await this.Session.DisposeAsync();
            this.Telegram.Dispose(); this.Slack.Dispose();
        }
    }
}