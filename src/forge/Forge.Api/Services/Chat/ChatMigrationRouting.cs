// -----------------------------------------------------------------------
// <copyright file="ChatMigrationRouting.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Producer compatibility is proved by imported scope and dedupe records, never by a message ID alone.</summary>
public sealed class ChatMigrationRouting(ControlDbContext control, ChatScopeResolver resolver)
{
    internal static string DedupeSource(string scopeKey, string kind, string legacyKey) => ChatEventCatalog.Hash(scopeKey + "\0" + kind + "\0" + legacyKey);

    public async Task<bool> SuppressAsync(ChatEventInput input, string legacyKey, bool legacyEngine, CancellationToken ct)
    {
        var state = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(ct);
        if (state is null) return false;
        var sourceConfiguration = JsonSerializer.Deserialize<ChatMigrationSource>(state.SourceJson)!;
        var owned = await resolver.ResolveAsync(input, ct);
        var scope = owned.Spawn is null ? ChatConfigurationScope.Host(sourceConfiguration.HostId)
            : ChatConfigurationScope.Spawn(owned.Product!.Id, owned.Spawn.Id);
        if (legacyEngine)
        {
            if (state.Phase != "legacy") return false;
            // Rollback returns only the selected imported routes; unrelated future parents remain off.
            if (owned.Reason is not null && !ChatEventCatalog.HostDefaults.Contains(input.Kind)) return true;
            if (owned.Spawn is not null && !await control.SpawnPlugins.AnyAsync(p => p.ProductId == owned.Product!.Id && p.SpawnId == owned.Spawn.Id
                && p.PluginId == "chat" && p.Enabled && p.RetiredAt == null && p.RunIdentity == ChatConfigurationService.RunIdentity(owned.Spawn), ct)) return true;
            var subscriptions = await control.ChatSubscriptions.AsNoTracking().Where(s => s.ScopeKey == scope.Key && !s.Paused && s.RetiredAt == null
                && control.ChatMigrationItems.Any(i => i.Kind == "subscription" && i.TargetId == s.Id)
                && (s.ScopeKind == "host" || control.SpawnPlugins.Any(p => p.ProductId == s.ProductId && p.SpawnId == s.SpawnId && p.PluginId == "chat" && p.ActivationGeneration == s.ActivationGeneration))
                && control.ChatDestinations.Any(d => d.Id == s.DestinationId && d.ChannelId == sourceConfiguration.GlobalChannel && d.DeletedAt == null)).ToListAsync(ct);
            return !subscriptions.Any(s => ChatEventCatalog.Matches(s, input.Kind));
        }
        var source = DedupeSource(scope.Key, input.Kind, legacyKey);
        var id = await control.ChatMigrationItems.AsNoTracking().Where(i => i.Kind == "dedupe" && i.SourceId == source).Select(i => i.TargetId).SingleOrDefaultAsync(ct);
        return id is not null && await control.ChatNotifications.AnyAsync(n => n.Id == id && n.Platform == "plugins"
            && (n.Status == "pending" || n.Status == "sent" || n.Status == "sending" || n.Status == "uncertain")
            && control.ChatDeliveries.Any(d => d.NotificationId == n.Id && d.State != "cancelled" && d.State != "resolved"
                && (d.State == "uncertain" || control.ChatDeliveryMessages.Any(m => m.DeliveryId == d.Id && !m.ControlsRetired))), ct);
    }
}