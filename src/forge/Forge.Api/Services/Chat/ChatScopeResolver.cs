// -----------------------------------------------------------------------
// <copyright file="ChatScopeResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed record ChatOwnedScope(ProductRow? Product, StorySpawnRow? Spawn, string? WorkItemId, string? WorkstreamId,
    string? ArtifactKey, string? Reason, bool Degraded = false);

public sealed class ChatScopeResolver(ControlDbContext control, IProductDbContextFactory products, IForgeDbPaths paths,
    IOptions<ChatPluginOptions> options)
{
    public bool ProductAvailable(ProductRow product) => !SplitMarkerCache.IsPhase73Applied(paths) || paths.ProductDbExists(product.Slug);

    public async Task<ChatOwnedScope> ResolveAsync(ChatEventInput input, CancellationToken ct)
    {
        if (ChatEventCatalog.HostDefaults.Contains(input.Kind)) return new(null, null, null, null, null, null);
        var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == input.ProductId || p.Slug == input.ProductId, ct);
        if (product is null) return new(null, null, null, null, null, "unresolved-scope");
        if (!this.ProductAvailable(product)) return new(product, null, null, null, null, "ownership-unverified", true);
        try
        {
            var db = products.CreateFor(product.Slug);
            var childId = input.WorkstreamId ?? input.WorkItemId;
            var child = childId is null ? null : await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(w => w.Id == childId, ct);
            string? parentId = child?.StorySpawnId;
            string? artifact = null;
            if (child is null && input.WorkItemId?.StartsWith("plan-doc:", StringComparison.Ordinal) == true)
            {
                var last = input.WorkItemId.LastIndexOf(':');
                if (last > 9 && input.WorkItemId.EndsWith(".md", StringComparison.Ordinal))
                {
                    parentId = input.WorkItemId[9..last]; artifact = input.WorkItemId[(last + 1)..^3];
                    if (!ForgeCapabilities.ArtifactKeys.Contains(artifact)
                        || !await db.DeliverableQuestions.AnyAsync(q => q.WorkItemId == input.WorkItemId, ct)) parentId = null;
                }
            }
            else if (child is null && input.WorkItemId is not null)
            {
                var planningParent = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == input.WorkItemId || s.StoryId == input.WorkItemId, ct);
                if (planningParent is not null && await db.DeliverableQuestions.AnyAsync(q => q.WorkItemId == input.WorkItemId, ct)) parentId = planningParent.Id;
            }
            // An explicit parent alone is valid only for a parent status/action event, never proof of child ownership.
            if (childId is null) parentId = input.SpawnId;
            if (parentId is null || input.SpawnId is not null && input.SpawnId != parentId
                || input.ArtifactKey is not null && input.ArtifactKey != artifact)
                return new(product, null, null, null, null, "unresolved-scope");
            var spawn = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == parentId, ct);
            if (spawn is null || input.RepoId is not null && input.RepoId != spawn.RepoId
                || child is not null && child.RepoId != spawn.RepoId
                || !await db.Repos.AnyAsync(r => r.Id == spawn.RepoId && r.ProductId == product.Id, ct))
                return new(product, null, null, null, null, "unresolved-scope");
            return new(product, spawn, input.WorkItemId, child?.Id, artifact, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { return new(product, null, null, null, null, "ownership-unverified", true); }
    }

    public async Task<string?> ValidateAsync(ChatDeliveryTarget target, ChatNotificationRow notification, CancellationToken ct, bool action = false)
    {
        if (!options.Value.PluginsEnabled || options.Value.UnavailableReason is not null) return "disabled";
        var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == target.ConnectionId, ct);
        var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == target.DestinationId, ct);
        var subscription = await control.ChatSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == target.SubscriptionId, ct);
        var importedHistory = !action && await control.ChatMigrationItems.AnyAsync(i => i.Kind == "notification" && i.SourceId == notification.Id
            && control.ChatDeliveries.Any(d => d.Id == i.TargetId && d.SubscriptionId == target.SubscriptionId), ct);
        if (target.Version != 1 || connection is null || !connection.Enabled || connection.DeletedAt is not null
            || connection.Revision != target.ConnectionRevision || connection.ProviderId != target.ProviderId
            || destination is null || destination.DeletedAt is not null || destination.Revision != target.DestinationRevision
            || destination.ConnectionId != connection.Id || destination.ExternalTenantId != target.ExternalTenantId || destination.ChannelId != target.ChannelId
            || subscription is null || subscription.Paused || subscription.RetiredAt is not null
            || subscription.Revision != target.SubscriptionRevision || subscription.DestinationId != target.DestinationId
            || subscription.ActivationGeneration != target.ActivationGeneration
            || !importedHistory && !(notification.Platform == "plugins" && notification.Kind == "setup" && !action) && !ChatEventCatalog.Matches(subscription, notification.Kind)) return "route-retired";
        if (action && !subscription.Interactive) return "notifications-only";
        if (target.ScopeKind == "host") return target.HostId == options.Value.HostId && subscription.HostId == target.HostId ? null : "route-retired";
        if (!await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connection.Id && g.ProductId == target.ProductId, ct)) return "grant-revoked";
        var owned = await this.ResolveAsync(new(notification.Kind, notification.DedupeKey, target.ProductId, target.SpawnId,
            notification.WorkItemId, notification.WorkstreamId, notification.RepoId, notification.DomainRevision, notification.ArtifactKey), ct);
        if (owned.Reason is not null) return owned.Degraded ? "ownership-unverified" : "parent-or-target-deleted";
        var plugin = await control.SpawnPlugins.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == target.ProductId && p.SpawnId == target.SpawnId && p.PluginId == "chat", ct);
        if (plugin is null || !plugin.Enabled || plugin.RetiredAt is not null || plugin.SettingsVersion != 1
            || plugin.ActivationGeneration != target.ActivationGeneration || plugin.RunIdentity != target.RunIdentity
            || owned.Spawn is null || ChatConfigurationService.RunIdentity(owned.Spawn) != target.RunIdentity) return "new-run-or-disabled";
        return null;
    }
}