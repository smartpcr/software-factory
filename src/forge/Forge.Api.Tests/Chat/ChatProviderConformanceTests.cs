// -----------------------------------------------------------------------
// <copyright file="ChatProviderConformanceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Plugins;
using Forge.Api.Services.Chat;
using Xunit;

/// <summary>Stage 1 shared harness. Real protocol fixtures join this suite in Stages 3/4.</summary>
public sealed class ChatProviderConformanceTests
{
    [Theory]
    [InlineData("FakeAlpha")]
    [InlineData("FakeBeta")]
    [InlineData("telegram")]
    [InlineData("slack")]
    public async Task Shared_provider_contract_qualifies_destinations_receipts_edits_and_unsupported_policies(string id)
    {
        using var telegram = new TelegramProviderFixture();
        using var slack = new SlackProviderFixture();
        IChatProviderPlugin provider = id == "slack" ? slack.Provider : id == "telegram" ? telegram.Provider : new FakeChatProvider(id, id == "FakeAlpha");
        var config = id == "slack" ? slack.Configuration() : id == "telegram" ? telegram.Configuration() : new ChatConnectionConfiguration("one", id, "installation", 1, 1,
            ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>());
        IChatSecretStore secrets = id == "slack" ? slack : id == "telegram" ? telegram : new FakeSecretStore();
        var registry = new ChatProviderRegistry([provider]); Assert.Same(provider, registry.Resolve(id));
        await using var session = (await registry.Resolve(id)!.OpenSessionAsync(config, secrets)).Value!;
        var address = id == "slack" ? SlackProviderFixture.Address(thread: null) : id == "telegram" ? TelegramProviderFixture.Address(topic: null) : new ChatAddress("one", "tenant", "channel");
        var destination = (await session.ValidateDestinationAsync(address)).Value!; Assert.Equal(address, destination.Address);
        var flat = (await session.EnsureConversationAsync(destination, ChatConversationPolicy.Flat, "parent", "Forge")).Value!;
        var notification = new ChatSemanticNotification("event", "open-questions", "Forge", "Hello café", 1,
            [new("Answer", TelegramProviderFixture.Key, "question.answer")]);
        var receipt = (await session.SendAsync(flat.Address, notification)).Value!;
        Assert.All(receipt.Messages, message => { Assert.Equal(flat.Address, message.Address); Assert.False(string.IsNullOrEmpty(message.MessageId)); });
        Assert.True((await session.EditAsync(receipt.Messages[0], notification with { Revision = 2, Actions = [] })).Succeeded);
        Assert.False((await session.EditAsync(receipt.Messages[0] with { Address = address with { ConnectionId = "other" } }, notification)).Succeeded);
        Assert.Equal(ChatFailureKind.InvalidPayload, (await session.SendAsync(flat.Address, notification with { Actions = [new("Answer", new string('界', 65), "question.answer")] })).Failure!.Kind);
        var unsupported = provider.Descriptor.Capabilities.Topics ? ChatConversationPolicy.Thread : ChatConversationPolicy.Topic;
        Assert.Equal(ChatFailureKind.NotSupported, (await session.EnsureConversationAsync(destination, unsupported, "unsupported", "Forge")).Failure!.Kind);
        Assert.True((await session.RetireConversationAsync(flat)).Succeeded);
    }

    [Theory]
    [InlineData("FakeAlpha", true)]
    [InlineData("FakeBeta", false)]
    public async Task Connections_Conversations_And_Message_Addresses_Remain_Independent(string id, bool topics)
    {
        var provider = new FakeChatProvider(id, topics);
        var config = new ChatConnectionConfiguration("one", id, "installation", 1, 1, ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>());
        var first = (await provider.OpenSessionAsync(config, new FakeSecretStore())).Value!;
        var second = (await provider.OpenSessionAsync(config with { Id = "two" }, new FakeSecretStore())).Value!;
        await using var session = first;
        await using var other = second;
        var destination = new ChatDestination(new("one", "T:opaque", "C/opaque"), "destination", "group");
        var policy = topics ? ChatConversationPolicy.Topic : ChatConversationPolicy.Thread;
        var conversation = (await first.EnsureConversationAsync(destination, policy, "parent-one", "Title")).Value!;
        Assert.Equal(conversation, (await first.EnsureConversationAsync(destination, policy, "parent-one", "Renamed")).Value);
        Assert.NotEqual(conversation, (await first.EnsureConversationAsync(destination, policy, "parent-two", "Other")).Value);
        var message = new ChatSemanticNotification("question", "open-questions", "Title", "<source>& café", 1, [new("Answer", "opaque-token", "answer")]);
        var receipt = (await first.SendAsync(conversation.Address, message)).Value!;
        var reference = Assert.Single(receipt.Messages);
        Assert.Equal(conversation.Address, reference.Address);
        Assert.Contains(".", reference.MessageId); // timestamps remain strings, never parsed as numbers
        Assert.True((await first.EditAsync(reference, message with { Revision = 2, Actions = [] })).Succeeded);
        Assert.False((await other.EditAsync(reference, message)).Succeeded);
        Assert.True((await first.RetireConversationAsync(conversation)).Succeeded);
        Assert.False((await first.SendAsync(conversation.Address, message)).Succeeded);
        Assert.Equal(topics, ((FakeChatSession)first).RemotelyClosed);
        Assert.DoesNotContain("<source>", ((FakeChatSession)first).Rendered.Single());
    }

    [Theory]
    [InlineData("FakeAlpha", true)]
    [InlineData("FakeBeta", false)]
    public async Task Capabilities_Limits_Retry_Cancellation_And_Durable_Receipt_Are_Explicit(string id, bool topics)
    {
        var provider = new FakeChatProvider(id, topics);
        var config = new ChatConnectionConfiguration("c", id, "installation", 1, 3, ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference>());
        await using var session = (FakeChatSession)(await provider.OpenSessionAsync(config, new FakeSecretStore())).Value!;
        var page = await session.DiscoverAsync(null);
        if (provider.Descriptor.Capabilities.DestinationDiscovery)
        {
            Assert.NotNull(page.Value!.NextCursor);
            Assert.Null((await session.DiscoverAsync(page.Value.NextCursor)).Value!.NextCursor);
        }
        else Assert.Equal(ChatFailureKind.NotSupported, page.Failure!.Kind);
        var address = new ChatAddress("c", "tenant", "channel");
        var message = new ChatSemanticNotification("n", "open-questions", "", "hello", 1, []);
        Assert.Equal(ChatFailureKind.NotSupported, (await session.EnsureConversationAsync(new(address, "D", "group"),
            topics ? ChatConversationPolicy.Thread : ChatConversationPolicy.Topic, "unsupported", "Title")).Failure!.Kind);
        Assert.Equal(ChatFailureKind.InvalidPayload, (await session.SendAsync(address, message with { Body = new string('x', 101) })).Failure!.Kind);
        Assert.Equal(ChatFailureKind.InvalidPayload, (await session.SendAsync(address, message with { Body = string.Concat(Enumerable.Repeat("界", 90)) })).Failure!.Kind);
        Assert.Equal(ChatFailureKind.InvalidPayload, (await session.SendAsync(address, message with { Actions = [new("A", new string('x', 65), "answer")] })).Failure!.Kind);
        session.NextFailure = new(ChatFailureKind.RateLimited, "rate-limited", TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), (await session.SendAsync(address, message)).Failure!.RetryAfter);
        session.NextFailure = new(ChatFailureKind.Unavailable, "offline");
        Assert.Equal(ChatFailureKind.Unavailable, (await session.SendAsync(address, message)).Failure!.Kind);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(address, message, cancelled.Token).AsTask());
        var sink = new RecordingSink();
        await session.ReceiveAsync(sink, CancellationToken.None);
        Assert.Equal(2, session.Acknowledged);
        Assert.Equal(new[] { ChatInboundAcceptance.Retry, ChatInboundAcceptance.Accepted, ChatInboundAcceptance.Duplicate }, sink.Results);
        Assert.Equal("c", sink.Envelope!.ConnectionId);
        Assert.Equal(id, sink.Envelope.ProviderId);
        Assert.Equal(address, sink.Envelope.Address);
    }

    [Fact]
    public void Duplicate_Registry_Is_Unavailable_And_Version_Failure_Is_Provider_Local()
    {
        var alpha = new FakeChatProvider("FakeAlpha", true);
        var duplicate = new ChatProviderRegistry([alpha, new FakeChatProvider("fakealpha", false)]);
        Assert.False(duplicate.IsAvailable);
        Assert.Null(duplicate.Resolve("FakeAlpha"));
        var registry = new ChatProviderRegistry([alpha, new FakeChatProvider("FakeBeta", false, 99)]);
        Assert.True(registry.IsAvailable);
        Assert.Same(alpha, registry.Resolve("FakeAlpha"));
        Assert.Null(registry.Resolve("FakeBeta"));
        Assert.Equal("unsupported-provider-version", registry.Providers.Single(p => p.Descriptor.Id == "FakeBeta").Reason);
        var pluginRegistry = new WorkstreamPluginRegistry([new ChatWorkstreamPlugin(), new ChatWorkstreamPlugin()]);
        Assert.False(pluginRegistry.IsAvailable);
        Assert.Null(pluginRegistry.Resolve("chat"));
    }

    private sealed class RecordingSink : IChatInboundSink
    {
        public List<ChatInboundAcceptance> Results { get; } = [];
        public ChatInboundEnvelope? Envelope { get; private set; }
        public ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); this.Envelope = envelope;
            var result = this.Results.Count switch { 0 => ChatInboundAcceptance.Retry, 1 => ChatInboundAcceptance.Accepted, _ => ChatInboundAcceptance.Duplicate };
            this.Results.Add(result); return ValueTask.FromResult(result);
        }
    }
}

internal sealed class FakeChatProvider(string id, bool topics, int version = 1) : IChatProviderPlugin
{
    public ChatProviderDescriptor Descriptor { get; } = new(id, id, version, [1],
        new(topics ? ChatReceptionMode.LongPolling : ChatReceptionMode.Socket, true, true, true, topics, !topics, topics,
            topics ? ChatConversationRetirement.RemoteClose : ChatConversationRetirement.LocalOnly, 100, 256, 64), []);
    public ChatSettingsValidation ValidateSettings(int settingsVersion, JsonElement settings)
        => new(settingsVersion == 1 && settings.ValueKind == JsonValueKind.Object && !settings.EnumerateObject().Any());
    public ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(connection.ProviderId == this.Descriptor.Id && this.ValidateSettings(connection.SettingsVersion, connection.Settings).Valid
            ? ChatProviderResult<IChatConnectionSession>.Success(new FakeChatSession(connection, this.Descriptor))
            : ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.InvalidConfiguration, "invalid-connection"));
    }
}

internal sealed class FakeChatSession(ChatConnectionConfiguration config, ChatProviderDescriptor descriptor) : IChatConnectionSession
{
    private readonly Dictionary<string, ChatConversation> conversations = new(StringComparer.Ordinal);
    private readonly HashSet<ChatAddress> retired = [];
    private readonly HashSet<ChatMessageReference> messages = [];
    public string ConnectionId => config.Id;
    public string ReceptionKey => descriptor.Id + ":" + config.InstallationId;
    public bool RemotelyClosed { get; private set; }
    public int Acknowledged { get; private set; }
    public ChatProviderFailure? NextFailure { get; set; }
    public List<string> Rendered { get; } = [];
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default)
        => Result(address.ConnectionId == this.ConnectionId ? new ChatDestination(address, "destination", "group") : null, ct);
    public ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default)
        => descriptor.Capabilities.DestinationDiscovery ? Result(new ChatDestinationPage([new(new(this.ConnectionId, "tenant", cursor ?? "channel"), "destination", "group")], cursor is null ? "page-2" : null), ct)
            : Unsupported<ChatDestinationPage>(ct);
    public ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination, ChatConversationPolicy policy, string operationKey, string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (policy == ChatConversationPolicy.Topic && !descriptor.Capabilities.Topics || policy == ChatConversationPolicy.Thread && !descriptor.Capabilities.Threads)
            return Unsupported<ChatConversation>(ct);
        if (!this.conversations.TryGetValue(operationKey, out var conversation))
            this.conversations[operationKey] = conversation = new(destination.Address with { ConversationId = operationKey }, "1700000000.001");
        return Result(conversation, ct);
    }
    public ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); this.retired.Add(conversation.Address);
        this.RemotelyClosed = descriptor.Capabilities.Retirement == ChatConversationRetirement.RemoteClose;
        return Result(true, ct);
    }
    public ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (this.NextFailure is { } failure) { this.NextFailure = null; return ValueTask.FromResult(new ChatProviderResult<ChatSendReceipt>(null, failure)); }
        if (address.ConnectionId != this.ConnectionId || this.retired.Contains(address)) return Result<ChatSendReceipt>(null, ct);
        if (notification.Body.Length > descriptor.Capabilities.MaxTextCharacters || Encoding.UTF8.GetByteCount(notification.Body) > descriptor.Capabilities.MaxPayloadBytes
            || notification.Actions.Any(a => Encoding.UTF8.GetByteCount(a.Token) > descriptor.Capabilities.MaxActionBytes))
            return ValueTask.FromResult(ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.InvalidPayload, "payload-limit"));
        var reference = new ChatMessageReference(address, "1700000000." + this.messages.Count.ToString("D6"));
        this.messages.Add(reference); this.Rendered.Add(WebUtility.HtmlEncode(notification.Body));
        return Result(new ChatSendReceipt([reference]), ct);
    }
    public ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default)
        => this.messages.Contains(message) ? Result(true, ct) : ValueTask.FromResult(ChatProviderResult<bool>.Fail(ChatFailureKind.Unauthorized, "wrong-connection"));
    public async Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct)
    {
        var envelope = new ChatInboundEnvelope(descriptor.Id, this.ConnectionId, "delivery-opaque", new(this.ConnectionId, "tenant", "channel"), "user", "text", "123.456", null, "answer", null, DateTimeOffset.UtcNow);
        for (var attempt = 0; attempt < 3; attempt++)
            if (await sink.AcceptAsync(envelope, ct) is ChatInboundAcceptance.Accepted or ChatInboundAcceptance.Duplicate) this.Acknowledged++;
    }
    private static ValueTask<ChatProviderResult<T>> Result<T>(T? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(value is null ? ChatProviderResult<T>.Fail(ChatFailureKind.Unauthorized, "invalid-address") : ChatProviderResult<T>.Success(value));
    }
    private static ValueTask<ChatProviderResult<T>> Unsupported<T>(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(ChatProviderResult<T>.Fail(ChatFailureKind.NotSupported, "not-supported")); }
}

internal sealed class FakeSecretStore : IChatSecretStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public string Id => "fake";
    public ChatSecretStoreStatus Status => new(true, false);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default) => ValueTask.FromResult(this.values.GetValueOrDefault(reference.Key));
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default)
    { this.values[key] = value; return ValueTask.FromResult(new ChatSecretReference(this.Id, key)); }
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default)
    { this.values.Remove(reference.Key); return ValueTask.CompletedTask; }
}