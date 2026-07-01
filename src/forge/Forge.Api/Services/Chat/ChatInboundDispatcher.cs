// -----------------------------------------------------------------------
// <copyright file="ChatInboundDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge, Phase 4) — routes one normalized inbound update to a
/// Forge action. Handles the <c>/link</c> bind + slash commands, enforces the
/// <c>chat_subscribers</c> allow-list (unlinked chat ids are ignored), and
/// correlates a reply/button-tap back to the originating <c>chat_notifications</c>
/// row to answer an open question via the shared <see cref="IOpenQuestionService"/>.
///
/// <para>Correlation: a Telegram <c>callback_query</c> and a text reply both carry
/// the id of the message they act on, mapped to
/// <see cref="ChatInbound.ReplyToMessageId"/> — so a single lookup
/// (<c>ChatMessageId == ReplyToMessageId</c>) resolves the notification for both.</para>
/// </summary>
public sealed class ChatInboundDispatcher
{
    /// <summary>Callback-data scheme for an open-question choice: <c>oq|{externalId}|{choiceIndex}</c>.</summary>
    public const string OpenQuestionCallbackPrefix = "oq";

    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly IOpenQuestionService openQuestions;
    private readonly IOperatorActionService operatorActions;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatBridgeOptions> options;
    private readonly ILogger<ChatInboundDispatcher> logger;

    public ChatInboundDispatcher(
        ControlDbContext control,
        ProductDbContext db,
        IOpenQuestionService openQuestions,
        IOperatorActionService operatorActions,
        IChatTransport transport,
        IOptionsMonitor<ChatBridgeOptions> options,
        ILogger<ChatInboundDispatcher> logger)
    {
        this.control = control;
        this.db = db;
        this.openQuestions = openQuestions;
        this.operatorActions = operatorActions;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Route a single inbound update. Never throws — logs and swallows per-update errors.</summary>
    public async Task DispatchAsync(ChatInbound update, CancellationToken ct)
    {
        try
        {
            var text = update.Text?.Trim() ?? string.Empty;

            // /link is the ONE command an as-yet-unlinked chat id may issue.
            if (text.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
            {
                await this.HandleLinkAsync(update, text, ct);
                return;
            }

            // Part B — authorize by the sender's user id, not the chat id: in a
            // shared supergroup every message carries the same chat id. The
            // subscriber's ChatId column holds the user id (in a DM they're equal,
            // so legacy rows already match).
            var subscriber = await this.control.ChatSubscribers.FirstOrDefaultAsync(
                s => s.Platform == this.transport.Platform && s.LinkedAt != null && s.ChatId == update.FromId, ct);
            if (subscriber is null)
            {
                subscriber = await this.TryBootstrapAllowedUserAsync(update, ct);
            }

            if (subscriber is null)
            {
                // Hard allow-list: an unlinked user can't drive Forge.
                this.logger.LogInformation("Chat inbound from unlinked user {UserId} (chat {ChatId}) ignored.", update.FromId, update.ChatId);
                return;
            }

            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                await this.HandleCommandAsync(update, text, subscriber, ct);
                return;
            }

            var cb = update.CallbackData ?? string.Empty;

            // Stalled-workstream action buttons: callback `act|{action}|{wsId}`.
            if (cb.StartsWith("act|", StringComparison.Ordinal))
            {
                await this.HandleWorkstreamActionAsync(update, subscriber, ct);
                return;
            }

            // Queue-strain [Confirm] + auth/human-interaction [Retry|Done|Snooze]
            // acks: `strain|{action}|…` / `hi|{action}|…`.
            if (cb.StartsWith("strain|", StringComparison.Ordinal) || cb.StartsWith("hi|", StringComparison.Ordinal))
            {
                await this.HandleAckActionAsync(update, ct);
                return;
            }

            // Otherwise a reply/tap that should answer an open question.
            await this.HandleAnswerAsync(update, subscriber, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "chat inbound dispatch failed for update {UpdateId}", update.UpdateId);
        }
    }

    private async Task HandleLinkAsync(ChatInbound update, string text, CancellationToken ct)
    {
        var token = text.Length > 5 ? text[5..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            await this.transport.SendAsync(update.ChatId, "Usage: /link <token> — get a token from Forge → Chat settings.", buttons: null, ct);
            return;
        }

        var pending = await this.control.ChatSubscribers.FirstOrDefaultAsync(
            s => s.Platform == this.transport.Platform && s.LinkToken == token && s.LinkedAt == null, ct);
        if (pending is null)
        {
            await this.transport.SendAsync(update.ChatId, "❌ That link token is invalid or already used.", buttons: null, ct);
            return;
        }

        // Bind this operator; the token is single-use. ChatId holds the sender's
        // user id (the identity key) — in a DM it equals the chat id, in a
        // supergroup it disambiguates operators sharing the group.
        pending.ChatId = update.FromId;
        pending.LinkedAt = DateTimeOffset.UtcNow;
        pending.LinkToken = null;
        await this.control.SaveChangesAsync(ct);

        await this.transport.SendAsync(update.ChatId,
            "✅ Linked. You'll get Forge alerts here and can answer questions by tapping a choice or replying.",
            buttons: null, ct);
    }

    /// <summary>
    /// Part B (group mode) — auto-link a sender whose user id is in the
    /// <c>AllowedUserIds</c> bootstrap so an operator can drive Forge from the
    /// supergroup without a per-user <c>/link</c> dance. No-op when the sender
    /// isn't allow-listed. Idempotent: a second message just re-finds the row.
    /// </summary>
    private async Task<ChatSubscriberRow?> TryBootstrapAllowedUserAsync(ChatInbound update, CancellationToken ct)
    {
        if (!long.TryParse(update.FromId, out var fromId)) return null;
        var allowed = this.options.CurrentValue.Telegram.AllowedUserIds;
        if (allowed is null || !allowed.Contains(fromId)) return null;

        var row = new ChatSubscriberRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Platform = this.transport.Platform,
            ChatId = update.FromId, // identity key (see ChatSubscriberRow.ChatId).
            LinkedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this.control.ChatSubscribers.Add(row);
        try
        {
            await this.control.SaveChangesAsync(ct);
            this.logger.LogInformation("Bootstrapped allow-listed chat operator {UserId} from the group.", update.FromId);
            return row;
        }
        catch (DbUpdateException)
        {
            // Raced another update (unique index on platform+chatid) — re-read.
            this.control.Entry(row).State = EntityState.Detached;
            return await this.control.ChatSubscribers.FirstOrDefaultAsync(
                s => s.Platform == this.transport.Platform && s.ChatId == update.FromId, ct);
        }
    }

    private async Task HandleCommandAsync(ChatInbound update, string text, ChatSubscriberRow subscriber, CancellationToken ct)
    {
        var cmd = text.Split(' ', 2)[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/help":
                await this.transport.SendAsync(update.ChatId,
                    "Forge chat bridge:\n" +
                    "• Tap a choice button or reply to a question to answer it.\n" +
                    "• /status — your pending questions.\n" +
                    "• /mute · /unmute — pause/resume alerts.\n" +
                    "• /help — this message.",
                    buttons: null, ct);
                break;

            case "/mute":
            case "/unmute":
                subscriber.Muted = cmd == "/mute";
                await this.control.SaveChangesAsync(ct);
                await this.transport.SendAsync(update.ChatId, subscriber.Muted ? "🔕 Muted." : "🔔 Unmuted.", buttons: null, ct);
                break;

            case "/status":
                var openCount = await this.db.WorkItems
                    .CountAsync(w => w.ExecutionStatus == ExecutionStatuses.AwaitingAnswers, ct);
                await this.transport.SendAsync(update.ChatId,
                    openCount == 0 ? "✅ No work items are awaiting answers." : $"⏳ {openCount} work item(s) awaiting answers.",
                    buttons: null, ct);
                break;

            default:
                await this.transport.SendAsync(update.ChatId, "Unknown command. /help for options.", buttons: null, ct);
                break;
        }
    }

    private async Task HandleWorkstreamActionAsync(ChatInbound update, ChatSubscriberRow subscriber, CancellationToken ct)
    {
        var parts = update.CallbackData!.Split('|');
        if (parts.Length != 3) return;
        var action = parts[1];
        var wsId = parts[2];
        var actor = subscriber.ForgeUserId ?? $"chat:{update.FromId}";
        var notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
            n => n.ChatMessageId == update.ReplyToMessageId, ct);

        // Restart is destructive → arm a second-tap confirm before executing.
        if (action == "restart")
        {
            await this.EditIfPossibleAsync(update,
                "⚠ Restart wipes the worktree, deliverables & branch. Tap to confirm.",
                new[]
                {
                    new ChatButton("⚠ Confirm restart", $"act|restart-confirm|{wsId}"),
                    new ChatButton("Cancel", $"act|restart-cancel|{wsId}"),
                }, ct);
            return;
        }

        if (action == "restart-cancel")
        {
            await this.EditIfPossibleAsync(update, "Restart cancelled — the workstream is unchanged.", buttons: null, ct);
            return;
        }

        var outcome = action switch
        {
            "retry" => await this.operatorActions.RetryAsync(wsId, actor, ct),
            "accept" => await this.operatorActions.AcceptAsync(wsId, actor, ct),
            "cancel" => await this.operatorActions.CancelAsync(wsId, ct),
            "restart-confirm" => await this.operatorActions.RestartAsync(wsId, ct),
            _ => OperatorActionOutcome.Problem(400, null, "Unknown action", null),
        };

        if (notif is not null)
        {
            notif.Status = ChatNotificationStatuses.Acted;
            notif.RespondedAt = DateTimeOffset.UtcNow;
            await this.control.SaveChangesAsync(ct);
        }

        var label = action == "restart-confirm" ? "restart" : action;
        await this.EditIfPossibleAsync(update,
            outcome.Ok ? $"✅ {label} done." : $"❌ {label} failed: {outcome.Title}", buttons: null, ct);
    }

    private async Task EditIfPossibleAsync(ChatInbound update, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(update.ChatId) && !string.IsNullOrEmpty(update.ReplyToMessageId))
        {
            await this.transport.EditAsync(update.ChatId, update.ReplyToMessageId!, text, buttons, ct);
        }
    }

    /// <summary>
    /// Ack-style buttons that resolve the alert on the Forge side elsewhere:
    /// queue-strain [Confirm] and auth/human-interaction [Retry|Done|Snooze].
    /// Marks the notification acted and edits the message; the actual
    /// reassignment / host auth completes in the UI / on the host.
    /// </summary>
    private async Task HandleAckActionAsync(ChatInbound update, CancellationToken ct)
    {
        var parts = (update.CallbackData ?? string.Empty).Split('|');
        if (parts.Length < 2) return;
        var kind = parts[0];
        var action = parts[1];

        var notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
            n => n.ChatMessageId == update.ReplyToMessageId, ct);
        if (notif is not null && action != "snooze")
        {
            notif.Status = ChatNotificationStatuses.Acted;
            notif.RespondedAt = DateTimeOffset.UtcNow;
            await this.control.SaveChangesAsync(ct);
        }

        var msg = (kind, action) switch
        {
            ("strain", "confirm") => "✅ Acknowledged — kept in queue.",
            ("hi", "done") => "✅ Marked done — thanks for completing it on the host.",
            ("hi", "snooze") => "⏰ Snoozed — I'll remind you again later.",
            ("hi", "retry") => "🔁 Re-attempting — complete the sign-in / prompt on the Forge host.",
            _ => "Done.",
        };
        await this.EditIfPossibleAsync(update, msg, buttons: null, ct);
    }

    private async Task HandleAnswerAsync(ChatInbound update, ChatSubscriberRow subscriber, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(update.ReplyToMessageId))
        {
            return; // uncorrelatable (not a reply / not a tap) — ignore quietly.
        }

        var notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
            n => n.ChatMessageId == update.ReplyToMessageId, ct);
        if (notif is null || notif.Kind != "open-questions" || string.IsNullOrEmpty(notif.WorkItemId))
        {
            return;
        }

        var actor = subscriber.ForgeUserId ?? $"chat:{update.FromId}";

        // Button tap → callback data `oq|{externalId}|{choiceIndex}`.
        if (!string.IsNullOrEmpty(update.CallbackData))
        {
            var answer = await this.ResolveChoiceAnswerAsync(notif.WorkItemId!, update.CallbackData, ct);
            if (answer is null) return;
            await this.AnswerAndEditAsync(notif, answer.Value.ExternalId, answer.Value.Answer, actor, ct);
            return;
        }

        // Free-text reply → answers the single unanswered question, when unambiguous.
        var unanswered = await this.db.DeliverableQuestions
            .Where(q => q.WorkItemId == notif.WorkItemId && (q.Answer == null || q.Answer == ""))
            .ToListAsync(ct);
        var distinct = unanswered
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();
        if (distinct.Count == 1 && !string.IsNullOrWhiteSpace(update.Text))
        {
            await this.AnswerAndEditAsync(notif, distinct[0], update.Text!.Trim(), actor, ct);
        }
        else if (distinct.Count > 1)
        {
            await this.transport.SendAsync(update.ChatId,
                "This item has multiple open questions — tap a choice button, or answer in the Forge UI.", buttons: null, ct);
        }
    }

    private async Task<(string ExternalId, string Answer)?> ResolveChoiceAnswerAsync(string workItemId, string callbackData, CancellationToken ct)
    {
        var parts = callbackData.Split('|');
        if (parts.Length != 3 || parts[0] != OpenQuestionCallbackPrefix) return null;
        var externalId = parts[1];
        if (!int.TryParse(parts[2], out var choiceIdx)) return null;

        var row = await this.db.DeliverableQuestions
            .Where(q => q.WorkItemId == workItemId && q.ExternalId == externalId)
            .OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var choices = ChatChoiceParser.Parse(row.ChoicesJson);
        if (choiceIdx < 0 || choiceIdx >= choices.Count) return null;
        return (externalId, choices[choiceIdx]);
    }

    private async Task AnswerAndEditAsync(ChatNotificationRow notif, string externalId, string answer, string actor, CancellationToken ct)
    {
        await this.openQuestions.AnswerAsync(
            notif.WorkItemId!, new[] { new OpenQuestionAnswer(externalId, answer) }, actor, ct);

        // Reflect the answer on the sent message and release the outbox slot.
        notif.Status = ChatNotificationStatuses.Answered;
        notif.RespondedAt = DateTimeOffset.UtcNow;
        await this.control.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(notif.ChatId) && !string.IsNullOrEmpty(notif.ChatMessageId))
        {
            await this.transport.EditAsync(notif.ChatId!, notif.ChatMessageId!,
                $"{notif.Title}\n\n✅ Answered: {answer}", buttons: null, ct);
        }
    }
}

/// <summary>Parses a <c>deliverable_questions.ChoicesJson</c> array into a choice list.</summary>
public static class ChatChoiceParser
{
    public static IReadOnlyList<string> Parse(string? choicesJson)
    {
        if (string.IsNullOrWhiteSpace(choicesJson)) return Array.Empty<string>();
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(choicesJson);
            return list ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
