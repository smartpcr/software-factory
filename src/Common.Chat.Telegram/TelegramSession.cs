// -----------------------------------------------------------------------
// <copyright file="TelegramSession.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Telegram;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

public sealed partial class TelegramSession : IChatConnectionSession, IChatResumableSession
{
    private readonly ChatConnectionConfiguration configuration;
    private readonly TelegramBotApi api;
    private readonly string token;
    private readonly string username;
    private readonly int pollSeconds;
    private readonly Action release;
    private readonly ConcurrentDictionary<string, ChatDestination> discovered = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim receiveGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long cursor = -1;
    private int disposed;
    internal TelegramSession(ChatConnectionConfiguration configuration, TelegramBotApi api, string token, string username, int pollSeconds, Action release)
    { this.configuration = configuration; this.api = api; this.token = token; this.username = username; this.pollSeconds = pollSeconds; this.release = release; }
    public string ConnectionId => this.configuration.Id;
    public string ReceptionKey => "bot:" + this.configuration.InstallationId;
    public string BotUsername => this.username;

    private bool Matches(ChatAddress address) => this.disposed == 0 && address.ConnectionId == this.ConnectionId
        && address.ExternalTenantId == this.configuration.InstallationId && IsId(address.ChannelId)
        && (address.ConversationId is null || long.TryParse(address.ConversationId, NumberStyles.None, CultureInfo.InvariantCulture, out var topic) && topic > 0);
    private static bool IsId(string id) => long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
        && value != 0 && value.ToString(CultureInfo.InvariantCulture) == id;

    public async ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Matches(address)) return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-address-mismatch");
        var result = await this.api.CallAsync("getChat", new { chat_id = address.ChannelId }, ct);
        if (!result.Succeeded) return new(null, result.Failure);
        var chat = result.Value; var kind = TelegramBotApi.String(chat, "type");
        if (TelegramBotApi.String(chat, "id") != address.ChannelId || kind is not ("private" or "group" or "supergroup" or "channel"))
            return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-chat-mismatch");
        if (kind != "private")
        {
            var membership = await this.MembershipAsync(address.ChannelId, ct);
            if (!membership.Succeeded) return new(null, membership.Failure);
            var member = membership.Value; var status = TelegramBotApi.String(member, "status");
            if (status is not ("creator" or "administrator" or "member" or "restricted")
                || status == "restricted" && (!TelegramBotApi.True(member, "is_member") || !TelegramBotApi.True(member, "can_send_messages"))
                || kind == "channel" && status != "creator" && !TelegramBotApi.True(member, "can_post_messages")
                || status == "member" && chat.TryGetProperty("permissions", out var permissions)
                    && permissions.TryGetProperty("can_send_messages", out var send) && send.ValueKind == JsonValueKind.False)
                return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.Unauthorized, "telegram-membership-denied");
        }
        var destination = new ChatDestination(address, "Telegram " + address.ChannelId, TelegramBotApi.True(chat, "is_forum") ? "forum" : kind);
        this.discovered[address.ChannelId] = destination with { Address = address with { ConversationId = null } };
        return ChatProviderResult<ChatDestination>.Success(destination);
    }

    private Task<ChatProviderResult<JsonElement>> MembershipAsync(string chatId, CancellationToken ct)
        => this.api.CallAsync("getChatMember", new { chat_id = chatId, user_id = this.configuration.InstallationId }, ct);

    public ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (this.disposed != 0) return ValueTask.FromResult(ChatProviderResult<ChatDestinationPage>.Fail(ChatFailureKind.Unavailable, "telegram-session-closed"));
        // Discovery only reads chats observed by this session's one supervised receiver; it never calls getUpdates.
        var page = this.discovered.Values.OrderBy(d => d.Address.ChannelId, StringComparer.Ordinal)
            .Where(d => cursor is null || StringComparer.Ordinal.Compare(d.Address.ChannelId, cursor) > 0).Take(51).ToArray();
        return ValueTask.FromResult(ChatProviderResult<ChatDestinationPage>.Success(new(page.Take(50).ToArray(), page.Length > 50 ? page[49].Address.ChannelId : null)));
    }

    public async ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination, ChatConversationPolicy policy,
        string operationKey, string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Matches(destination.Address)) return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-address-mismatch");
        if (policy == ChatConversationPolicy.Flat) return ChatProviderResult<ChatConversation>.Success(new(destination.Address with { ConversationId = null }, null));
        if (policy != ChatConversationPolicy.Topic) return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.NotSupported, "telegram-thread-unsupported");
        var verified = await this.ValidateDestinationAsync(destination.Address, ct);
        if (!verified.Succeeded) return new(null, verified.Failure);
        if (verified.Value!.Kind != "forum") return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.NotSupported, "telegram-forum-required");
        var membership = await this.MembershipAsync(destination.Address.ChannelId, ct);
        if (!membership.Succeeded) return new(null, membership.Failure);
        if (TelegramBotApi.String(membership.Value, "status") != "creator"
            && (TelegramBotApi.String(membership.Value, "status") != "administrator" || !TelegramBotApi.True(membership.Value, "can_manage_topics")))
            return ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.Unauthorized, "telegram-manage-topics-required");
        var name = TelegramRendering.Truncate(title.Replace(this.token, "[redacted]", StringComparison.Ordinal), 128);
        if (string.IsNullOrWhiteSpace(name)) name = "Forge workstream";
        // The host claims operationKey durably before this non-idempotent call. No local retry on an unknown result.
        var created = await this.api.CallAsync("createForumTopic", new { chat_id = destination.Address.ChannelId, name }, ct, mayCreate: true);
        if (!created.Succeeded) return new(null, created.Failure);
        var id = TelegramBotApi.String(created.Value, "message_thread_id");
        return id is not null && long.TryParse(id, out var number) && number > 0
            ? ChatProviderResult<ChatConversation>.Success(new(destination.Address with { ConversationId = id }, null))
            : ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.UnknownOutcome, "telegram-topic-unconfirmed");
    }

    public async ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Matches(conversation.Address)) return ChatProviderResult<bool>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-address-mismatch");
        if (conversation.Address.ConversationId is null) return ChatProviderResult<bool>.Success(true);
        var result = await this.api.CallAsync("closeForumTopic", new { chat_id = conversation.Address.ChannelId, message_thread_id = conversation.Address.ConversationId }, ct);
        return result.Succeeded ? ChatProviderResult<bool>.Success(true) : new(false, result.Failure);
    }

    public async ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Matches(address)) return ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-address-mismatch");
        var rendered = TelegramRendering.Render(address, notification, this.token);
        if (!rendered.Succeeded) return new(null, rendered.Failure);
        var messages = new List<ChatMessageReference>();
        foreach (var body in rendered.Value!)
        {
            var result = await this.api.CallAsync("sendMessage", body, ct, mayCreate: true);
            var id = result.Succeeded ? TelegramBotApi.String(result.Value, "message_id") : null;
            if (id is null)
                return messages.Count == 0 ? new(null, result.Failure ?? new(ChatFailureKind.UnknownOutcome, "telegram-send-unconfirmed"))
                    : new(new(messages), new(ChatFailureKind.UnknownOutcome, "telegram-partial-send"));
            messages.Add(new(address, id));
        }
        return ChatProviderResult<ChatSendReceipt>.Success(new(messages));
    }

    public async ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!this.Matches(message.Address) || !IsId(message.MessageId)) return ChatProviderResult<bool>.Fail(ChatFailureKind.InvalidConfiguration, "telegram-address-mismatch");
        var rendered = TelegramRendering.Render(message.Address, notification, this.token);
        if (!rendered.Succeeded) return new(false, rendered.Failure);
        if (rendered.Value!.Count != 1) return ChatProviderResult<bool>.Fail(ChatFailureKind.InvalidPayload, "render-payload-limit:edit-text:4096");
        var body = rendered.Value[0]; body["message_id"] = message.MessageId; body.Remove("message_thread_id");
        var result = await this.api.CallAsync("editMessageText", body, ct);
        return result.Succeeded ? ChatProviderResult<bool>.Success(true) : new(false, result.Failure);
    }

    public ValueTask RestoreCursorAsync(string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (value is not null && (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == long.MaxValue || value.Length > 19)) throw new InvalidOperationException("telegram-cursor-invalid");
        this.cursor = value is null ? -1 : long.Parse(value, CultureInfo.InvariantCulture); return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
        await this.lifetime.CancelAsync();
        await this.receiveGate.WaitAsync();
        try { this.release(); }
        finally { this.receiveGate.Release(); this.lifetime.Dispose(); }
    }
}