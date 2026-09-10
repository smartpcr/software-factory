// -----------------------------------------------------------------------
// <copyright file="ChatContentPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.RegularExpressions;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.Extensions.Options;

/// <summary>Metadata by default; bounded question text requires both product and subscription opt-in.</summary>
public sealed class ChatContentPolicy(IOptions<ChatPluginOptions> options)
{
    public bool AllowsQuestions(string? productId, string subscriptionMode = "redacted") => subscriptionMode == "redacted"
        && (productId is null ? options.Value.HostContentMode : options.Value.ProductContentModes.GetValueOrDefault(productId)) == "redacted";

    public ChatSemanticNotification Render(ChatNotificationRow row, ChatDeliveryMessageRow part, string subscriptionMode,
        IEnumerable<string> credentials, IReadOnlyList<ChatSemanticAction> actions)
    {
        var title = row.Kind switch
        {
            "open-questions" => "Forge · Open questions", "workstream-stalled" => "Forge · Workstream needs attention",
            "queue-strain" => "Forge · Queue pressure", "human-interaction" => "Forge · Host action needed", _ => "Forge notification",
        };
        var body = part.ControlsRetired ? "This request is closed. Open Forge for the current status." : "Open Forge to review the current status.";
        if (part.ConfirmationActorId is not null && !part.ControlsRetired) body = "Confirm restart of this target. This resets its worktree and run. Confirmation expires in 30 seconds.";
        if (row.SpawnId is not null) body += $"\nTarget: {row.SpawnId} / {row.WorkstreamId ?? row.ArtifactKey ?? "parent"}";
        var question = ChatPayloadConverter.Read(row)?.Questions.SingleOrDefault(q => q.Id == part.QuestionId && q.Revision == part.ExpectedRevision);
        var allow = !part.ControlsRetired && this.AllowsQuestions(row.ScopeKind == "host" ? null : row.ProductId, subscriptionMode)
            && (part.QuestionId is null || question?.Text is not null);
        if (allow && question?.Text is { } questionText) body = questionText + "\n\n" + body;
        var link = this.Create(row.Id, row.Kind, ChatPayloadConverter.Read(row)?.StoryId ?? "", []).Body;
        var linkStart = link.IndexOf("\n\n", StringComparison.Ordinal);
        if (linkStart >= 0) body += link[linkStart..];
        return new(row.Id, row.Kind, Redact(title, credentials), Redact(body, credentials), part.DesiredRevision,
            allow ? actions : [], part.QuestionId);
    }

    public bool AcceptsAnswer(string? text, IEnumerable<string> credentials) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= 4000 && Redact(text, credentials) == text;

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