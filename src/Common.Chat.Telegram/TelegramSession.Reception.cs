// -----------------------------------------------------------------------
// <copyright file="TelegramSession.Reception.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Telegram;

using System.Globalization;
using System.Text.Json;

public sealed partial class TelegramSession
{
    public async Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct)
    {
        if (this.disposed != 0) throw new InvalidOperationException("telegram-session-closed");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.lifetime.Token); ct = linked.Token;
        if (!await this.receiveGate.WaitAsync(0, ct)) throw new InvalidOperationException("duplicate-telegram-receiver");
        try
        {
            while (!ct.IsCancellationRequested && this.disposed == 0)
            {
                var result = await this.PollCoreAsync(sink, ct);
                if (!result.Succeeded)
                {
                    if (result.Failure!.Kind is ChatFailureKind.Unauthorized or ChatFailureKind.InvalidConfiguration) throw new InvalidOperationException(result.Failure.Code);
                    await Task.Delay(result.Failure.RetryAfter ?? TimeSpan.FromSeconds(2), ct);
                }
                else if (result.Value == 0) await Task.Delay(200, ct);
            }
        }
        finally { this.receiveGate.Release(); }
    }

    /// <summary>One bounded poll for tests/hosts. Only the supervised receiver owns this operation.</summary>
    public async Task<ChatProviderResult<int>> PollOnceAsync(IChatInboundSink sink, CancellationToken ct = default)
    {
        if (this.disposed != 0) return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-session-closed");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.lifetime.Token); ct = linked.Token;
        if (!await this.receiveGate.WaitAsync(0, ct)) return ChatProviderResult<int>.Fail(ChatFailureKind.InvalidConfiguration, "duplicate-telegram-receiver");
        try { return await this.PollCoreAsync(sink, ct); }
        finally { this.receiveGate.Release(); }
    }

    private async Task<ChatProviderResult<int>> PollCoreAsync(IChatInboundSink sink, CancellationToken ct)
    {
        if (this.disposed != 0) return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-session-closed");
        var result = await this.api.CallAsync("getUpdates", new { offset = this.cursor < 0 ? 0 : this.cursor + 1, limit = 20,
            timeout = this.pollSeconds, allowed_updates = new[] { "message", "callback_query", "my_chat_member" } }, ct);
        if (!result.Succeeded) return new(0, result.Failure);
        if (result.Value.ValueKind != JsonValueKind.Array) return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-updates-invalid");
        ChatInboundEnvelope?[] updates;
        try { updates = result.Value.EnumerateArray().Select(this.Normalize).ToArray(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        { return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-updates-invalid"); }
        if (updates.Length == 0) return ChatProviderResult<int>.Success(0);
        if (updates.Any(e => e is null) || updates.Length > 20) return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-updates-invalid");
        var envelopes = updates.Select(e => e!).ToArray();
        var maximum = envelopes.Max(e => long.Parse(e.DeliveryKey, CultureInfo.InvariantCulture));
        ChatInboundAcceptance accepted;
        if (sink is IChatDurableBatchSink batch)
            accepted = await batch.AcceptBatchAsync(envelopes, [new(this.ConnectionId, maximum.ToString(CultureInfo.InvariantCulture))], ct);
        else return ChatProviderResult<int>.Fail(ChatFailureKind.InvalidConfiguration, "durable-batch-sink-required");
        if (accepted == ChatInboundAcceptance.Retry) return ChatProviderResult<int>.Fail(ChatFailureKind.Unavailable, "telegram-inbox-retry");
        this.cursor = Math.Max(this.cursor, maximum);
        using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(ct); acknowledgement.CancelAfter(TimeSpan.FromSeconds(2));
        await Task.WhenAll(envelopes.Where(e => e.CallbackQueryId is not null).Select(envelope => this.api.CallAsync("answerCallbackQuery",
            new { callback_query_id = envelope.CallbackQueryId, text = "Received. Checking in Forge.", cache_time = 0 }, acknowledgement.Token)));
        return ChatProviderResult<int>.Success(envelopes.Length);
    }

    private ChatInboundEnvelope? Normalize(JsonElement update)
    {
        var updateId = TelegramBotApi.String(update, "update_id");
        if (!long.TryParse(updateId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || parsed == long.MaxValue) return null;
        var callbackId = default(string); var callbackData = default(string); var kind = "ignored";
        JsonElement message = default; JsonElement sender = default;
        if (update.TryGetProperty("callback_query", out var callback))
        {
            callbackId = TelegramBotApi.String(callback, "id"); callbackData = TelegramBotApi.String(callback, "data");
            callback.TryGetProperty("from", out sender); callback.TryGetProperty("message", out message); kind = "action";
        }
        else if (update.TryGetProperty("message", out message)) { message.TryGetProperty("from", out sender); kind = "text"; }
        else if (update.TryGetProperty("my_chat_member", out var membership)) message = membership;
        JsonElement chat = default;
        if (message.ValueKind == JsonValueKind.Object) message.TryGetProperty("chat", out chat);
        var chatId = TelegramBotApi.String(chat, "id") ?? "0"; var senderId = TelegramBotApi.String(sender, "id") ?? "0";
        var threadId = TelegramBotApi.String(message, "message_thread_id");
        var address = new ChatAddress(this.ConnectionId, this.configuration.InstallationId, chatId, threadId);
        if (chatId != "0") this.discovered[chatId] = new(address with { ConversationId = null }, "Telegram " + chatId,
            TelegramBotApi.True(chat, "is_forum") ? "forum" : TelegramBotApi.String(chat, "type") ?? "unknown");
        if (TelegramBotApi.True(sender, "is_bot") || senderId == "0" || chatId == "0" || senderId == this.configuration.InstallationId) kind = "ignored";
        var text = kind == "text" ? TelegramBotApi.String(message, "text") : null;
        if (kind == "text" && text is null) kind = "unsupported";
        var replyTo = kind == "action" ? TelegramBotApi.String(message, "message_id")
            : message.ValueKind == JsonValueKind.Object && message.TryGetProperty("reply_to_message", out var reply) ? TelegramBotApi.String(reply, "message_id") : null;
        string? command = null; string? argument = null;
        if (text?.StartsWith("/", StringComparison.Ordinal) == true)
        {
            var fields = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries); var addressed = fields[0][1..].Split('@', 2);
            if (addressed.Length == 2 && !addressed[1].Equals(this.username, StringComparison.OrdinalIgnoreCase)) { kind = "ignored"; text = null; }
            else
            {
                command = addressed[0].ToLowerInvariant(); argument = fields.Length == 2 ? fields[1].Trim() : ""; kind = "command"; text = null;
                if (command == "answer")
                { var answer = argument.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries); argument = answer.FirstOrDefault() ?? ""; text = answer.Length == 2 ? answer[1] : null; }
            }
        }
        return new("telegram", this.ConnectionId, updateId!, address, senderId, kind, TelegramBotApi.String(message, "message_id"), replyTo,
            text, kind == "action" ? callbackData : null, DateTimeOffset.UtcNow, CallbackQueryId: callbackId, Command: command, CommandArgument: argument);
    }
}