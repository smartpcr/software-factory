// -----------------------------------------------------------------------
// <copyright file="TelegramRendering.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Telegram;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class TelegramRendering
{
    public static bool IsActionKey(string value) => value.Length == 25 && Regex.IsMatch(value, @"\Aa1_[A-Za-z0-9_-]{22}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string Truncate(string text, int limit)
    {
        var length = Math.Min(limit, text.Length);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    internal static ChatProviderResult<IReadOnlyList<Dictionary<string, object>>> Render(ChatAddress address, ChatSemanticNotification value, string credential)
    {
        ChatProviderResult<IReadOnlyList<Dictionary<string, object>>> Fail(string field, int limit)
            => ChatProviderResult<IReadOnlyList<Dictionary<string, object>>>.Fail(ChatFailureKind.InvalidPayload, $"render-payload-limit:{field}:{limit}");
        foreach (var action in value.Actions)
        {
            if (Encoding.UTF8.GetByteCount(action.Token) > 64) return Fail("callback_data", 64);
            if (!IsActionKey(action.Token)) return Fail("action-key", 25);
            if (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 256) return Fail("button-label", 256);
        }
        if (value.Actions.Count > 20 || value.ForceReply && value.Actions.Count > 0) return Fail("buttons", 20);
        var text = (value.Title.Length == 0 ? value.Body : value.Title + "\n\n" + value.Body).Replace(credential, "[redacted]", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16 * 4096) return Fail("text", 65536);
        var parts = new List<Dictionary<string, object>>();
        while (text.Length > 0)
        {
            var part = Truncate(text, 4096); text = text[part.Length..];
            var body = new Dictionary<string, object> { ["chat_id"] = address.ChannelId, ["text"] = part,
                ["link_preview_options"] = new { is_disabled = true } };
            if (address.ConversationId is not null) body["message_thread_id"] = address.ConversationId;
            body["reply_markup"] = text.Length == 0 && value.ForceReply ? new { force_reply = true, selective = false }
                : new { inline_keyboard = text.Length == 0 ? value.Actions.Select(a => new[] { new { text = a.Label.Replace(credential, "[redacted]", StringComparison.Ordinal), callback_data = a.Token } }).ToArray() : [] };
            if (JsonSerializer.SerializeToUtf8Bytes(body).Length > 32768) return Fail("payload-bytes", 32768);
            parts.Add(body);
        }
        return ChatProviderResult<IReadOnlyList<Dictionary<string, object>>>.Success(parts);
    }
}