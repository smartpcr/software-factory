// -----------------------------------------------------------------------
// <copyright file="ChatEventService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Atomic logical event and immutable fan-out; producers never perform transport I/O.</summary>
public sealed class ChatEventService(ControlDbContext control, IProductDbContextFactory products, ChatScopeResolver resolver,
    ChatContentPolicy policy, ChatRuntimeState runtime, ChatDbBudget budget, TimeProvider clock, IOptions<ChatPluginOptions> options,
    WorkstreamPluginRegistry plugins, ChatSecretResolver secrets)
{
    public async Task<bool> EnqueueAsync(ChatEventInput input, CancellationToken ct = default)
    {
        using var limits = ChatDbBudget.Limit(control);
        await runtime.EnqueueGate.WaitAsync(ct);
        try
        {
            var dedupe = "event:" + ChatEventCatalog.Hash(JsonSerializer.Serialize(new { input.Kind, input.ProductId, input.SpawnId, input.DedupeKey }));
            if (await control.ChatNotifications.AnyAsync(n => n.Platform == "plugins" && n.DedupeKey == dedupe, ct)) return false;
            var owned = await resolver.ResolveAsync(input, ct);
            var spawnScope = owned.Spawn is not null;
            var scope = spawnScope ? ChatConfigurationScope.Spawn(owned.Product!.Id, owned.Spawn!.Id) : ChatConfigurationScope.Host(options.Value.HostId);
            var now = clock.GetUtcNow();
            var credentials = new List<string>();
            var includeQuestionText = policy.AllowsQuestions(scope.ProductId);
            if (includeQuestionText)
            {
                try
                {
                    foreach (var connection in await control.ChatConnections.AsNoTracking().Where(c => c.DeletedAt == null).ToListAsync(ct))
                        credentials.AddRange(await secrets.ValuesAsync(connection, ct));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
                { includeQuestionText = false; } // Persist generated metadata when the redaction inputs cannot be verified.
            }
            var notification = new ChatNotificationRow
            {
                Id = Guid.NewGuid().ToString("N"), Platform = "plugins", ScopeKind = scope.Kind, ProductId = scope.ProductId ?? "",
                SpawnId = scope.SpawnId, HostId = scope.HostId, RepoId = owned.Spawn?.RepoId,
                WorkItemId = owned.WorkItemId, WorkstreamId = owned.WorkstreamId, ArtifactKey = owned.ArtifactKey,
                Kind = input.Kind, CatalogVersion = 1, PayloadVersion = 1, DomainRevision = input.Revision,
                DedupeKey = dedupe, CreatedAt = now, ExpiresAt = now.AddDays(7), Status = "pending", RouteState = "unrouted",
                Title = "Forge notification", Body = "Open Forge to review the current status.",
            };
            var questions = new List<ChatQuestionPayload>();
            if (spawnScope && input.Kind == "open-questions" && owned.WorkItemId is not null)
            {
                var rows = await products.CreateFor(owned.Product!.Slug).DeliverableQuestions.AsNoTracking().Where(q => q.WorkItemId == owned.WorkItemId).ToListAsync(ct);
                questions.AddRange(rows.GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First()).Where(q => string.IsNullOrEmpty(q.Answer))
                    .Select(q => new ChatQuestionPayload(q.ExternalId, q.Id, includeQuestionText ? ChatContentPolicy.Redact(q.Text[..Math.Min(q.Text.Length, 2000)], credentials) : null)));
            }
            notification.PayloadJson = JsonSerializer.Serialize(new ChatEventPayload(1, notification.Title, notification.Body, questions, [], owned.Spawn?.StoryId));
            var subscriptions = await control.ChatSubscriptions.Where(s => s.ScopeKey == scope.Key && s.RetiredAt == null).ToListAsync(ct);
            var parent = spawnScope ? await control.SpawnPlugins.SingleOrDefaultAsync(p => p.ProductId == scope.ProductId && p.SpawnId == scope.SpawnId && p.PluginId == "chat", ct) : null;
            notification.RouteReason = !ChatEventCatalog.Known(input.Kind) ? "unsupported-kind"
                : owned.Degraded ? "ownership-unverified"
                : !options.Value.PluginsEnabled || !plugins.IsAvailable || spawnScope && (parent is null || !parent.Enabled || parent.RetiredAt is not null
                    || parent.RunIdentity != ChatConfigurationService.RunIdentity(owned.Spawn!) || parent.SettingsVersion != 1) ? "disabled"
                : subscriptions.Count == 0 ? owned.Reason ?? "no-destination" : "filtered";
            await using var tx = await control.Database.BeginTransactionAsync(ct);
            control.ChatNotifications.Add(notification);
            if (notification.RouteReason is "filtered" or "no-destination" or "unresolved-scope")
            {
                foreach (var sub in subscriptions.Where(s => !s.Paused && ChatEventCatalog.Matches(s, input.Kind)))
                {
                    if (spawnScope && sub.ActivationGeneration != parent!.ActivationGeneration) continue;
                    var destination = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == sub.DestinationId && d.DeletedAt == null, ct);
                    if (destination is null) continue;
                    var connection = await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == destination.ConnectionId && c.DeletedAt == null && c.Enabled, ct);
                    if (connection is null || spawnScope && !await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connection.Id && g.ProductId == scope.ProductId, ct)) continue;
                    var binding = await control.ChatConversationBindings.SingleOrDefaultAsync(b => b.SubscriptionId == sub.Id && b.SubscriptionRevision == sub.Revision && b.RetiredAt == null, ct);
                    if (binding is null)
                    {
                        binding = new() { Id = Guid.NewGuid().ToString("N"), SubscriptionId = sub.Id, SubscriptionRevision = sub.Revision,
                            ActivationGeneration = sub.ActivationGeneration, ConnectionId = connection.Id, ExternalTenantId = destination.ExternalTenantId, ChannelId = destination.ChannelId };
                        control.ChatConversationBindings.Add(binding);
                    }
                    var target = new ChatDeliveryTarget(1, scope.Kind, scope.ProductId, scope.SpawnId, scope.HostId, parent?.RunIdentity,
                        sub.ActivationGeneration, sub.Id, sub.Revision, destination.Id, destination.Revision, connection.Id, connection.Revision,
                        connection.ProviderId, destination.ExternalTenantId, destination.ChannelId, sub.ConversationPolicy, sub.Interactive);
                    var delivery = new ChatDeliveryRow { NotificationId = notification.Id, SubscriptionId = sub.Id, BindingId = binding.Id,
                        ConnectionId = connection.Id, ChannelId = destination.ChannelId, TargetJson = JsonSerializer.Serialize(target),
                        CreatedAt = now.ToUnixTimeMilliseconds(), NextAttemptAt = now.ToUnixTimeMilliseconds(), DomainRevision = input.Revision,
                        Actionable = sub.Interactive && ChatEventCatalog.SpawnDefaults.Contains(input.Kind) };
                    control.ChatDeliveries.Add(delivery);
                    if (questions.Count == 0)
                    {
                        var expectedRevision = spawnScope ? await ChatDomainRevision.ReadAsync(products.CreateFor(owned.Product!.Slug),
                            new(SpawnId: owned.Spawn!.Id, WorkstreamId: owned.WorkstreamId, ArtifactKey: owned.ArtifactKey), ct) : 0;
                        control.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id, ExpectedRevision = expectedRevision });
                    }
                    else foreach (var question in questions) control.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id,
                        PartKey = "question:" + ChatEventCatalog.Hash(question.Id), QuestionId = question.Id, ExpectedRevision = question.Revision });
                    notification.RouteState = "routed"; notification.RouteReason = null;
                }
            }
            // Supersession is per exact domain target; old retries never restore stale controls.
            if (spawnScope && (owned.WorkItemId is not null || owned.WorkstreamId is not null))
            {
                var older = await control.ChatNotifications.Where(n => n.Platform == "plugins" && n.ProductId == notification.ProductId
                    && n.SpawnId == notification.SpawnId && n.Kind == input.Kind && n.WorkItemId == notification.WorkItemId
                    && n.WorkstreamId == notification.WorkstreamId && n.DomainRevision <= input.Revision && n.Status == "pending").ToListAsync(ct);
                foreach (var old in older) { old.Status = "superseded"; old.RespondedAt = now; }
            }
            await budget.SaveAsync(control, ct); await tx.CommitAsync(ct);
            runtime.Signal();
            return true;
        }
        finally { runtime.EnqueueGate.Release(); }
    }
}