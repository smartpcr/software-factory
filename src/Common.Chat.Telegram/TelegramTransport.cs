// -----------------------------------------------------------------------
// <copyright file="TelegramTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Telegram <see cref="IChatTransport"/> over raw HTTP (the Bot API's
/// <c>sendMessage</c> / <c>editMessageText</c> / <c>getUpdates</c>) — raw
/// <c>HttpClient</c>, no bot SDK, small deps. A fresh client is built per call
/// from the named factory client so tests can swap the message handler.
///
/// <para>All calls are failure-tolerant: transport/HTTP errors and Telegram
/// <c>ok:false</c> bodies become a <see cref="ChatSendResult.Failure"/> /
/// empty update list — never an unhandled throw — so an outbound pump can
/// retry rather than die.</para>
/// </summary>
public sealed class TelegramTransport : IChatTransport
{
    public const string ClientName = "chat-telegram";

    /// <summary>Telegram's hard per-message character cap.</summary>
    public const int MaxMessageChars = 4096;

    private const string TruncationSuffix = "\n… open in UI";

    private readonly IHttpClientFactory clientFactory;
    private readonly IOptionsMonitor<ChatTransportOptions> options;
    private readonly ILogger<TelegramTransport> logger;

    public TelegramTransport(
        IHttpClientFactory clientFactory,
        IOptionsMonitor<ChatTransportOptions> options,
        ILogger<TelegramTransport> logger)
    {
        this.clientFactory = clientFactory;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string Platform => "telegram";

    /// <inheritdoc/>
    public async Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null)
    {
        var body = BuildMessageBody(chatId, Cap(text), buttons);
        if (!string.IsNullOrEmpty(threadId))
        {
            body["message_thread_id"] = threadId;
        }

        return await this.PostForMessageIdAsync("sendMessage", body, ct);
    }

    /// <inheritdoc/>
    public async Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct)
    {
        var opt = this.options.CurrentValue;
        var token = ChatTokenResolver.Resolve(opt, this.logger);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var client = this.clientFactory.CreateClient(ClientName);
            var body = new Dictionary<string, object> { ["chat_id"] = chatId, ["name"] = Cap(name) };
            using var resp = await client.PostAsJsonAsync($"{BaseUrl(opt, token)}/createForumTopic", body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : Trim(json);
                this.logger.LogWarning("createForumTopic failed: {Desc}", desc);
                return null;
            }

            if (doc.RootElement.TryGetProperty("result", out var res)
                && res.TryGetProperty("message_thread_id", out var tid))
            {
                return TryString(tid);
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "createForumTopic threw.");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct)
    {
        var body = BuildMessageBody(chatId, Cap(text), buttons);
        body["message_id"] = messageId;
        var result = await this.PostForMessageIdAsync("editMessageText", body, ct);

        // Telegram rejects an edit whose text/markup is unchanged with
        // "message is not modified" — treat that as a successful no-op.
        if (!result.Ok && result.Error is not null
            && result.Error.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            return ChatSendResult.Success(messageId);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatInbound>> PollUpdatesAsync(long offset, CancellationToken ct)
    {
        var opt = this.options.CurrentValue;
        var token = ChatTokenResolver.Resolve(opt, this.logger);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Array.Empty<ChatInbound>();
        }

        try
        {
            var client = this.clientFactory.CreateClient(ClientName);
            var url = $"{BaseUrl(opt, token)}/getUpdates?offset={offset}&timeout={opt.Telegram.LongPollTimeoutSec}";
            using var resp = await client.GetAsync(url, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                this.logger.LogWarning("Telegram getUpdates failed: {Status} {Body}", (int)resp.StatusCode, Trim(json));
                return Array.Empty<ChatInbound>();
            }

            return ParseUpdates(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Telegram getUpdates threw — treating as no updates.");
            return Array.Empty<ChatInbound>();
        }
    }

    /// <summary>Truncate to Telegram's char cap with an "open in UI" hint.</summary>
    public static string Cap(string text)
    {
        text ??= string.Empty;
        if (text.Length <= MaxMessageChars)
        {
            return text;
        }

        var keep = MaxMessageChars - TruncationSuffix.Length;
        return string.Concat(text.AsSpan(0, keep), TruncationSuffix);
    }

    /// <summary>Parse a <c>getUpdates</c> response body into normalized inbound updates.</summary>
    public static IReadOnlyList<ChatInbound> ParseUpdates(string json)
    {
        var list = new List<ChatInbound>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                return list;
            }

            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var upd in result.EnumerateArray())
            {
                var updateId = upd.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var n) ? n : 0L;

                if (upd.TryGetProperty("callback_query", out var cb))
                {
                    var chatId = TryString(TryPath(cb, "message", "chat", "id"));
                    var fromId = TryString(TryPath(cb, "from", "id"));
                    var data = cb.TryGetProperty("data", out var d) ? d.GetString() : null;
                    var replyTo = TryString(TryPath(cb, "message", "message_id"));
                    var threadId = NullIfEmpty(TryString(TryPath(cb, "message", "message_thread_id")));
                    list.Add(new ChatInbound(updateId, chatId, fromId, Text: null, CallbackData: data, ReplyToMessageId: replyTo, ThreadId: threadId));
                    continue;
                }

                if (upd.TryGetProperty("message", out var msg))
                {
                    var chatId = TryString(TryPath(msg, "chat", "id"));
                    var fromId = TryString(TryPath(msg, "from", "id"));
                    var text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
                    var replyTo = TryString(TryPath(msg, "reply_to_message", "message_id"));
                    var threadId = NullIfEmpty(TryString(TryPath(msg, "message_thread_id")));
                    list.Add(new ChatInbound(updateId, chatId, fromId, Text: text, CallbackData: null, ReplyToMessageId: replyTo, ThreadId: threadId));
                }
            }
        }
        catch (JsonException)
        {
            // Malformed body — return whatever parsed so far (usually empty).
        }

        return list;
    }

    private async Task<ChatSendResult> PostForMessageIdAsync(string method, Dictionary<string, object> body, CancellationToken ct)
    {
        var opt = this.options.CurrentValue;
        var token = ChatTokenResolver.Resolve(opt, this.logger);
        if (string.IsNullOrWhiteSpace(token))
        {
            return ChatSendResult.Failure("chat bridge has no bot token configured");
        }

        try
        {
            var client = this.clientFactory.CreateClient(ClientName);
            var url = $"{BaseUrl(opt, token)}/{method}";
            using var resp = await client.PostAsJsonAsync(url, body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : Trim(json);
                return ChatSendResult.Failure(desc ?? $"telegram {method} returned ok:false");
            }

            string? messageId = null;
            if (doc.RootElement.TryGetProperty("result", out var res)
                && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("message_id", out var mid))
            {
                messageId = TryString(mid);
            }

            return ChatSendResult.Success(messageId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Telegram {Method} threw.", method);
            return ChatSendResult.Failure(ex.Message);
        }
    }

    private static Dictionary<string, object> BuildMessageBody(string chatId, string text, IReadOnlyList<ChatButton>? buttons)
    {
        var body = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
        };

        if (buttons is { Count: > 0 })
        {
            // One button per row keeps long labels readable.
            var keyboard = buttons
                .Select(b => new[] { new { text = b.Label, callback_data = b.CallbackData } })
                .ToArray();
            body["reply_markup"] = new { inline_keyboard = keyboard };
        }

        return body;
    }

    private static string BaseUrl(ChatTransportOptions opt, string token)
    {
        var root = string.IsNullOrWhiteSpace(opt.Telegram.ApiBaseUrl)
            ? "https://api.telegram.org"
            : opt.Telegram.ApiBaseUrl.TrimEnd('/');
        return $"{root}/bot{token}";
    }

    private static JsonElement? TryPath(JsonElement el, params string[] path)
    {
        var current = el;
        foreach (var seg in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(seg, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string TryString(JsonElement? el)
    {
        if (el is not { } e)
        {
            return string.Empty;
        }

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? string.Empty,
            JsonValueKind.Number => e.TryGetInt64(out var n) ? n.ToString() : e.GetRawText(),
            _ => string.Empty,
        };
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}