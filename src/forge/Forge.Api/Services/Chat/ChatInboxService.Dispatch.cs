// -----------------------------------------------------------------------
// <copyright file="ChatInboxService.Dispatch.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatInboxService
{
    public async Task DispatchOnceAsync(CancellationToken ct = default)
    {
        using var limits = ChatDbBudget.Limit(control);
        await runtime.DispatchGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var rows = await control.ChatInbox.Where(i => (i.State == "pending" || i.State == "processing") && i.NextAttemptAt <= now)
                .OrderBy(i => i.ReceivedAt).Take(20).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.State = "processing"; row.OwnerProcessInstanceId = runtime.ProcessId; row.RecoveryDeadline = now + 60000; row.Attempts++;
                await budget.SaveAsync(control, ct);
                try
                {
                    row.Result = await this.DispatchAsync(row, ct); row.State = "processed"; row.TerminalAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                }
                catch (ForgeAccessDeniedException) { row.Result = "not-authorized"; row.State = "rejected"; row.TerminalAt = now; }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
                {
                    row.Result = "dispatch-unavailable"; row.State = row.Attempts >= 10 ? "dead-letter" : "pending";
                    row.NextAttemptAt = now + Math.Min(300000, 1000L << Math.Min(row.Attempts, 8));
                    if (row.State == "dead-letter") row.TerminalAt = now;
                }
                await budget.SaveAsync(control, ct);
            }
        }
        finally { runtime.DispatchGate.Release(); }
    }

    private async Task<string> DispatchAsync(ChatInboxRow row, CancellationToken ct)
    {
        if (row.EnvelopeJson is null) return "content-expired";
        var envelope = JsonSerializer.Deserialize<ChatInboundEnvelope>(row.EnvelopeJson);
        if (envelope is not { Version: 1 }) return "unsupported-envelope";
        ChatActionTokenRow? token = null;
        ChatDeliveryMessageRow? message = null;
        if (envelope.ActionToken is { Length: > 0 })
        {
            var hash = envelope.ActionToken.StartsWith("sha256:", StringComparison.Ordinal) ? envelope.ActionToken[7..] : ChatEventCatalog.Hash(envelope.ActionToken);
            token = await control.ChatActionTokens.SingleOrDefaultAsync(t => t.Hash == hash, ct);
            if (token is null || token.State != "active" || token.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds()) return "stale-action";
            message = await control.ChatDeliveryMessages.SingleOrDefaultAsync(m => m.Id == token.DeliveryMessageId, ct);
        }
        else if (envelope.ReplyToMessageId is not null)
        {
            var candidates = await control.ChatDeliveryMessages.AsNoTracking().Where(m => m.MessageId == envelope.ReplyToMessageId && !m.ControlsRetired).ToListAsync(ct);
            var exact = candidates.Where(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson) == envelope.Address && m.QuestionId is not null).ToArray();
            if (exact.Length != 1) return "select-one-question";
            message = exact[0];
        }
        if (message is null || message.State != "sent" || message.ControlsRetired
            || JsonSerializer.Deserialize<ChatAddress>(message.AddressJson) != envelope.Address) return "unmatched-message";
        var delivery = await control.ChatDeliveries.AsNoTracking().SingleAsync(d => d.Id == message.DeliveryId, ct);
        var notification = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == delivery.NotificationId, ct);
        if (notification.ExpiresAt <= clock.GetUtcNow() || notification.Status is "resolved" or "superseded" or "expired") return "stale-action";
        var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
        if (target.ConnectionId != envelope.ConnectionId || target.ProviderId != envelope.ProviderId) return "unmatched-connection";
        if (target.ScopeKind != "spawn") return "open-host-settings";
        using var validation = scopes.CreateScope();
        if (await validation.ServiceProvider.GetRequiredService<ChatScopeResolver>().ValidateAsync(target, notification, ct, action: true) is { } reason) return reason;
        var actor = await validation.ServiceProvider.GetRequiredService<ChatLinkService>().ResolveAsync(envelope.ProviderId, envelope.Address.ExternalTenantId, envelope.SenderId, ct);
        var action = token?.Action ?? "question.answer";
        var actionTarget = new ForgeActionTarget(target.ProductId, target.SpawnId,
            WorkstreamId: action.StartsWith("workstream.", StringComparison.Ordinal) ? notification.WorkstreamId : null,
            WorkItemId: action == "question.answer" ? notification.WorkItemId : null,
            ArtifactKey: action.StartsWith("artifact.", StringComparison.Ordinal) ? notification.ArtifactKey : null);
        await validation.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>().RequireAsync(actor, action, actionTarget, ct);
        if (token?.ActorId is { } boundActor && actor!.UserId != boundActor) return "confirmation-actor-mismatch";
        if (action.EndsWith(".restart", StringComparison.Ordinal) && token?.ActorId is null)
        {
            var key = "confirm:" + ChatEventCatalog.Hash(actor!.UserId + ":" + token!.ExpectedRevision);
            if (!await control.ChatDeliveryMessages.AnyAsync(m => m.DeliveryId == delivery.Id && m.PartKey == key, ct))
            {
                control.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id, PartKey = key, ActionKind = action, ConfirmationActorId = actor.UserId,
                    ExpectedRevision = token.ExpectedRevision, ExpiresAt = clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds() });
                var queued = await control.ChatDeliveries.SingleAsync(d => d.Id == delivery.Id, ct);
                queued.State = "pending"; queued.NextAttemptAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await budget.SaveAsync(control, ct); runtime.Signal();
            }
            return "confirmation-queued";
        }
        var product = await control.Products.AsNoTracking().SingleAsync(p => p.Id == target.ProductId, ct);
        using var productContext = ProductDbContextFactory.PushAsyncLocalSlug(product.Slug);
        using var actorContext = ForgeActorScope.Push(actor!, product.Id);
        using var domain = scopes.CreateScope();
        var db = domain.ServiceProvider.GetRequiredService<ProductDbContext>();
        var revision = token?.ExpectedRevision ?? message.ExpectedRevision;
        using var intent = ChatActionIntent.Push(revision);
        if (action == "question.answer")
        {
            var subscription = await control.ChatSubscriptions.AsNoTracking().SingleAsync(s => s.Id == delivery.SubscriptionId, ct);
            if (!policy.AllowsQuestions(product.Id, subscription.ContentMode)
                || ChatPayloadConverter.Read(notification)?.Questions.Any(q => q.Id == message.QuestionId && q.Text is not null) != true) return "answer-in-forge";
            if (string.IsNullOrWhiteSpace(envelope.Text)) return "answer-required";
            var latest = (await db.DeliverableQuestions.AsNoTracking().Where(q => q.WorkItemId == notification.WorkItemId).ToListAsync(ct))
                .Where(q => q.ExternalId.Equals(message.QuestionId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).FirstOrDefault();
            if (latest is null || latest.Id != revision) return "stale-revision";
            var result = await domain.ServiceProvider.GetRequiredService<IOpenQuestionService>().AnswerAsync(notification.WorkItemId!,
                [new(message.QuestionId!, envelope.Text, revision)], actor!.UserId, ct);
            var key = ChatEventCatalog.Hash($"question:{notification.WorkItemId}:{revision}");
            row.ActionReceiptId = await db.ChatActionReceipts.Where(r => r.ProductId == product.Id && r.ActionKey == key).Select(r => r.Id).SingleOrDefaultAsync(ct);
            return result.RevisionConflict ? "stale-revision" : result.AcceptedCount == 0 ? "already-resolved" : result.UnansweredCount == 0 ? "answered-all" : "answered-more-remain";
        }
        if (action.StartsWith("workstream.", StringComparison.Ordinal))
        {
            var operations = domain.ServiceProvider.GetRequiredService<IOperatorActionService>();
            var outcome = action switch
            {
                "workstream.retry" => await operations.RetryAsync(notification.WorkstreamId!, actor!.UserId, ct),
                "workstream.accept" => await operations.AcceptAsync(notification.WorkstreamId!, actor!.UserId, ct),
                "workstream.cancel" => await operations.CancelAsync(notification.WorkstreamId!, ct),
                "workstream.restart" => await operations.RestartAsync(notification.WorkstreamId!, ct),
                _ => null,
            };
            if (token?.ActorId is not null) token.State = "consumed";
            return outcome?.Ok == true ? "accepted" : "action-conflict";
        }
        var spawn = await db.StorySpawns.AsNoTracking().SingleAsync(s => s.Id == target.SpawnId, ct);
        var stories = domain.ServiceProvider.GetRequiredService<StoryOperatorActionService>();
        var resultValue = action switch
        {
            "spawn.pause" => await stories.PauseSpawnAsync(spawn.StoryId, ct), "spawn.resume" => await stories.ResumeSpawnAsync(spawn.StoryId, ct),
            "artifact.retry" => await stories.RetryArtifactAsync(spawn.StoryId, notification.ArtifactKey!, ct),
            "artifact.accept" => await stories.AcceptArtifactAsync(spawn.StoryId, notification.ArtifactKey!, ct),
            "artifact.cancel" => await stories.CancelArtifactAsync(spawn.StoryId, notification.ArtifactKey!, ct),
            "artifact.restart" => await stories.RestartArtifactAsync(spawn.StoryId, notification.ArtifactKey!, ct), _ => null,
        };
        if (token?.ActorId is not null) token.State = "consumed";
        return resultValue is IStatusCodeHttpResult { StatusCode: >= 400 } || resultValue is null ? "action-conflict" : "accepted";
    }
}