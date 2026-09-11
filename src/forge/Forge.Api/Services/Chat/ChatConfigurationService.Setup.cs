// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationService.Setup.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record ChatRouteDraft(string DestinationId, string ConversationPolicy, bool Interactive = true,
    string ContentMode = "metadata", IReadOnlyList<string>? EventKinds = null, bool Paused = false);
public sealed record ChatSetupWrite(long ExpectedRevision, bool Enabled, IReadOnlyList<ChatRouteDraft> Destinations, bool ActivateNewRun = false);
public sealed record ChatSetupView(ChatPluginView Plugin, bool CanManage, bool CanManageConnections, bool InteractiveAvailable,
    bool RedactedContentAllowed, string? HostReason, bool NotificationOnlySlice);

public sealed partial class ChatConfigurationService
{
    public async Task<ChatSetupView> SetupAsync(ForgeActor? actor, string productId, string spawnId, CancellationToken ct)
    {
        var plugin = await this.GetPluginAsync(actor, productId, spawnId, ct);
        return new(plugin, await authorizer.IsAllowedAsync(actor, ForgeCapabilities.SubscriptionManage, new(plugin.ProductId, spawnId), ct),
            await authorizer.IsAllowedAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct),
            ChatPluginInteractiveGate.AvailableFor(options.Value), options.Value.ProductContentModes.GetValueOrDefault(plugin.ProductId) == "redacted",
            options.Value.UnavailableReason ?? (!options.Value.PluginsEnabled ? options.Value.Engine == "legacy" ? "legacy-engine-active" : "host-chat-disabled" : null), options.Value.TelegramSlice.Enabled);
    }

    /// <summary>One revision and one transaction for the entire drawer. Never executes a work action or contacts a provider.</summary>
    public async Task<ChatSetupView> SaveSetupAsync(ForgeActor? actor, string productId, string spawnId, ChatSetupWrite write, CancellationToken ct)
    {
        var product = await this.ProductAsync(actor, productId, spawnId, true, ct);
        if (options.Value.TelegramSlice.Enabled)
        {
            if (write.Destinations is null || write.Destinations.Count > 1 || write.Destinations.Any(d => d is null || d.Interactive))
                throw new ChatConfigurationException("interactive-mode-unavailable", 409);
            await slice.SaveAsync(actor, product.Id, spawnId, new(write.ExpectedRevision, write.Enabled,
                write.Destinations.FirstOrDefault()?.DestinationId, write.ActivateNewRun), ct);
            return await this.SetupAsync(actor, product.Id, spawnId, ct);
        }
        if (write.Destinations is null || write.Destinations.Count > 32 || write.Destinations.Any(d => d is null || string.IsNullOrWhiteSpace(d.DestinationId)) || write.Enabled && write.Destinations.Count == 0
            || write.Destinations.Select(d => d.DestinationId).Distinct(StringComparer.Ordinal).Count() != write.Destinations.Count)
            throw new ChatConfigurationException("invalid-destinations");
        var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
        var run = RunIdentity(spawn);
        var parent = await control.SpawnPlugins.SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == spawnId && p.PluginId == "chat", ct);
        CheckRevision(parent?.Revision, write.ExpectedRevision);
        var newRun = parent is not null && parent.RunIdentity != run;
        if (newRun && write.Enabled && !write.ActivateNewRun) throw new ChatConfigurationException("new-run-needs-activation", 409);
        if (parent is not null && this.PluginReason(parent) is not null) throw new ChatConfigurationException("plugin-settings-unavailable", 409);
        foreach (var draft in write.Destinations)
        {
            var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == draft.DestinationId && d.DeletedAt == null, ct)
                ?? throw new ChatConfigurationException("destination-not-found", 404);
            await this.RequireConnectionGrantAsync(destination.ConnectionId, product.Id, ct);
            var connection = await this.ConnectionAsync(destination.ConnectionId, ct);
            var provider = providers.Resolve(connection.ProviderId) ?? throw new ChatConfigurationException("provider-unavailable", 409);
            if (draft.ConversationPolicy is not ("flat" or "topic" or "thread")
                || draft.ConversationPolicy == "topic" && !provider.Descriptor.Capabilities.Topics
                || draft.ConversationPolicy == "thread" && !provider.Descriptor.Capabilities.Threads)
                throw new ChatConfigurationException("unsupported-conversation-policy");
            if (draft.Interactive && (!ChatPluginInteractiveGate.IsAvailable || !provider.Descriptor.Capabilities.Buttons
                || provider.Descriptor.Capabilities.Reception == ChatReceptionMode.None)) throw new ChatConfigurationException("interactive-mode-unavailable", 409);
            if (draft.ContentMode is not ("metadata" or "redacted")) throw new ChatConfigurationException("unsupported-content-mode");
            if (draft.EventKinds is { } kinds && (kinds.Distinct().Count() != kinds.Count || kinds.Any(k => !SpawnKinds.Contains(k))))
                throw new ChatConfigurationException("invalid-event-kinds");
        }
        var key = ChatConfigurationScope.Spawn(product.Id, spawnId).Key;
        await using var tx = await control.Database.BeginTransactionAsync(ct);
        var subscriptions = await control.ChatSubscriptions.Where(s => s.ScopeKey == key && s.RetiredAt == null).ToListAsync(ct);
        parent ??= new SpawnPluginRow { ProductId = product.Id, SpawnId = spawnId, Revision = 0 };
        if (parent.Revision == 0) control.SpawnPlugins.Add(parent);
        if (parent.Revision == 0 || newRun || !parent.Enabled && write.Enabled) parent.ActivationGeneration++;
        var retired = subscriptions.Where(s => newRun || !write.Destinations.Any(d => d.DestinationId == s.DestinationId && d.ConversationPolicy == s.ConversationPolicy)).ToList();
        await this.RetireAsync(retired, ct);
        if (!write.Enabled || newRun || !parent.Enabled) await this.RetireBindingsAsync(key, ct);
        parent.Enabled = write.Enabled; parent.RunIdentity = run; parent.Revision++;
        parent.RetiredAt = write.Enabled ? null : DateTimeOffset.UtcNow;
        // Retire conflicting routes before inserting replacements (SQLite's partial unique index is immediate).
        await this.SaveAsync(ct);
        foreach (var draft in write.Destinations)
        {
            var subscription = subscriptions.Find(s => !retired.Contains(s) && s.DestinationId == draft.DestinationId && s.ConversationPolicy == draft.ConversationPolicy);
            if (subscription is null)
            {
                subscription = new() { Id = Guid.NewGuid().ToString("N"), ScopeKey = key, ScopeKind = "spawn", ProductId = product.Id,
                    SpawnId = spawnId, PluginId = "chat", DestinationId = draft.DestinationId, ConversationPolicy = draft.ConversationPolicy, Revision = 0 };
                control.ChatSubscriptions.Add(subscription);
            }
            var eventKinds = JsonSerializer.Serialize(draft.EventKinds ?? SpawnKinds);
            var changed = subscription.Revision == 0 || subscription.ActivationGeneration != parent.ActivationGeneration || subscription.Interactive != draft.Interactive
                || subscription.ContentMode != draft.ContentMode || subscription.Paused != draft.Paused || subscription.EventKindsJson != eventKinds;
            if (changed)
            {
                subscription.ActivationGeneration = parent.ActivationGeneration;
                subscription.Interactive = draft.Interactive; subscription.ContentMode = draft.ContentMode; subscription.Paused = draft.Paused;
                subscription.EventKindsJson = eventKinds; subscription.Revision++;
                foreach (var binding in await control.ChatConversationBindings.Where(b => b.SubscriptionId == subscription.Id && b.RetiredAt == null).ToListAsync(ct))
                { binding.SubscriptionRevision = subscription.Revision; binding.Revision++; }
            }
        }
        await this.SaveAsync(ct);
        if (write.Enabled)
        {
            foreach (var subscription in await control.ChatSubscriptions.Where(s => s.ScopeKey == key && s.RetiredAt == null).ToListAsync(ct))
                await this.QueueIntroductionAsync(parent, subscription, spawn, ct);
            await control.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await this.SetupAsync(actor, product.Id, spawnId, ct);
    }

    private async Task QueueIntroductionAsync(SpawnPluginRow parent, ChatSubscriptionRow subscription, StorySpawnRow spawn, CancellationToken ct)
    {
        if (subscription.Paused) return;
        var destination = await control.ChatDestinations.AsNoTracking().SingleAsync(d => d.Id == subscription.DestinationId, ct);
        var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == destination.ConnectionId, ct);
        var binding = await control.ChatConversationBindings.SingleOrDefaultAsync(b => b.SubscriptionId == subscription.Id && b.RetiredAt == null, ct);
        if (binding is not null && binding.ProvisioningState != "pending") return;
        if (binding is null)
        {
            binding = new() { Id = Guid.NewGuid().ToString("N"), SubscriptionId = subscription.Id, SubscriptionRevision = subscription.Revision,
                ActivationGeneration = parent.ActivationGeneration, ConnectionId = connection.Id, ExternalTenantId = destination.ExternalTenantId, ChannelId = destination.ChannelId,
                RootDesiredState = spawn.Phase == "done" ? "completed" : "active" };
            control.ChatConversationBindings.Add(binding);
        }
        var dedupe = $"setup:{binding.Id}:{subscription.Revision}:{connection.Revision}:{destination.Revision}";
        if (await control.ChatNotifications.AnyAsync(n => n.Platform == "plugins" && n.DedupeKey == dedupe, ct)) return;
        var now = clock.GetUtcNow();
        var notification = new ChatNotificationRow { Id = Guid.NewGuid().ToString("N"), Platform = "plugins", Kind = "setup", ScopeKind = "spawn",
            ProductId = parent.ProductId, SpawnId = parent.SpawnId, RepoId = spawn.RepoId, DedupeKey = dedupe, CatalogVersion = 1, PayloadVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new ChatEventPayload(1, "Forge · Chat enabled", "Chat is enabled for this parent and every current and future child.", [], [], spawn.StoryId)),
            CreatedAt = now, ExpiresAt = now.AddDays(7), Status = "pending", RouteState = "routed" };
        control.ChatNotifications.Add(notification);
        var target = new ChatDeliveryTarget(1, "spawn", parent.ProductId, parent.SpawnId, null, parent.RunIdentity, parent.ActivationGeneration,
            subscription.Id, subscription.Revision, destination.Id, destination.Revision, connection.Id, connection.Revision, connection.ProviderId,
            destination.ExternalTenantId, destination.ChannelId, subscription.ConversationPolicy, subscription.Interactive);
        var delivery = new ChatDeliveryRow { NotificationId = notification.Id, SubscriptionId = subscription.Id, BindingId = binding.Id,
            ConnectionId = connection.Id, ChannelId = destination.ChannelId, TargetJson = JsonSerializer.Serialize(target),
            CreatedAt = now.ToUnixTimeMilliseconds(), NextAttemptAt = now.ToUnixTimeMilliseconds(), Actionable = false };
        control.ChatDeliveries.Add(delivery);
        control.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id });
    }

}