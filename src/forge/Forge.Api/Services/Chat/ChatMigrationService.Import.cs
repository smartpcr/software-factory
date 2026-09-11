// -----------------------------------------------------------------------
// <copyright file="ChatMigrationService.Import.cs" company="Microsoft Corp.">
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
    private async Task ImportCoreAsync(ChatMigrationStateRow state, string botId, IReadOnlyList<ChatMigrationSelection> selected, bool activate, CancellationToken ct)
    {
        var source = JsonSerializer.Deserialize<ChatMigrationSource>(state.SourceJson)!;
        var connection = await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == ConnectionId, ct);
        if (connection is null)
        {
            if (await control.ChatConnections.AnyAsync(c => c.ProviderId == "telegram" && c.InstallationId == botId && c.DeletedAt == null, ct))
                throw new ChatConfigurationException("legacy-bot-already-configured", 409);
            connection = new() { Id = ConnectionId, DisplayName = "Legacy Telegram", ProviderId = "telegram", InstallationId = botId,
                SettingsJson = JsonSerializer.Serialize(new { longPollSeconds = source.LongPollSeconds }),
                SecretReferencesJson = JsonSerializer.Serialize(new Dictionary<string, ChatSecretReference> { ["bot-token"] = source.Credential }) };
            control.ChatConnections.Add(connection);
        }
        if (connection.DeletedAt is not null || connection.InstallationId != botId || connection.ProviderId != "telegram")
            throw new ChatConfigurationException("legacy-connection-changed", 409);
        if (activate && !connection.Enabled) { connection.Enabled = true; connection.Revision++; }
        var topics = await control.ChatSpawnTopics.AsNoTracking().ToListAsync(ct);
        var notifications = await control.ChatNotifications.Where(n => n.Platform == "telegram" || n.Platform == "").ToListAsync(ct);
        var channels = source.Channels.Concat(topics.Select(t => t.ChatId)).Concat(notifications.Where(n => !string.IsNullOrWhiteSpace(n.ChatId)).Select(n => n.ChatId!)).Distinct().ToArray();
        var destinations = new Dictionary<string, ChatDestinationRow>();
        foreach (var channel in channels)
        {
            var id = Key("destination", channel);
            var destination = await control.ChatDestinations.FindAsync([id], ct);
            if (destination is null)
            {
                destination = new() { Id = id, ConnectionId = ConnectionId, ExternalTenantId = botId, ChannelId = channel,
                    DisplayName = channel == source.GlobalChannel ? "Legacy host alerts" : "Legacy " + channel,
                    Kind = channel == legacy.Bridge.Telegram.SupergroupChatId.ToString() || topics.Any(t => t.ChatId == channel) ? "forum" : "group" };
                control.ChatDestinations.Add(destination);
            }
            if (destination.DeletedAt is not null || destination.ConnectionId != ConnectionId || destination.ChannelId != channel || destination.ExternalTenantId != botId)
                throw new ChatConfigurationException("legacy-destination-changed", 409);
            destinations[channel] = destination;
        }
        await control.SaveChangesAsync(ct);
        var issues = new List<ChatMigrationIssue>();
        var invalidTopics = new Dictionary<(string ProductId, string SpawnId, string ChannelId), string>();
        var routes = new Dictionary<string, (ChatSubscriptionRow Sub, ChatConversationBindingRow Binding, SpawnPluginRow? Parent)>();
        foreach (var destination in destinations.Values)
            routes["host:" + destination.Id] = await this.RouteAsync(state, source, destination, null, null, false, activate, ct);
        foreach (var topic in topics)
        {
            var owned = await resolver.ResolveAsync(new("open-questions", "import", topic.ProductId, topic.SpawnId), ct);
            var reason = owned.Reason ?? (topic.ClosedAt is not null ? "topic-retired" : null);
            if (reason is null && owned.Spawn is not null && topic.CreatedAt < owned.Spawn.CreatedAt) reason = "topic-from-earlier-run";
            if (topics.Count(t => t.ChatId == topic.ChatId && t.ThreadId == topic.ThreadId) > 1) reason = "ambiguous-topic";
            if (reason is not null)
            {
                issues.Add(new("topic", topic.SpawnId, reason));
                invalidTopics[(owned.Product?.Id ?? topic.ProductId, topic.SpawnId, topic.ChatId)] = reason;
                continue;
            }
            var destination = destinations[topic.ChatId];
            var key = ScopeKey(owned.Product!.Id, owned.Spawn!.Id) + ":" + destination.Id;
            routes[key] = await this.RouteAsync(state, source, destination, owned, topic,
                selected.Any(s => s.ProductId == owned.Product.Id && s.SpawnId == owned.Spawn.Id), activate, ct);
        }
        foreach (var notification in notifications)
        {
            var owned = await this.OwnedAsync(notification, ct);
            var destination = destinations.GetValueOrDefault(notification.ChatId ?? "") ?? destinations[source.GlobalChannel];
            var reason = owned.Reason ?? (string.IsNullOrWhiteSpace(notification.ChatId) ? "destination-missing" : null);
            if (owned.Spawn is not null && invalidTopics.TryGetValue((owned.Product!.Id, owned.Spawn.Id, destination.ChannelId), out var topicReason)) reason = topicReason;
            var key = owned.Spawn is not null && reason is null ? ScopeKey(owned.Product!.Id, owned.Spawn.Id) + ":" + destination.Id : "host:" + destination.Id;
            if (!routes.TryGetValue(key, out var route))
            {
                route = await this.RouteAsync(state, source, destination, owned.Spawn is not null && reason is null ? owned : null, null,
                    selected.Any(s => s.ProductId == owned.Product?.Id && s.SpawnId == owned.Spawn?.Id), activate, ct);
                routes[key] = route;
            }
            if (reason is not null) issues.Add(new("notification", notification.Id, reason));
            var originalTopic = topics.SingleOrDefault(t => t.ChatId == destination.ChannelId && t.SpawnId == (notification.SpawnId ?? owned.Spawn?.Id)
                && (t.ProductId == notification.ProductId || t.ProductId == owned.Product?.Id || t.ProductId == owned.Product?.Slug));
            var aliasIssue = await this.BackfillAsync(notification, connection, destination, route.Sub, route.Binding, route.Parent, owned, activate, reason, originalTopic?.ThreadId, ct);
            if (aliasIssue is not null) issues.Add(new("alias", notification.Id, aliasIssue));
        }
        foreach (var choice in selected)
        {
            if (!routes.Values.Any(r => r.Sub.ProductId == choice.ProductId && r.Sub.SpawnId == choice.SpawnId))
                throw new ChatConfigurationException("migration-selection-unresolved", 409);
            foreach (var route in routes.Values.Where(r => r.Sub.ProductId == choice.ProductId && r.Sub.SpawnId == choice.SpawnId))
            {
                if (route.Sub.ContentMode != choice.ContentMode)
                {
                    route.Sub.ContentMode = choice.ContentMode;
                    // Content remains subject to the host's product ceiling at rendering and action dispatch.
                }
            }
        }
        foreach (var group in identities.ActiveProviderLinks.Where(l => l.Provider == "telegram" && source.Channels.Contains(l.TenantId)).GroupBy(l => l.ProviderUserId))
        {
            if (group.Select(l => l.UserId).Distinct().Count() != 1) { issues.Add(new("identity", group.Key, "ambiguous-legacy-identity")); continue; }
            foreach (var link in group)
            {
                var key = Key("link", link.Id);
                if (!await control.ChatMigrationItems.AnyAsync(i => i.Key == key, ct)) control.ChatMigrationItems.Add(new()
                { Key = key, Kind = "link", SourceId = link.Id, TargetId = botId, SnapshotJson = JsonSerializer.Serialize(link) });
            }
        }
        state.IssuesJson = JsonSerializer.Serialize(issues);
        await control.SaveChangesAsync(ct);
    }

    private async Task<(ChatSubscriptionRow Sub, ChatConversationBindingRow Binding, SpawnPluginRow? Parent)> RouteAsync(
        ChatMigrationStateRow state, ChatMigrationSource source, ChatDestinationRow destination, ChatOwnedScope? owned,
        ChatSpawnTopicRow? topic, bool selected, bool activate, CancellationToken ct)
    {
        var key = owned is null ? ChatConfigurationScope.Host(source.HostId).Key : ScopeKey(owned.Product!.Id, owned.Spawn!.Id);
        SpawnPluginRow? parent = null;
        if (owned is not null)
        {
            parent = await control.SpawnPlugins.FindAsync([owned.Product!.Id, owned.Spawn!.Id, "chat"], ct);
            if (parent is null)
            {
                parent = new() { ProductId = owned.Product.Id, SpawnId = owned.Spawn.Id, RunIdentity = ChatConfigurationService.RunIdentity(owned.Spawn), ActivationGeneration = 1 };
                control.SpawnPlugins.Add(parent);
            }
            if (parent.RunIdentity != ChatConfigurationService.RunIdentity(owned.Spawn)) throw new ChatConfigurationException("migration-parent-run-changed", 409);
            if (activate && selected && !parent.Enabled) { parent.Enabled = true; parent.RetiredAt = null; parent.Revision++; }
            if (!await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == ConnectionId && g.ProductId == owned.Product.Id, ct)
                && !control.ChatConnectionProducts.Local.Any(g => g.ConnectionId == ConnectionId && g.ProductId == owned.Product.Id))
                control.ChatConnectionProducts.Add(new() { ConnectionId = ConnectionId, ProductId = owned.Product.Id });
        }
        var id = Key("subscription", key + ":" + destination.Id);
        var sub = await control.ChatSubscriptions.FindAsync([id], ct);
        if (sub is null)
        {
            sub = new() { Id = id, ScopeKey = key, ScopeKind = owned is null ? "host" : "spawn", HostId = owned is null ? source.HostId : null,
                ProductId = owned?.Product?.Id, SpawnId = owned?.Spawn?.Id, PluginId = owned is null ? null : "chat", DestinationId = destination.Id,
                ConversationPolicy = topic is null ? "flat" : "topic", ActivationGeneration = parent?.ActivationGeneration ?? 0,
                Interactive = owned is not null && source.RemoteActions, ContentMode = "metadata",
                EventKindsJson = JsonSerializer.Serialize(owned is null ? destination.ChannelId == source.GlobalChannel ? ChatEventCatalog.HostDefaults : [] : ChatEventCatalog.SpawnDefaults),
                Paused = true };
            control.ChatSubscriptions.Add(sub);
            control.ChatMigrationItems.Add(new() { Key = id, Kind = "subscription", SourceId = key + ":" + destination.Id, TargetId = id });
        }
        if (sub.RetiredAt is not null) throw new ChatConfigurationException("migration-subscription-retired", 409);
        var paused = !activate || owned is not null && !selected;
        if (sub.Paused != paused) { sub.Paused = paused; sub.Revision++; }
        var bindingId = Key("binding", id);
        var binding = await control.ChatConversationBindings.FindAsync([bindingId], ct);
        if (binding is null)
        {
            binding = new() { Id = bindingId, SubscriptionId = id, SubscriptionRevision = sub.Revision, ActivationGeneration = sub.ActivationGeneration,
                ConnectionId = ConnectionId, ExternalTenantId = destination.ExternalTenantId, ChannelId = destination.ChannelId,
                ConversationId = topic?.ThreadId, ProvisioningState = "ready", RootDesiredState = owned?.Spawn?.Phase == "done" ? "completed" : "active" };
            control.ChatConversationBindings.Add(binding);
        }
        binding.SubscriptionRevision = sub.Revision;
        binding.ConversationId = topic?.ThreadId;
        binding.ProvisioningState = "ready";
        sub.ConversationPolicy = topic is null ? "flat" : "topic";
        await control.SaveChangesAsync(ct);
        return (sub, binding, parent);
    }
}