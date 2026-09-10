// -----------------------------------------------------------------------
// <copyright file="ChatInboxService.Interactions.cs" company="Microsoft Corp.">
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
    private sealed record PreparedInput(ChatInboundEnvelope Envelope, string? Result = null);

    private async Task<PreparedInput> PrepareInteractionAsync(ChatInboxRow row, ChatInboundEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Kind == "ignored") return new(envelope, "ignored");
        if (envelope.Kind == "rejected") return new(envelope, "safe-replacement-required");
        using var scope = scopes.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<ChatLinkService>();
        if (envelope.Command is "start" or "link")
        {
            if (row.Result == "linked" && await links.ResolveAsync(envelope.ProviderId, envelope.Address.ExternalTenantId, envelope.SenderId, ct) is not null) return new(envelope, "linked");
            if (envelope.CommandArgument is not { Length: 71 } hash || !hash.StartsWith("sha256:", StringComparison.Ordinal))
                return new(envelope, "link-instructions");
            return new(envelope, await links.RedeemHashAsync(hash[7..], envelope.ConnectionId, envelope.Address.ExternalTenantId, envelope.SenderId, ct, row.Id) is null
                ? "link-invalid-or-expired" : "linked");
        }
        if (envelope.Command == "unlink")
        {
            var linked = await links.ResolveAsync(envelope.ProviderId, envelope.Address.ExternalTenantId, envelope.SenderId, ct);
            if (linked is null) return new(envelope, "link-required");
            await links.RevokeAsync(linked, ct); return new(envelope, "unlinked");
        }
        if (envelope.Kind == "unsupported") return new(envelope, "unsupported-input");
        if (envelope.Command?.StartsWith("legacy-", StringComparison.Ordinal) == true) return new(envelope);
        if (envelope.Command == "answer") return new(envelope with { ReplyToMessageId = envelope.CommandArgument });
        if (envelope.Command is "status" or "pause" or "resume" or "questions")
        {
            var bindings = await this.BindingsAsync(envelope.Address, ct);
            var live = bindings.Where(b => b.RetiredAt is null).ToArray();
            if (live.Length == 0) return new(envelope, "no-bound-scope");
            var actor = await links.ResolveAsync(envelope.ProviderId, envelope.Address.ExternalTenantId, envelope.SenderId, ct);
            var allowed = new List<(ChatSubscriptionRow Sub, ChatDeliveryTarget Target)>();
            foreach (var binding in live)
            {
                var sub = await control.ChatSubscriptions.AsNoTracking().SingleAsync(s => s.Id == binding.SubscriptionId, ct);
                if (sub.ScopeKind != "spawn") continue;
                if (!string.IsNullOrEmpty(envelope.CommandArgument) && envelope.CommandArgument != sub.ProductId + ":" + sub.SpawnId) continue;
                var destination = await control.ChatDestinations.AsNoTracking().SingleAsync(d => d.Id == sub.DestinationId, ct);
                var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == binding.ConnectionId, ct);
                var plugin = await control.SpawnPlugins.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == sub.ProductId && p.SpawnId == sub.SpawnId && p.PluginId == "chat", ct);
                var target = new ChatDeliveryTarget(1, sub.ScopeKind, sub.ProductId, sub.SpawnId, null, plugin?.RunIdentity, sub.ActivationGeneration,
                    sub.Id, sub.Revision, destination.Id, destination.Revision, connection.Id, connection.Revision, connection.ProviderId,
                    destination.ExternalTenantId, destination.ChannelId, sub.ConversationPolicy, sub.Interactive);
                var sampleKind = JsonSerializer.Deserialize<string[]>(sub.EventKindsJson)?.FirstOrDefault() ?? "open-questions";
                if (await scope.ServiceProvider.GetRequiredService<ChatScopeResolver>().ValidateAsync(target,
                    new() { Kind = sampleKind, ProductId = sub.ProductId!, SpawnId = sub.SpawnId, ScopeKind = "spawn" }, ct, action: true) is not null) continue;
                try { await scope.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>().RequireAsync(actor, ForgeCapabilities.SpawnRead, new(sub.ProductId, sub.SpawnId), ct); }
                catch (ForgeAccessDeniedException) { continue; }
                allowed.Add((sub, target));
            }
            if (allowed.Count == 0) return new(envelope, actor is null ? "link-required" : "scope-off-paused-or-denied");
            row.ContentScopesJson = JsonSerializer.Serialize(allowed.Select(a => new { a.Sub.ProductId, a.Sub.SpawnId }));
            if (allowed.Count > 1)
            {
                row.ResponseText = "Select a workstream by adding product:spawn to the command: " + string.Join(", ", allowed.Select(a => a.Sub.ProductId + ":" + a.Sub.SpawnId));
                return new(envelope, "select-one-scope");
            }
            var selected = allowed[0].Sub;
            if (envelope.Command == "questions")
            {
                row.ResponseText = await this.QuestionListAsync(row, envelope, ct, selected.Id); return new(envelope, "questions-listed");
            }
            if (envelope.Command == "status")
            {
                var status = JsonSerializer.SerializeToElement(await scope.ServiceProvider.GetRequiredService<ChatStatusService>().SpawnAsync(actor, selected.ProductId!, selected.SpawnId!, ct));
                row.ResponseText = $"Workstream {selected.ProductId}:{selected.SpawnId}\nPhase: {status.GetProperty("Phase").GetString()}\nPending questions: {status.GetProperty("PendingQuestionCount").GetInt32()}\nChildren: {status.GetProperty("ChildCounts").GetRawText()}";
                return new(envelope, "status");
            }
            var capability = "spawn." + envelope.Command;
            await scope.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>().RequireAsync(actor, capability, new(selected.ProductId, selected.SpawnId), ct);
            var product = await control.Products.AsNoTracking().SingleAsync(p => p.Id == selected.ProductId, ct);
            using var productScope = ProductDbContextFactory.PushAsyncLocalSlug(product.Slug);
            using var actorScope = ForgeActorScope.Push(actor!, product.Id);
            using var domain = scopes.CreateScope();
            var db = domain.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().SingleAsync(s => s.Id == selected.SpawnId, ct);
            // Persist the expected revision before invoking a non-transactional domain core. Redelivery uses the same intent.
            var revision = row.CommandRevision is { } previous ? previous
                : await ChatDomainRevision.ReadAsync(db, new(SpawnId: spawn.Id), ct);
            row.CommandRevision = revision;
            await budget.SaveAsync(control, ct);
            var prior = await db.ChatActionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ProductId == product.Id && r.Action == capability && r.TargetId == spawn.StoryId && r.ExpectedRevision == revision, ct);
            if (prior is not null)
            {
                row.ActionReceiptId = prior.Id;
                return new(envelope, prior.State != "completed" ? "operation-needs-repair" : prior.Result != "accepted" ? "action-conflict" : envelope.Command == "pause" ? "parent-paused" : "parent-resumed");
            }
            using var intent = ChatActionIntent.Push(revision);
            var stories = domain.ServiceProvider.GetRequiredService<StoryOperatorActionService>();
            var result = envelope.Command == "pause" ? await stories.PauseSpawnAsync(spawn.StoryId, ct) : await stories.ResumeSpawnAsync(spawn.StoryId, ct);
            row.ActionReceiptId = await db.ChatActionReceipts.Where(r => r.ProductId == product.Id && r.Action == capability && r.TargetId == spawn.StoryId && r.ExpectedRevision == revision).Select(r => r.Id).SingleOrDefaultAsync(ct);
            return new(envelope, result is IStatusCodeHttpResult { StatusCode: >= 400 } ? "action-conflict" : envelope.Command == "pause" ? "parent-paused" : "parent-resumed");
        }
        if (envelope.Command is not null) return new(envelope, "unknown-command");
        if (envelope.ActionToken is null && envelope.ReplyToMessageId is null && !string.IsNullOrWhiteSpace(envelope.Text))
        {
            var questions = await this.ActiveQuestionsAsync(envelope, ct);
            if (questions.Limited) return new(envelope, "too-many-questions");
            if (questions.Items.Count != 1)
            {
                if (questions.Items.Count > 1) row.ResponseText = await this.QuestionListAsync(row, envelope, ct);
                return new(envelope, questions.Items.Count == 0 ? "unmatched-message" : "select-one-question");
            }
            return new(envelope with { ReplyToMessageId = questions.Items[0].MessageId });
        }
        return new(envelope);
    }

    private async Task<List<ChatConversationBindingRow>> BindingsAsync(ChatAddress address, CancellationToken ct)
    {
        var bindings = await control.ChatConversationBindings.AsNoTracking().Where(b => b.ConnectionId == address.ConnectionId && b.ExternalTenantId == address.ExternalTenantId
            && b.ChannelId == address.ChannelId && b.ConversationId == address.ConversationId).ToListAsync(ct);
        if (address.ConversationId is null)
        {
            // A configured flat destination is usable before the first notification creates a delivery binding.
            var flat = await control.ChatSubscriptions.AsNoTracking().Where(s => s.ConversationPolicy == "flat" && s.RetiredAt == null
                && control.ChatDestinations.Any(d => d.Id == s.DestinationId && d.ConnectionId == address.ConnectionId && d.ExternalTenantId == address.ExternalTenantId && d.ChannelId == address.ChannelId && d.DeletedAt == null)).ToListAsync(ct);
            foreach (var sub in flat.Where(s => !bindings.Any(b => b.SubscriptionId == s.Id && b.RetiredAt is null)))
                bindings.Add(new() { SubscriptionId = sub.Id, SubscriptionRevision = sub.Revision, ConnectionId = address.ConnectionId,
                    ExternalTenantId = address.ExternalTenantId, ChannelId = address.ChannelId, ActivationGeneration = sub.ActivationGeneration });
        }
        return bindings;
    }

    private sealed record QuestionCandidates(List<ChatDeliveryMessageRow> Items, bool Limited = false);
    private async Task<QuestionCandidates> ActiveQuestionsAsync(ChatInboundEnvelope envelope, CancellationToken ct)
    {
        var addressJson = JsonSerializer.Serialize(envelope.Address);
        var candidates = await control.ChatDeliveryMessages.AsNoTracking().Where(m => m.State == "sent" && !m.ControlsRetired && m.QuestionId != null
            && m.AddressJson == addressJson && m.ActionKind != "question.prompt" && control.ChatDeliveries.Any(d => d.Id == m.DeliveryId && d.ConnectionId == envelope.ConnectionId && d.ChannelId == envelope.Address.ChannelId))
            .OrderBy(m => m.Id).Take(201).ToListAsync(ct);
        if (candidates.Count == 201) return new([], true); // Never infer a unique question from a truncated scan.
        using var scope = scopes.CreateScope();
        var actor = await scope.ServiceProvider.GetRequiredService<ChatLinkService>().ResolveAsync(envelope.ProviderId, envelope.Address.ExternalTenantId, envelope.SenderId, ct);
        var result = new List<ChatDeliveryMessageRow>();
        foreach (var message in candidates.Where(m => JsonSerializer.Deserialize<ChatAddress>(m.AddressJson) == envelope.Address))
        {
            var delivery = await control.ChatDeliveries.AsNoTracking().SingleAsync(d => d.Id == message.DeliveryId, ct);
            var notification = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == delivery.NotificationId, ct);
            var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
            if (notification.ExpiresAt <= clock.GetUtcNow() || notification.Status is "expired" or "resolved" or "superseded"
                || await scope.ServiceProvider.GetRequiredService<ChatScopeResolver>().ValidateAsync(target, notification, ct, action: true) is not null) continue;
            try { await scope.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>().RequireAsync(actor, ForgeCapabilities.QuestionAnswer,
                new(target.ProductId, target.SpawnId, WorkItemId: notification.WorkItemId), ct); }
            catch (ForgeAccessDeniedException) { continue; }
            if (!result.Any(m => m.DeliveryId == message.DeliveryId && m.QuestionId == message.QuestionId && m.ExpectedRevision == message.ExpectedRevision)) result.Add(message);
        }
        return new(result);
    }

    private async Task<string> QuestionListAsync(ChatInboxRow row, ChatInboundEnvelope envelope, CancellationToken ct, string? subscriptionId = null)
    {
        var candidates = await this.ActiveQuestionsAsync(envelope, ct);
        if (candidates.Limited) return "There are too many question messages to list here. Reply to a specific question message, or select it in Forge.";
        var questions = candidates.Items;
        var deliveryIds = questions.Select(q => q.DeliveryId).ToList();
        var deliveries = await control.ChatDeliveries.AsNoTracking().Where(d => deliveryIds.Contains(d.Id) && (subscriptionId == null || d.SubscriptionId == subscriptionId)).ToListAsync(ct);
        questions = questions.Where(q => deliveries.Any(d => d.Id == q.DeliveryId)).ToList();
        row.ContentScopesJson = JsonSerializer.Serialize(deliveries.Select(d => JsonSerializer.Deserialize<ChatDeliveryTarget>(d.TargetJson)!).Select(t => new { t.ProductId, t.SpawnId }).Distinct());
        return questions.Count == 0 ? "No open question messages in this conversation. Open Forge for the current status."
            : "Reply to the specific question message, or use the answer command with its message ID and your answer.\n"
                + string.Join("\n", questions.Take(20).Select(q => $"Message {q.MessageId}: question {q.QuestionId}"))
                + (questions.Count > 20 ? "\nMore questions are available in Forge." : "");
    }

    private async Task QueueResponseAsync(ChatInboxRow row, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<ChatInboundEnvelope>(row.EnvelopeJson ?? "null");
        if (envelope is null || envelope.Kind == "ignored" || row.Result is "ignored" or "answer-prompt-queued" || row.ResponseState != "none") return;
        if (envelope.Command is not ("start" or "link" or "unlink") && (await this.BindingsAsync(envelope.Address, ct)).Count == 0) return;
        row.ResponseText ??= row.Result switch
        {
            "linked" => "Linked to your Forge operator identity. Use status or questions in a configured workstream conversation.",
            "unlinked" => "Your chat identity links were revoked. Link again from Forge to restore access.",
            "link-instructions" => $"Open Forge while signed in and request a chat identity link for connection {envelope.ConnectionId}, bot {envelope.Address.ExternalTenantId}, sender {envelope.SenderId}. Send the challenge with the start command.",
            "link-invalid-or-expired" => "This link challenge is expired, already used, or belongs to another connection or sender. Request a new challenge in Forge.",
            "link-required" or "not-authorized" => "Link your enrolled Forge identity and check your workstream access in Forge.",
            "answered-all" => "Answer saved. All questions in this request are answered.",
            "answered-more-remain" => "Answer saved. Other questions remain open; their controls are still available.",
            "parent-paused" => "The parent workstream and its active children are paused. Resume restores their prior state.",
            "parent-resumed" => "The parent workstream is resumed.",
            "accepted" => "Action accepted. Open Forge for current status.",
            "confirmation-queued" => "Review the separate restart confirmation before proceeding.",
            "operation-needs-repair" => "A previous operation has an unknown outcome. Open Forge to inspect and repair it before retrying.",
            "safe-replacement-required" => "The answer contains unsupported or sensitive content. Send a safe replacement, or answer in Forge.",
            "unsupported-input" => "This conversation accepts text answers and Forge buttons. Send a text reply to a question.",
            "answer-in-forge" => "This destination permits metadata only. Answer in Forge.",
            "select-one-question" => "Several questions can match. Reply to a specific question message or use the questions command.",
            "too-many-questions" => "There are too many question messages to select automatically. Reply to a specific question message or select it in Forge.",
            "legacy-alias-repair-required" => "This old message has no unambiguous current link. Open Forge to repair the migrated message or use a current question message.",
            "status" or "questions-listed" or "select-one-scope" => "Open Forge for current status.",
            "unknown-command" => "Supported commands: start, unlink, status, questions, answer, pause, resume. In a shared flat chat, add product:spawn to scope status, pause or resume.",
            _ => "This request is no longer available or this scope is off, paused, or inaccessible. Open Forge for the current status.",
        };
        var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == row.ConnectionId, ct);
        row.ResponseText = ChatContentPolicy.Redact(row.ResponseText, await secrets.ValuesAsync(connection, ct));
        row.ResponseState = "pending";
    }
}