// -----------------------------------------------------------------------
// <copyright file="ChatInboundDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge, Phase 4) — routes one normalized inbound update to a
/// Forge action. Registered as the app's <see cref="IChatInboundHandler"/> with
/// the <c>Common.Chat</c> inbound pump. Handles the <c>/link</c> bind + slash
/// commands, enforces the <c>chat_subscribers</c> allow-list (unlinked chat ids
/// are ignored), and correlates a reply/button-tap back to the originating
/// <c>chat_notifications</c> row to answer an open question via the shared
/// <see cref="IOpenQuestionService"/>.
///
/// <para>Correlation: a Telegram <c>callback_query</c> and a text reply both carry
/// the id of the message they act on, mapped to
/// <see cref="ChatInbound.ReplyToMessageId"/> — so a single lookup
/// (<c>ChatMessageId == ReplyToMessageId</c>) resolves the notification for both.
/// A <b>non-reply</b> message typed inside a spawn's forum topic falls back to the
/// channel route (<see cref="ChatInboundMessage.RouteKey"/>,
/// <c>spawn/{productId}/{spawnId}</c>) — it targets that spawn's outstanding
/// open-questions alert, and all product-DB work (questions, answer/resume) is
/// scoped to the notification's product via the AsyncLocal slug.</para>
/// </summary>
public sealed class ChatInboundDispatcher : IChatInboundHandler
{
    /// <summary>Callback-data scheme for an open-question choice: <c>oq|{externalId}|{choiceIndex}</c>.</summary>
    public const string OpenQuestionCallbackPrefix = "oq";

    private readonly HostIdentityStore identities;
    private readonly ForgeActorResolver actors;
    private readonly ForgeCapabilityAuthorizer authorizer;
    private readonly ChatMigratedIdentity? migrated;
    private readonly ControlDbContext control;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatBridgeOptions> options;
    private readonly ILogger<ChatInboundDispatcher> logger;

    public ChatInboundDispatcher(
        ControlDbContext control,
        IServiceScopeFactory scopeFactory,
        IChatTransport transport,
        IOptionsMonitor<ChatBridgeOptions> options,
        ILogger<ChatInboundDispatcher> logger,
        HostIdentityStore identities,
        ForgeActorResolver actors,
        ForgeCapabilityAuthorizer authorizer,
        ChatMigratedIdentity? migrated = null)
    {
        this.identities = identities;
        this.actors = actors;
        this.authorizer = authorizer;
        this.migrated = migrated;
        this.control = control;
        this.scopeFactory = scopeFactory;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task HandleAsync(ChatInboundMessage message, CancellationToken ct) =>
        this.DispatchAsync(message.Update, message.RouteKey, ct);

    /// <summary>Route a single unrouted update (kept for tests / non-topic paths).</summary>
    public Task DispatchAsync(ChatInbound update, CancellationToken ct) =>
        this.DispatchAsync(update, routeKey: null, ct);

    /// <summary>Route a single inbound update. Never throws — logs and swallows per-update errors.</summary>
    public async Task DispatchAsync(ChatInbound update, string? routeKey, CancellationToken ct)
    {
        // Check before /link, allow-list bootstrap, and every domain/command path.
        // The pump may consume updates to advance its cursor in notification-only
        // mode, but no subscriber, question, workstream, or acknowledgement changes.
        var opt = this.options.CurrentValue;
        if (!opt.Enabled || !opt.AllowRemoteActions) return;

        try
        {
            var text = update.Text?.Trim() ?? string.Empty;

            // /link is the ONE command an as-yet-unlinked chat id may issue.
            if (text.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
            {
                await this.HandleLinkAsync(update, text, ct);
                return;
            }

            var verified = await this.actors.FromProviderAsync(this.transport.Platform, update.ChatId, update.FromId, ct);
            if (verified is null && this.migrated is not null) verified = await this.migrated.ResolveLegacyAsync(this.transport.Platform, update.ChatId, update.FromId, ct);
            if (verified is null) return;
            // The old subscriber row is only a preference record; it conveys no authority.
            var subscriber = await this.control.ChatSubscribers.FirstOrDefaultAsync(
                s => s.Platform == this.transport.Platform && s.ChatId == update.FromId && s.ForgeUserId == verified.UserId, ct);
            subscriber ??= new ChatSubscriberRow { ForgeUserId = verified.UserId, Platform = this.transport.Platform, ChatId = update.FromId };
            using var actorScope = ForgeActorScope.Push(verified,
                ForgeChatRoutes.TryParseSpawn(routeKey, out var routedProduct, out _) ? routedProduct : "");

            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                await this.HandleCommandAsync(update, text, routeKey, subscriber, ct);
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

            // Otherwise a reply/tap (or a message in a spawn topic) that should
            // answer an open question.
            await this.HandleAnswerAsync(update, routeKey, subscriber, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "chat inbound dispatch failed for update {UpdateId}", update.UpdateId);
        }
    }

    private async Task HandleLinkAsync(ChatInbound update, string text, CancellationToken ct)
    {
        var token = text.Length > 5 ? text[5..].Trim() : string.Empty;
        var pendingUser = this.identities.ChallengeUserId(token, "link");
        if (pendingUser is null || !await this.control.Users.AnyAsync(u => u.Id == pendingUser, ct)) return;
        var link = this.identities.RedeemLinkChallenge(token, this.transport.Platform, update.ChatId, update.FromId);
        if (link is null) return;
        var verified = await this.actors.FromProviderAsync(this.transport.Platform, update.ChatId, update.FromId, ct);
        await this.authorizer.RequireAsync(verified, ForgeCapabilities.LinkSelf, new(), ct);
        var row = await this.control.ChatSubscribers.FirstOrDefaultAsync(
            s => s.Platform == this.transport.Platform && s.ChatId == update.FromId, ct);
        if (row is null)
        {
            row = new() { Id = Guid.NewGuid().ToString("N"), Platform = this.transport.Platform, ChatId = update.FromId, CreatedAt = DateTimeOffset.UtcNow };
            this.control.ChatSubscribers.Add(row);
        }
        row.ForgeUserId = link.UserId;
        row.LinkedAt = DateTimeOffset.UtcNow;
        row.LinkToken = null;
        await this.control.SaveChangesAsync(ct);
        await this.transport.SendAsync(update.ChatId, "Linked to your enrolled Forge operator.", null, ct, update.ThreadId);
    }

    private async Task HandleCommandAsync(ChatInbound update, string text, string? routeKey, ChatSubscriberRow subscriber, CancellationToken ct)
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
                await this.authorizer.RequireAsync(ForgeActorScope.Current?.Actor, ForgeCapabilities.LinkSelf, new(), ct);
                if (string.IsNullOrEmpty(subscriber.Id)) return;
                subscriber.Muted = cmd == "/mute";
                await this.control.SaveChangesAsync(ct);
                await this.transport.SendAsync(update.ChatId, subscriber.Muted ? "🔕 Muted." : "🔔 Unmuted.", buttons: null, ct);
                break;

            case "/status":
                var hasRoute = ForgeChatRoutes.TryParseSpawn(routeKey, out var productId, out var spawnId);
                var actor = ForgeActorScope.Current?.Actor;
                var productIds = hasRoute ? new[] { productId }
                    : await this.authorizer.ReadableProductIdsAsync(actor, ct);
                var openCount = 0;
                foreach (var targetProduct in productIds)
                {
                    await this.authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead,
                        new(targetProduct, SpawnId: hasRoute ? spawnId : null), ct);
                    var product = await this.authorizer.ResolveProductAsync(targetProduct, ct);
                    using var productScope = ProductDbContextFactory.PushAsyncLocalSlug(product!.Slug);
                    using var scope = this.scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var repoIds = await db.Repos.Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync(ct);
                    var pending = db.WorkItems.Where(w => w.RepoId != null && repoIds.Contains(w.RepoId)
                        && w.ExecutionStatus == ExecutionStatuses.AwaitingAnswers);
                    if (hasRoute)
                    {
                        var childIds = await db.Workstreams.Where(w => w.StorySpawnId == spawnId).Select(w => w.Id).ToListAsync(ct);
                        var storyId = await db.StorySpawns.Where(s => s.Id == spawnId).Select(s => s.StoryId).SingleAsync(ct);
                        var docPrefix = "plan-doc:" + spawnId + ":";
                        pending = pending.Where(w => childIds.Contains(w.Id) || w.Id == storyId || w.Id == spawnId || w.Id.StartsWith(docPrefix));
                    }
                    openCount += await pending.CountAsync(ct);
                }

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
        if (parts.Length < 2) return;
        var action = parts[1];
        var actor = subscriber.ForgeUserId ?? $"chat:{update.FromId}";
        var notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
            n => n.Platform == this.transport.Platform && n.ChatId == update.ChatId
                && n.ChatMessageId == update.ReplyToMessageId && n.Status == ChatNotificationStatuses.Sent, ct);

        if (notif is null || string.IsNullOrEmpty(notif.WorkstreamId)) return;
        var wsId = notif.WorkstreamId;
        var capabilityAction = action is "restart" or "restart-confirm" or "restart-cancel" ? "restart" : action;
        await this.authorizer.RequireAsync(ForgeActorScope.Current?.Actor, "workstream." + capabilityAction,
            new(notif.ProductId, SpawnId: notif.SpawnId, WorkstreamId: wsId), ct);
        var product = await this.authorizer.ResolveProductAsync(notif.ProductId, ct);

        // Restart is destructive → arm a second-tap confirm before executing.
        if (action == "restart")
        {
            await this.EditIfPossibleAsync(update,
                "⚠ Restart wipes the worktree, deliverables & branch. Tap to confirm.",
                new[]
                {
                    new ChatButton("⚠ Confirm restart", "act|restart-confirm"),
                    new ChatButton("Cancel", "act|restart-cancel"),
                }, ct);
            return;
        }

        if (action == "restart-cancel")
        {
            await this.EditIfPossibleAsync(update, "Restart cancelled — the workstream is unchanged.", buttons: null, ct);
            return;
        }

        // The workstream lives in its product's DB, not the ambient (header-less)
        // one this background scope resolved. Scope a fresh IOperatorActionService
        // to the notification's product via the AsyncLocal slug so it opens the
        // right per-product ProductDbContext (else the lookup returns "not found").
        OperatorActionOutcome outcome;
        using (ForgeActorScope.Push(ForgeActorScope.Current!.Value.Actor, product!.Id))
        using (ProductDbContextFactory.PushAsyncLocalSlug(product.Slug))
        using (var pscope = this.scopeFactory.CreateScope())
        {
            var ops = pscope.ServiceProvider.GetRequiredService<IOperatorActionService>();
            outcome = action switch
            {
                "retry" => await ops.RetryAsync(wsId, actor, ct),
                "accept" => await ops.AcceptAsync(wsId, actor, ct),
                "cancel" => await ops.CancelAsync(wsId, ct),
                "restart-confirm" => await ops.RestartAsync(wsId, ct),
                _ => OperatorActionOutcome.Problem(400, null, "Unknown action", null),
            };
        }

        if (outcome.Ok)
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
        await this.authorizer.RequireAsync(ForgeActorScope.Current?.Actor, ForgeCapabilities.HostAlertManage, new(), ct);
        var parts = (update.CallbackData ?? string.Empty).Split('|');
        if (parts.Length < 2) return;
        var kind = parts[0];
        var action = parts[1];

        var notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
            n => n.Platform == this.transport.Platform && n.ChatId == update.ChatId
                && n.ChatMessageId == update.ReplyToMessageId && n.Status == ChatNotificationStatuses.Sent, ct);
        if (notif is null || (kind == "hi" && notif.Kind != "human-interaction")
            || (kind == "strain" && notif.Kind != "queue-strain")) return;
        if (action != "snooze")
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

    private async Task HandleAnswerAsync(ChatInbound update, string? routeKey, ChatSubscriberRow subscriber, CancellationToken ct)
    {
        ChatNotificationRow? notif = null;
        if (!string.IsNullOrEmpty(update.ReplyToMessageId))
        {
            notif = await this.control.ChatNotifications.FirstOrDefaultAsync(
                n => n.Platform == this.transport.Platform && n.ChatId == update.ChatId
                && n.ChatMessageId == update.ReplyToMessageId && n.Status == ChatNotificationStatuses.Sent, ct);
        }

        // Channel-route fallback: a plain (non-reply) message typed inside a
        // spawn's forum topic targets that spawn's outstanding open-questions
        // alert — the topic IS the spawn's channel, so no reply is needed.
        if (notif is null && ForgeChatRoutes.TryParseSpawn(routeKey, out var routeProductId, out var spawnId))
        {
            var candidates = await this.control.ChatNotifications
                .Where(n => n.Kind == "open-questions"
                    && n.Platform == this.transport.Platform && n.ChatId == update.ChatId
                    && n.ProductId == routeProductId && n.SpawnId == spawnId
                    && n.Status == ChatNotificationStatuses.Sent)
                .ToListAsync(ct);
            // Client-side order — SQLite can't ORDER BY DateTimeOffset.
            notif = candidates.OrderByDescending(n => n.CreatedAt).FirstOrDefault();
        }

        if (notif is null || notif.Kind != "open-questions" || string.IsNullOrEmpty(notif.WorkItemId))
        {
            return; // uncorrelatable — ignore quietly.
        }

        var actor = subscriber.ForgeUserId ?? $"chat:{update.FromId}";

        // Questions + the answer/resume core live in the notification's product
        // DB — scope a fresh provider to it via the AsyncLocal slug (the same
        // idiom the workstream-action path uses).
        await this.authorizer.RequireAsync(ForgeActorScope.Current?.Actor, ForgeCapabilities.QuestionAnswer,
            new(notif.ProductId, SpawnId: notif.SpawnId, WorkItemId: notif.WorkItemId), ct);
        var product = await this.authorizer.ResolveProductAsync(notif.ProductId, ct);
        using (ForgeActorScope.Push(ForgeActorScope.Current!.Value.Actor, product!.Id))
        using (ProductDbContextFactory.PushAsyncLocalSlug(product.Slug))
        using (var pscope = this.scopeFactory.CreateScope())
        {
            var db = pscope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var openQuestions = pscope.ServiceProvider.GetRequiredService<IOpenQuestionService>();

            // Button tap → callback data `oq|{externalId}|{choiceIndex}`.
            if (!string.IsNullOrEmpty(update.CallbackData))
            {
                var answer = await ResolveChoiceAnswerAsync(db, notif.WorkItemId!, update.CallbackData, ct);
                if (answer is null) return;
                await this.AnswerAndEditAsync(openQuestions, notif, answer.Value.ExternalId, answer.Value.Answer, actor, ct);
                return;
            }

            // Free-text reply → answers the single unanswered question, when unambiguous.
            var unanswered = await db.DeliverableQuestions
                .Where(q => q.WorkItemId == notif.WorkItemId && (q.Answer == null || q.Answer == ""))
                .ToListAsync(ct);
            var distinct = unanswered
                .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
            if (distinct.Count == 1 && !string.IsNullOrWhiteSpace(update.Text))
            {
                await this.AnswerAndEditAsync(openQuestions, notif, distinct[0], update.Text!.Trim(), actor, ct);
            }
            else if (distinct.Count > 1)
            {
                await this.transport.SendAsync(update.ChatId,
                    "This item has multiple open questions — tap a choice button, or answer in the Forge UI.", buttons: null, ct);
            }
        }
    }

    private static async Task<(string ExternalId, string Answer)?> ResolveChoiceAnswerAsync(ProductDbContext db, string workItemId, string callbackData, CancellationToken ct)
    {
        var parts = callbackData.Split('|');
        if (parts.Length != 3 || parts[0] != OpenQuestionCallbackPrefix) return null;
        var externalId = parts[1];
        if (!int.TryParse(parts[2], out var choiceIdx)) return null;

        var row = await db.DeliverableQuestions
            .Where(q => q.WorkItemId == workItemId && q.ExternalId == externalId)
            .OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var choices = ChatChoiceParser.Parse(row.ChoicesJson);
        if (choiceIdx < 0 || choiceIdx >= choices.Count) return null;
        return (externalId, choices[choiceIdx]);
    }

    private async Task AnswerAndEditAsync(IOpenQuestionService openQuestions, ChatNotificationRow notif, string externalId, string answer, string actor, CancellationToken ct)
    {
        await openQuestions.AnswerAsync(
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
