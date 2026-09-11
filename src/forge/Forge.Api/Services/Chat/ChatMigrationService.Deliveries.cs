// -----------------------------------------------------------------------
// <copyright file="ChatMigrationService.Deliveries.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatMigrationService
{
    private async Task<string?> BackfillAsync(ChatNotificationRow notification, ChatConnectionRow connection, ChatDestinationRow destination,
        ChatSubscriptionRow subscription, ChatConversationBindingRow binding, SpawnPluginRow? parent, ChatOwnedScope owned,
        bool activate, string? reason, string? originalThread, CancellationToken ct)
    {
        var key = Key("notification", notification.Id);
        var item = await control.ChatMigrationItems.FindAsync([key], ct);
        if (item is null)
        {
            var previousDelivery = await control.ChatDeliveries.Where(d => d.NotificationId == notification.Id && d.SubscriptionId == subscription.Id)
                .Select(d => d.Id).SingleOrDefaultAsync(ct);
            item = new() { Key = key, Kind = "notification", SourceId = notification.Id, TargetId = previousDelivery ?? Key("delivery", notification.Id) };
            control.ChatMigrationItems.Add(item);
        }
        // Refresh this snapshot up to the final handoff: the legacy pump may send/resolve after the draft import.
        item.SnapshotJson = JsonSerializer.Serialize(new ChatLegacyNotificationSnapshot(notification.Platform, notification.ScopeKind,
            notification.DedupeKey, notification.RouteState, notification.RouteReason, notification.Status, notification.ProductId,
            notification.SpawnId, notification.WorkstreamId, notification.ArtifactKey));
        var delivery = await control.ChatDeliveries.FindAsync([item.TargetId], ct);
        if (delivery is null) { delivery = new() { Id = item.TargetId, NotificationId = notification.Id }; control.ChatDeliveries.Add(delivery); }
        var terminal = notification.Status is "answered" or "acted" or "expired" or "superseded" or "resolved" or "cancelled";
        delivery.SubscriptionId = subscription.Id; delivery.BindingId = binding.Id; delivery.ConnectionId = connection.Id;
        delivery.ChannelId = destination.ChannelId; delivery.CreatedAt = notification.CreatedAt.ToUnixTimeMilliseconds();
        delivery.Attempts = notification.Attempts; delivery.NextAttemptAt = delivery.CreatedAt;
        delivery.State = notification.Status is "sending" or "uncertain" ? "uncertain" : notification.Status == "resolving" ? "resolved" : notification.Status;
        delivery.TerminalAt = terminal || notification.Status == "resolving" ? notification.RespondedAt?.ToUnixTimeMilliseconds() : null;
        delivery.LastError = reason ?? (notification.LastError is not null ? "legacy-delivery-failed" : null);
        delivery.Actionable = !terminal && notification.Status != "resolving" && subscription.Interactive && reason is null;
        var revision = owned.Spawn is not null ? await ChatDomainRevision.ReadAsync(products.CreateFor(owned.Product!.Slug),
            new(SpawnId: owned.Spawn.Id, WorkstreamId: owned.WorkstreamId, ArtifactKey: owned.ArtifactKey), ct) : 0;
        delivery.DomainRevision = revision;
        delivery.TargetJson = JsonSerializer.Serialize(new ChatDeliveryTarget(1, subscription.ScopeKind, subscription.ProductId, subscription.SpawnId,
            subscription.HostId, parent?.RunIdentity, subscription.ActivationGeneration, subscription.Id, subscription.Revision,
            destination.Id, destination.Revision, connection.Id, connection.Revision, connection.ProviderId,
            destination.ExternalTenantId, destination.ChannelId, subscription.ConversationPolicy, subscription.Interactive));
        var address = new ChatAddress(connection.Id, connection.InstallationId, destination.ChannelId, originalThread ?? binding.ConversationId);
        var partId = Key("message", notification.Id);
        var part = await control.ChatDeliveryMessages.FindAsync([partId], ct);
        part ??= await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id && m.MessageId == notification.ChatMessageId).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (part is not null) partId = part.Id;
        var boundRevision = part?.MessageId is not null && part.MessageId == notification.ChatMessageId ? part.ExpectedRevision : (long?)null;
        string? aliasIssue = null;
        if (part is null) { part = new() { Id = partId, DeliveryId = delivery.Id }; control.ChatDeliveryMessages.Add(part); }
        part.PartKey = notification.ChatMessageId is null ? "summary" : "legacy:" + notification.Id;
        part.MessageId = notification.ChatMessageId; part.AddressJson = JsonSerializer.Serialize(address);
        part.State = notification.ChatMessageId is not null ? "sent" : terminal ? "cancelled" : delivery.State == "uncertain" ? "uncertain" : "pending";
        part.ExpectedRevision = boundRevision ?? revision; part.Attempts = notification.Attempts;
        if (notification.Kind == "workstream-stalled" && part.MessageId is not null && owned.WorkstreamId is not null)
        {
            var child = await products.CreateFor(owned.Product!.Slug).Workstreams.AsNoTracking().SingleAsync(w => w.Id == owned.WorkstreamId, ct);
            var lastActive = child.LastActive is > 0 and < 1000000000 ? child.LastActive + 1704067200 : child.LastActive;
            if (part.ExpectedRevision != revision || child.Status != "stalled" || lastActive > notification.CreatedAt.ToUnixTimeSeconds())
                aliasIssue = "legacy-control-revision-unverified";
        }
        part.ControlsRetired = terminal || notification.Status == "resolving" || reason is not null;
        part.EditState = notification.Status == "resolving" && part.MessageId is not null ? "pending" : "none";
        part.TerminalAt = notification.RespondedAt?.ToUnixTimeMilliseconds();
        part.ExpiresAt = notification.ExpiresAt?.ToUnixTimeMilliseconds();
        if (reason is not null)
        {
            // Preserve the source lifecycle, but hold delivery for an explicit repair instead of guessing a recipient/topic.
            if (!terminal && notification.Status != "resolving" && delivery.State != "uncertain") delivery.State = "blocked";
            part.State = part.MessageId is null ? "blocked" : part.State;
            part.EditState = "none";
        }
        part.QuestionId = null;
        foreach (var extra in await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id && m.Id != partId && m.MessageId == null).ToListAsync(ct))
        { extra.State = "cancelled"; extra.ControlsRetired = true; }
        // No rendered body is copied into the migration journal; the existing legacy row remains the rollback source.
        if (activate)
        {
            var dedupeSource = ChatMigrationRouting.DedupeSource(subscription.ScopeKey, notification.Kind, notification.DedupeKey);
            var dedupeId = Key("dedupe", dedupeSource);
            var dedupe = await control.ChatMigrationItems.FindAsync([dedupeId], ct);
            if (!terminal)
            {
                if (dedupe is null) control.ChatMigrationItems.Add(new() { Key = dedupeId, Kind = "dedupe", SourceId = dedupeSource, TargetId = notification.Id });
                else dedupe.TargetId = notification.Id;
            }
            var questions = new List<ChatQuestionPayload>();
            if (owned.WorkItemId is not null && owned.Product is not null)
            {
                var known = await products.CreateFor(owned.Product.Slug).DeliverableQuestions.AsNoTracking().Where(q => q.WorkItemId == owned.WorkItemId).ToListAsync(ct);
                var credentials = await legacy.ReadAsync(legacy.Reference, ct) is { } value ? new[] { value } : [];
                foreach (var question in known.GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First()))
                {
                    // An old message must not acquire authority over a changed question merely because its ID was reused.
                    if (string.IsNullOrEmpty(question.Text) || !notification.Body.Contains(question.Text, StringComparison.Ordinal)
                        || notification.ChatMessageId is not null && known.Count(q => q.ExternalId == question.ExternalId && q.Text == question.Text) != 1)
                    { aliasIssue = "legacy-question-revision-unverified"; continue; }
                    var choices = ChatContentPolicy.Choices(question.ChoicesJson, credentials);
                    var buttons = ChatActions.Parse(notification.ActionsJson) ?? [];
                    // Verify button labels as well as indices; an old choice must never acquire a replacement value.
                    var originalChoices = choices.Count > 0 && choices.Select((choice, index) => buttons.Any(b => b.CallbackData == $"oq|{question.ExternalId}|{index}"
                        && (b.Label == choice || b.Label == "★ " + choice))).All(match => match);
                    questions.Add(new(question.ExternalId, question.Id, policy.AllowsQuestions(owned.Product.Id) ? ChatContentPolicy.Redact(question.Text, credentials) : null,
                        policy.AllowsQuestions(owned.Product.Id) && originalChoices ? choices : []));
                }
            }
            if (questions.Count == 1) { part.QuestionId = questions[0].Id; part.ExpectedRevision = questions[0].Revision; }
            if (notification.ChatMessageId is null && notification.Status == "pending" && questions.Count > 1)
            {
                // There is no external legacy anchor yet. Use the core's per-question parts within this single delivery.
                for (var index = 0; index < questions.Count; index++)
                {
                    var question = questions[index]; var id = index == 0 ? part.Id : Key("question-message", notification.Id + ":" + question.Id);
                    var questionPart = index == 0 ? part : await control.ChatDeliveryMessages.FindAsync([id], ct);
                    if (questionPart is null) { questionPart = new() { Id = id, DeliveryId = delivery.Id }; control.ChatDeliveryMessages.Add(questionPart); }
                    questionPart.PartKey = "question:" + ChatEventCatalog.Hash(question.Id);
                    questionPart.QuestionId = question.Id; questionPart.ExpectedRevision = question.Revision;
                    questionPart.State = reason is null ? "pending" : "blocked"; questionPart.ControlsRetired = reason is not null;
                    questionPart.ExpiresAt = part.ExpiresAt; questionPart.AddressJson = part.AddressJson;
                }
            }
            notification.Platform = "plugins"; notification.DedupeKey = "legacy:" + notification.Id;
            notification.ScopeKind = subscription.ScopeKind; notification.ProductId = subscription.ProductId ?? "";
            notification.SpawnId = subscription.SpawnId; notification.WorkstreamId = owned.WorkstreamId; notification.ArtifactKey = owned.ArtifactKey;
            notification.HostId = subscription.HostId; notification.DomainRevision = revision;
            notification.PayloadVersion = 1; notification.CatalogVersion = 1;
            notification.PayloadJson = JsonSerializer.Serialize(new ChatEventPayload(1, "Forge notification", "Open Forge for the current status.", questions, [], owned.Spawn?.StoryId));
            notification.RouteState = reason is null ? "routed" : "unrouted"; notification.RouteReason = reason;
        }
        if (aliasIssue is not null)
        {
            delivery.LastError ??= aliasIssue;
            if (notification.Kind == "workstream-stalled" || ChatPayloadConverter.Read(notification)?.Questions.Count == 0) part.ControlsRetired = true;
        }
        await control.SaveChangesAsync(ct);
        return aliasIssue;
    }
}