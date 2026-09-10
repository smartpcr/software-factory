// -----------------------------------------------------------------------
// <copyright file="ChatContentPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.RegularExpressions;
using Common.Chat;
using Microsoft.Extensions.Options;

/// <summary>Metadata only in Stage 1S. Agent titles, questions, choices and source never enter the outbox.</summary>
public sealed class ChatContentPolicy(IOptions<ChatPluginOptions> options)
{
    public ChatSemanticNotification Create(string id, string kind, string storyId, IEnumerable<string> credentials)
    {
        var title = kind == "setup" ? "Forge · Notifications only" : "Forge · Open questions";
        var body = kind == "setup"
            ? "Notifications are enabled for this story. Open-question alerts contain metadata only. Open Forge to respond."
            : "Open questions were raised for this story. Open Forge to review the current status.";
        if (Uri.TryCreate(options.Value.TelegramSlice.ForgeBaseUrl, UriKind.Absolute, out var origin)
            && origin.Scheme is "http" or "https" && origin.UserInfo.Length == 0 && origin.Query.Length == 0
            && origin.Fragment.Length == 0 && origin.AbsolutePath == "/")
        {
            var path = storyId.Length <= 128 && storyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? "/workstreams/" + Uri.EscapeDataString(storyId) : "/workstreams";
            body += $"\n\n{(origin.IsLoopback ? "Open on Forge host" : "Open in Forge")}: {origin.GetLeftPart(UriPartial.Authority)}{path}";
        }
        return new(id, kind, Redact(title, credentials), Redact(body, credentials), 1, []);
    }

    public static string Redact(string text, IEnumerable<string> credentials)
    {
        try
        {
            foreach (var secret in credentials.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            foreach (var pattern in new[]
            {
                @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)",
                @"https?://[^\s]*?(?:@|[?&](?:token|key|secret|signature|sig|password)=)[^\s]*",
                @"\b(?:Bearer\s+\S+|(?:authorization|token|password|secret|api[_-]?key)\s*[:=]\s*\S+)",
                @"\b\d{5,}:[A-Za-z0-9_-]{20,}",
                @"(?:\b[A-Za-z]:[\\/]|\\\\)[^\r\n]*|(?<![:/\w])/[^\s/]+(?:/[^\s]*)?",
            }) text = Regex.Replace(text, pattern, "[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            // Plain text transport has no parse mode; break automatic Telegram @mentions.
            return text.Replace("@", "＠", StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException) { return "Forge notification. Open Forge to review."; }
    }
}