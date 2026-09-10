// -----------------------------------------------------------------------
// <copyright file="SlackMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SlackMessageRenderer
{
    internal const int TextLimit = 3000;
    internal const int PayloadLimit = 32768;
    internal static bool ActionKey(string? token) => token is { Length: 25 }
        && Regex.IsMatch(token, @"\Aa1_[A-Za-z0-9_-]{22}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static ChatProviderResult<IReadOnlyList<Dictionary<string, object>>> Render(ChatAddress address, ChatSemanticNotification notification, string? messageId = null)
    {
        var text = string.IsNullOrEmpty(notification.Title) ? notification.Body : notification.Title + "\n" + notification.Body;
        if (notification.ForceReply) text += "\nSend your answer in this thread. If you selected several questions, use !forge answer <message ID> <answer>.";
        if (text.Length is 0 or > 65536 || !ValidUnicode(text)) return Fail("text:65536");
        if (notification.Actions.Count > 20) return Fail("buttons:20");
        foreach (var action in notification.Actions)
        {
            if (Encoding.UTF8.GetByteCount(action.Token) > 2000) return Fail("button-value:2000");
            if (!ActionKey(action.Token)) return Fail("action-key:25");
            if (action.Label.Length is 0 or > 75 || !ValidUnicode(action.Label)) return Fail("button-label:75");
        }
        var chunks = Split(text).ToArray();
        if (messageId is not null && chunks.Length != 1) return Fail("edit-text:3000");
        var result = new List<Dictionary<string, object>>();
        for (var i = 0; i < chunks.Length; i++)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notification.Id + ":" + notification.Revision + ":" + i)))[..24];
            var blocks = new List<object> { new { type = "section", block_id = "text_" + key, text = new { type = "plain_text", text = chunks[i], emoji = false } } };
            var actions = i == chunks.Length - 1 ? notification.Actions : [];
            if (actions.Count > 0) blocks.Add(new { type = "actions", block_id = "actions_" + key, elements = actions.Select((a, index) => new
            {
                type = "button", action_id = "forge_" + index, value = a.Token,
                text = new { type = "plain_text", text = a.Label, emoji = false }, accessibility_label = a.Label,
            }).ToArray() });
            // Complete fallback content, including choices, with Slack parsing/unfurls disabled.
            var fallback = chunks[i] + (actions.Count > 0 ? "\nChoices: " + string.Join("; ", actions.Select(a => a.Label)) : "");
            var payload = new Dictionary<string, object>
            {
                ["channel"] = address.ChannelId, ["text"] = Escape(fallback), ["blocks"] = blocks,
                ["mrkdwn"] = false, ["parse"] = "none", ["link_names"] = false, ["unfurl_links"] = false, ["unfurl_media"] = false,
            };
            if (messageId is not null)
            {
                payload["ts"] = messageId; payload["as_user"] = true;
                // chat.update has a narrower argument set than chat.postMessage.
                payload.Remove("mrkdwn"); payload.Remove("unfurl_links"); payload.Remove("unfurl_media");
            }
            else if (address.ConversationId is not null) payload["thread_ts"] = address.ConversationId;
            if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > PayloadLimit) return Fail("payload-bytes:32768");
            result.Add(payload);
        }
        return ChatProviderResult<IReadOnlyList<Dictionary<string, object>>>.Success(result);
    }
    private static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    private static bool ValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    private static IEnumerable<string> Split(string value)
    {
        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(TextLimit, value.Length - offset);
            if (char.IsHighSurrogate(value[offset + length - 1])) length--;
            yield return value.Substring(offset, length); offset += length;
        }
    }
    private static ChatProviderResult<IReadOnlyList<Dictionary<string, object>>> Fail(string field)
        => ChatProviderResult<IReadOnlyList<Dictionary<string, object>>>.Fail(ChatFailureKind.InvalidPayload, "render-payload-limit:" + field);
}