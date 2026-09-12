// -----------------------------------------------------------------------
// <copyright file="SlackConnectionSession.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Collections.Concurrent;
using System.Text.Json;

public sealed class SlackConnectionSession : IChatConnectionSession, IChatSharedReceptionSession
{
    private readonly ChatConnectionConfiguration configuration;
    private readonly SlackApiClient api;
    private readonly SlackSocketReceiver receiver;
    private readonly Func<ValueTask> detach;
    private readonly ConcurrentDictionary<ChatAddress, bool> retired = new();
    private int disposed;
    internal SlackConnectionSession(ChatConnectionConfiguration configuration, string userId, string botId, SlackApiClient api,
        SlackSocketReceiver receiver, Func<ValueTask> detach)
    { this.configuration = configuration; this.BotUserId = userId; this.BotId = botId; this.api = api; this.receiver = receiver; this.detach = detach; }
    public string ConnectionId => this.configuration.Id;
    public string ReceptionKey => this.receiver.AppId;
    public string? ReceptionFailureCode => this.receiver.FailureCode;
    internal string WorkspaceId => this.configuration.InstallationId;
    internal string BotUserId { get; }
    internal string BotId { get; }
    internal bool PrivateChannels => SlackApiClient.True(this.configuration.Settings, "includePrivateChannels");
    private bool Owns(ChatAddress address) => this.disposed == 0 && address.ConnectionId == this.ConnectionId && address.ExternalTenantId == this.WorkspaceId
        && SlackApiClient.Id(address.ChannelId) && address.ChannelId[0] is 'C' or 'G' && (address.ConversationId is null || SlackApiClient.Timestamp(address.ConversationId));

    public async ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Owns(address)) return Fail<ChatDestination>("slack-address-mismatch");
        var result = await this.api.CallAsync("conversations.info", new { channel = address.ChannelId }, ct);
        if (!result.Succeeded) return new(null, result.Failure);
        var channel = SlackApiClient.Object(result.Value, "channel");
        if (SlackApiClient.String(channel, "id") != address.ChannelId) return Fail<ChatDestination>("slack-channel-unavailable");
        if (SlackApiClient.True(channel, "is_archived")) return Fail<ChatDestination>("slack-channel-archived");
        if (SlackApiClient.True(channel, "is_ext_shared")) return Fail<ChatDestination>("slack-connect-unsupported");
        if (SlackApiClient.True(channel, "is_im") || SlackApiClient.True(channel, "is_mpim")) return Fail<ChatDestination>("slack-channel-required");
        if (SlackApiClient.True(channel, "is_private") && !this.PrivateChannels) return Fail<ChatDestination>("slack-private-channels-disabled");
        if (!SlackApiClient.True(channel, "is_member")) return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.Unauthorized, "slack-membership-required");
        return ChatProviderResult<ChatDestination>.Success(new(address, SlackApiClient.String(channel, "name") ?? address.ChannelId,
            SlackApiClient.True(channel, "is_private") ? "private-channel" : "public-channel"));
    }
    public async ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (this.disposed != 0 || cursor is { Length: > 1024 }) return Fail<ChatDestinationPage>("slack-cursor-invalid");
        var result = await this.api.CallAsync("conversations.list", new { types = this.PrivateChannels ? "public_channel,private_channel" : "public_channel", limit = 200, exclude_archived = true, cursor = cursor ?? "" }, ct);
        if (!result.Succeeded) return new(null, result.Failure);
        var channels = SlackApiClient.Object(result.Value, "channels");
        if (channels.ValueKind != JsonValueKind.Array) return Fail<ChatDestinationPage>("slack-channel-unavailable");
        var destinations = new List<ChatDestination>();
        foreach (var channel in channels.EnumerateArray())
        {
            var id = SlackApiClient.String(channel, "id");
            if (!SlackApiClient.Id(id) || !SlackApiClient.True(channel, "is_member") || SlackApiClient.True(channel, "is_archived")
                || SlackApiClient.True(channel, "is_ext_shared") || SlackApiClient.True(channel, "is_im") || SlackApiClient.True(channel, "is_mpim")
                || SlackApiClient.True(channel, "is_private") && !this.PrivateChannels) continue;
            destinations.Add(new(new(this.ConnectionId, this.WorkspaceId, id!), SlackApiClient.String(channel, "name") ?? id!, SlackApiClient.True(channel, "is_private") ? "private-channel" : "public-channel"));
        }
        var next = SlackApiClient.String(SlackApiClient.Object(result.Value, "response_metadata"), "next_cursor");
        if (next?.Length > 1024) return Fail<ChatDestinationPage>("slack-cursor-invalid");
        return ChatProviderResult<ChatDestinationPage>.Success(new(destinations, string.IsNullOrEmpty(next) ? null : next));
    }
    public async ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination, ChatConversationPolicy policy, string operationKey, string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (policy is not (ChatConversationPolicy.Flat or ChatConversationPolicy.Thread)) return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.NotSupported, "slack-policy-unsupported");
        var validated = await this.ValidateDestinationAsync(destination.Address, ct);
        if (!validated.Succeeded) return new(null, validated.Failure);
        if (policy == ChatConversationPolicy.Flat) return ChatProviderResult<ChatConversation>.Success(new(destination.Address with { ConversationId = null }, null));
        if (destination.Address.ConversationId is { } existing) return ChatProviderResult<ChatConversation>.Success(new(destination.Address, existing));
        // The host durably claims creation before calling. Unknown outcomes must be repaired, never blindly recreated.
        var notification = new ChatSemanticNotification(operationKey, "conversation-root", title, "Forge workstream updates and questions appear in this thread.", 1, []);
        var preflight = SlackMessageRenderer.Render(destination.Address, notification);
        if (!preflight.Succeeded) return new(null, preflight.Failure);
        if (preflight.Value!.Count != 1) return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.InvalidPayload, "render-payload-limit:edit-text:3000");
        var root = await this.SendAsync(destination.Address, notification, ct);
        if (!root.Succeeded || root.Value!.Messages.Count != 1) return new(null, root.Failure ?? new(ChatFailureKind.UnknownOutcome, "slack-root-outcome-unknown"));
        var ts = root.Value.Messages[0].MessageId;
        return ChatProviderResult<ChatConversation>.Success(new(destination.Address with { ConversationId = ts }, ts));
    }
    public async ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Owns(conversation.Address)) return Fail<bool>("slack-address-mismatch");
        if (conversation.RootMessageId is { } root)
        {
            var result = await this.EditAsync(new(conversation.Address, root), new("retired", "conversation-root", "Forge workstream", "This thread is retired. Open Forge for the current workstream.", 1, []), ct);
            if (!result.Succeeded) return result;
        }
        this.retired[conversation.Address] = true;
        return ChatProviderResult<bool>.Success(true);
    }
    public async ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Owns(address) || this.retired.ContainsKey(address)) return Fail<ChatSendReceipt>("slack-address-mismatch");
        var rendered = SlackMessageRenderer.Render(address, notification);
        if (!rendered.Succeeded) return new(null, rendered.Failure);
        var receipts = new List<ChatMessageReference>();
        foreach (var payload in rendered.Value!)
        {
            ChatProviderResult<JsonElement> result;
            try { result = await this.api.CallAsync("chat.postMessage", payload, ct, address.ChannelId); }
            catch (OperationCanceledException) when (receipts.Count > 0) { return Partial(receipts, new(ChatFailureKind.UnknownOutcome, "slack-send-outcome-unknown")); }
            var ts = SlackApiClient.String(result.Value, "ts");
            if (!result.Succeeded || !SlackApiClient.Timestamp(ts) || SlackApiClient.String(result.Value, "channel") != address.ChannelId)
                return Partial(receipts, result.Failure ?? new(ChatFailureKind.UnknownOutcome, "slack-send-outcome-unknown"));
            receipts.Add(new(address, ts!));
        }
        return ChatProviderResult<ChatSendReceipt>.Success(new(receipts));
    }
    public async ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Owns(message.Address) || !SlackApiClient.Timestamp(message.MessageId)) return Fail<bool>("slack-address-mismatch");
        var rendered = SlackMessageRenderer.Render(message.Address, notification, message.MessageId);
        if (!rendered.Succeeded) return new(default, rendered.Failure);
        var result = await this.api.CallAsync("chat.update", rendered.Value![0], ct);
        if (!result.Succeeded) return new(default, result.Failure);
        return SlackApiClient.String(result.Value, "channel") == message.Address.ChannelId && SlackApiClient.String(result.Value, "ts") == message.MessageId
            ? ChatProviderResult<bool>.Success(true) : Fail<bool>("slack-message-unavailable");
    }
    public Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct) => this.receiver.ReceiveAsync(sink, ct);

    /// <summary>Request proof from one Slack user without posting the challenge to the channel.</summary>
    public async Task<ChatProviderResult<bool>> RequestIdentityLinkAsync(ChatAddress address, string senderId, string challenge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Owns(address) || address.ConversationId is not null || !SlackApiClient.Id(senderId)
            || senderId[0] is not ('U' or 'W') || senderId == this.BotUserId
            || challenge.Length != 64 || !challenge.All(char.IsAsciiHexDigit)) return Fail<bool>("invalid-link-target");
        var destination = await this.ValidateDestinationAsync(address, ct);
        if (!destination.Succeeded) return new(false, destination.Failure);
        const string text = "Confirm the account link you just requested in Forge. This connects your Slack account to the signed-in Forge operator. Expires in two minutes.";
        var result = await this.api.CallAsync("chat.postEphemeral", new
        {
            channel = address.ChannelId, user = senderId, text, parse = "none", link_names = false,
            blocks = new object[]
            {
                new { type = "section", text = new { type = "plain_text", text } },
                new { type = "actions", elements = new[] { new { type = "button", action_id = "forge_identity_link",
                    text = new { type = "plain_text", text = "Confirm link" }, style = "primary", value = challenge,
                    confirm = new { title = new { type = "plain_text", text = "Link your Slack account?" },
                        text = new { type = "plain_text", text = "Continue only if you just requested this link in your signed-in Forge browser." },
                        confirm = new { type = "plain_text", text = "Link account" }, deny = new { type = "plain_text", text = "Cancel" } } } } },
            },
        }, ct, address.ChannelId);
        if (!result.Succeeded) return new(false, result.Failure);
        return SlackApiClient.Timestamp(SlackApiClient.String(result.Value, "message_ts"))
            ? ChatProviderResult<bool>.Success(true)
            : ChatProviderResult<bool>.Fail(ChatFailureKind.UnknownOutcome, "slack-send-outcome-unknown");
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref this.disposed, 1) == 0) await this.detach(); }
    private static ChatProviderResult<T> Fail<T>(string code) => ChatProviderResult<T>.Fail(ChatFailureKind.InvalidConfiguration, code);
    private static ChatProviderResult<ChatSendReceipt> Partial(List<ChatMessageReference> receipts, ChatProviderFailure failure)
        => receipts.Count == 0 ? new(null, failure) : new(new(receipts), new(ChatFailureKind.UnknownOutcome, "slack-partial-send-outcome-unknown"));
}