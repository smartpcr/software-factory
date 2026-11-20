// -----------------------------------------------------------------------
// <copyright file="ChatContentPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.RegularExpressions;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.Extensions.Options;

/// <summary>Metadata by default; bounded question text requires both product and subscription opt-in.</summary>
public sealed class ChatContentPolicy(IOptions<ChatPluginOptions> options)
{
    public bool AllowsQuestions(string? productId, string subscriptionMode = "redacted") => subscriptionMode == "redacted"
        && (productId is null ? options.Value.HostContentMode : options.Value.ProductContentMode(productId)) == "redacted";

    public ChatSemanticNotification Render(ChatNotificationRow row, ChatDeliveryMessageRow part, string subscriptionMode,
        IEnumerable<string> credentials, IReadOnlyList<ChatSemanticAction> actions)
    {
        var title = row.Kind switch
        {
            "open-questions" => "Forge · Open questions", "workstream-stalled" => "Forge · Workstream needs attention",
            "queue-strain" => "Forge · Queue pressure", "human-interaction" => "Forge · Host action needed",
            "setup" => "Forge · Chat enabled", ChatEventCatalog.SpawnHalted => "Forge · Story halted", _ => "Forge notification",
        };
        var body = part.ControlsRetired ? "This request is closed. Open Forge for the current status." : "Open Forge to review the current status.";
        var payload = ChatPayloadConverter.Read(row);
        if (row.Kind == "setup" && !part.ControlsRetired) body = !string.IsNullOrEmpty(payload?.Body) && payload.Body != "Chat is enabled for this parent and every current and future child."
            ? payload.Body
            : "Chat is enabled for this parent. Every current and future child shares this conversation. Link your Forge operator account before using work actions.";
        if (part.ConfirmationActorId is not null && !part.ControlsRetired) body = part.ActionKind switch
        {
            "question.prompt" => "Reply to this message with your answer. Only the operator who requested this prompt can answer it. Expires in 10 minutes.",
            ChatInboxService.PinPrompt => "Reply to this message with the decision to pin. It is added to every future iteration prompt for this document. Only the operator who requested this prompt can answer it. Expires in 10 minutes.",
            "artifact.restart" => "Confirm restart of this document. Its iterations are cleared and it starts again at iteration 1. Confirmation expires in 30 seconds.",
            _ => "Confirm restart of this target. This resets its worktree and run. Confirmation expires in 30 seconds.",
        };
        // Name the story and stage the way operators see them, not internal ids.
        if (row.SpawnId is not null) body += payload?.StoryId is { Length: > 0 } story
            ? $"\nStory {story}" + (payload.TargetLabel is { Length: > 0 } label ? $" · {label}" : "")
            : $"\nTarget: {row.SpawnId} / {row.WorkstreamId ?? row.ArtifactKey ?? "parent"}";
        var question = payload?.Questions.SingleOrDefault(q => q.Id == part.QuestionId && q.Revision == part.ExpectedRevision);
        var allow = !part.ControlsRetired && this.AllowsQuestions(row.ScopeKind == "host" ? null : row.ProductId, subscriptionMode)
            && (part.QuestionId is null || question?.Text is not null);
        if (allow && question?.Text is { } questionText) body = questionText + "\n\n" + body;
        // A halted story's reason (redacted at enqueue) leads the message where content is allowed.
        if (allow && row.Kind == ChatEventCatalog.SpawnHalted && part.ConfirmationActorId is null && payload?.Body is { Length: > 0 } reason
            && reason != row.Body) body = reason + "\n\n" + body;
        var link = this.Create(row.Id, row.Kind, ChatPayloadConverter.Read(row)?.StoryId ?? "", []).Body;
        var linkStart = link.IndexOf("\n\n", StringComparison.Ordinal);
        if (linkStart >= 0) body += link[linkStart..];
        return new(row.Id, row.Kind, Redact(title, credentials), Redact(body, credentials), part.DesiredRevision,
            allow ? actions : [], part.QuestionId, ForceReply: allow && part.ActionKind is "question.prompt" or ChatInboxService.PinPrompt);
    }

    public static IReadOnlyList<string> Choices(string? json, IEnumerable<string> credentials)
    {
        if (json is null) return [];
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json);
            // Unsupported/unsafe choices use the explicit custom-answer path, never a changed choice value.
            return values is null || values.Length > 10 || values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200 || Redact(v, credentials) != v) ? [] : values;
        }
        catch (JsonException) { return []; }
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
                @"\bx(?:ox[baprs]-|app-)[A-Za-z0-9-]{10,}",
                @"(?:\b[A-Za-z]:[\\/]|\\\\)[^\r\n]*|(?<![:/\w])/[^\s/]+(?:/[^\s]*)?",
            }) text = Regex.Replace(text, pattern, "[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            // Plain text transport has no parse mode; break automatic Telegram @mentions.
            return text.Replace("@", "＠", StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException) { return "Forge notification. Open Forge to review."; }
    }
}