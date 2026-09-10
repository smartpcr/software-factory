// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationService.Spawns.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatConfigurationService
{
    public async Task<ChatPluginView> GetPluginAsync(ForgeActor? actor, string productId, string spawnId, CancellationToken ct = default)
    {
        var product = await this.ProductAsync(actor, productId, spawnId, false, ct);
        var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
        var row = await control.SpawnPlugins.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == spawnId && p.PluginId == "chat", ct);
        var currentRun = row is not null && row.RunIdentity == RunIdentity(spawn);
        if (!currentRun) return new(product.Id, spawnId, false, row?.Revision ?? 0, row?.ActivationGeneration ?? 0,
            1, null, "off", row is null ? null : "new-run-needs-activation", Array.Empty<ChatSubscriptionRow>());
        var scope = ChatConfigurationScope.Spawn(product.Id, spawnId);
        var subscriptions = await this.VisibleSubscriptionsAsync(scope, ct);
        subscriptions = subscriptions.Where(s => s.ActivationGeneration == row!.ActivationGeneration).ToList();
        var reason = this.PluginReason(row!);
        var state = !row!.Enabled ? "off" : reason is not null ? "paused" : subscriptions.Count == 0 ? "needs-setup" : "configured";
        if (state == "configured")
        {
            reason = await this.DeliveryGateReasonAsync(subscriptions, ct);
            if (reason is not null) state = "paused";
        }
        if (options.Value.TelegramSlice.Enabled)
        {
            var health = await slice.GetAsync(actor, product.Id, spawnId, ct);
            state = health.State; reason = health.Reason;
        }
        return new(product.Id, spawnId, row.Enabled, row.Revision, row.ActivationGeneration, row.SettingsVersion, row.CopyOrigin, state, reason, subscriptions);
    }

    /// <summary>Resolve current parent configuration on demand; never copy it onto child workstreams.</summary>
    public async Task<ChatPluginView> GetForChildAsync(ForgeActor? actor, string productId, string childId, CancellationToken ct = default)
    {
        var product = await this.ProductAsync(actor, productId, null, false, ct);
        await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(product.Id, WorkstreamId: childId), ct);
        var parentId = await products.CreateFor(product.Slug).Workstreams.AsNoTracking().Where(w => w.Id == childId).Select(w => w.StorySpawnId).SingleAsync(ct);
        if (parentId is null) throw new ChatConfigurationException("parent-unresolved", 409);
        return await this.GetPluginAsync(actor, product.Id, parentId, ct);
    }

    public async Task<ChatPluginView> PutPluginAsync(ForgeActor? actor, string productId, string spawnId, ChatPluginWrite write, CancellationToken ct = default)
    {
        var product = await this.ProductAsync(actor, productId, spawnId, true, ct);
        ValidateJson(write.Settings, write.SettingsVersion);
        var plugin = plugins.Resolve("chat");
        if (plugin is not null && plugin.Descriptor.SettingsVersions.Contains(write.SettingsVersion)
            && !plugin.ValidateSettings(write.SettingsVersion, write.Settings).Valid) throw new ChatConfigurationException("invalid-plugin-settings");
        var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
        var run = RunIdentity(spawn);
        var row = await control.SpawnPlugins.SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == spawnId && p.PluginId == "chat", ct);
        CheckRevision(row?.Revision, write.ExpectedRevision);
        var newRun = row is not null && row.RunIdentity != run;
        if (newRun && write.Enabled && !write.ActivateNewRun) throw new ChatConfigurationException("new-run-needs-activation", 409);
        if (write.CopyFromSpawnId is not null && (write.Enabled || row is not null && !newRun))
            throw new ChatConfigurationException("copy-requires-new-disabled-settings", 409);
        ChatPluginView? source = null;
        if (write.CopyFromSpawnId is not null)
        {
            if (write.CopyFromSpawnId == spawnId) throw new ChatConfigurationException("invalid-copy-origin");
            source = await this.GetPluginAsync(actor, product.Id, write.CopyFromSpawnId, ct);
            if (source.Revision == 0 || source.Reason == "new-run-needs-activation" || source.SettingsVersion != 1)
                throw new ChatConfigurationException("copy-origin-unavailable", 409);
            var sourceKey = ChatConfigurationScope.Spawn(product.Id, source.SpawnId).Key;
            if (await control.ChatSubscriptions.CountAsync(s => s.ScopeKey == sourceKey && s.RetiredAt == null, ct) != source.Subscriptions.Count)
                throw new ForgeAccessDeniedException();
        }

        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        var scope = ChatConfigurationScope.Spawn(product.Id, spawnId);
        var subscriptions = await control.ChatSubscriptions.Where(s => s.ScopeKey == scope.Key && s.RetiredAt == null).ToListAsync(ct);
        row ??= new SpawnPluginRow { ProductId = product.Id, SpawnId = spawnId, Revision = 0 };
        if (row.Revision == 0) control.SpawnPlugins.Add(row);
        if (newRun)
        {
            await this.RetireAsync(subscriptions, ct);
            subscriptions.Clear(); row.CopyOrigin = null;
        }
        if (newRun || row.Revision == 0 || !row.Enabled && write.Enabled)
        {
            row.ActivationGeneration++;
            foreach (var subscription in subscriptions) { subscription.ActivationGeneration = row.ActivationGeneration; subscription.Revision++; }
        }
        if (!write.Enabled || newRun || !row.Enabled)
            await this.RetireBindingsAsync(scope.Key, ct);
        row.Enabled = write.Enabled; row.SettingsVersion = write.SettingsVersion; row.SettingsJson = write.Settings.GetRawText();
        row.RetiredAt = write.Enabled ? null : DateTimeOffset.UtcNow;
        row.RunIdentity = run; row.Revision++;
        if (source is not null)
        {
            row.CopyOrigin = ChatConfigurationScope.Spawn(product.Id, source.SpawnId).Key;
            foreach (var saved in source.Subscriptions)
            {
                control.ChatSubscriptions.Add(new ChatSubscriptionRow
                {
                    Id = Guid.NewGuid().ToString("N"), ScopeKind = "spawn", ScopeKey = scope.Key,
                    ProductId = product.Id, SpawnId = spawnId, PluginId = "chat", ActivationGeneration = row.ActivationGeneration,
                    DestinationId = saved.DestinationId, ConversationPolicy = saved.ConversationPolicy,
                    Interactive = false, CatalogVersion = saved.CatalogVersion, EventKindsJson = saved.EventKindsJson, Paused = saved.Paused,
                });
            }
        }
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return await this.GetPluginAsync(actor, product.Id, spawnId, ct);
    }

    public async Task<HostChatSetup> GetHostSetupAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.HostAlertManage, new(), ct);
        var scope = ChatConfigurationScope.Host(options.Value.HostId);
        var subscriptions = await this.VisibleSubscriptionsAsync(scope, ct);
        var reason = subscriptions.Count == 0 ? "host-alerts-need-destination" : await this.DeliveryGateReasonAsync(subscriptions, ct);
        return new(scope.HostId!, scope.Key, subscriptions.Count == 0 ? "needs-setup" : reason is null ? "configured" : "paused", reason, subscriptions);
    }

    private string? PluginReason(SpawnPluginRow row)
    {
        var plugin = plugins.Resolve("chat");
        if (plugin is null) return "plugin-unavailable";
        if (!plugin.Descriptor.SettingsVersions.Contains(row.SettingsVersion)) return "unsupported-plugin-settings-version";
        try
        {
            using var settings = JsonDocument.Parse(row.SettingsJson);
            return plugin.ValidateSettings(row.SettingsVersion, settings.RootElement).Valid ? null : "invalid-plugin-settings";
        }
        catch (JsonException) { return "invalid-plugin-settings"; }
    }

    private async Task<List<ChatSubscriptionRow>> VisibleSubscriptionsAsync(ChatConfigurationScope scope, CancellationToken ct)
    {
        var query = control.ChatSubscriptions.AsNoTracking().Where(s => s.ScopeKey == scope.Key && s.RetiredAt == null);
        if (scope.Kind == "spawn")
            query = query.Where(s => control.ChatDestinations.Any(d => d.Id == s.DestinationId && d.DeletedAt == null
                && control.ChatConnectionProducts.Any(g => g.ConnectionId == d.ConnectionId && g.ProductId == scope.ProductId)));
        return await query.ToListAsync(ct);
    }

    private async Task<string?> DeliveryGateReasonAsync(IReadOnlyList<ChatSubscriptionRow> subscriptions, CancellationToken ct)
    {
        if (options.Value.UnavailableReason is { } invalid) return invalid;
        if (!options.Value.PluginsEnabled) return options.Value.Engine == "legacy" ? "legacy-engine-active" : "host-chat-disabled";
        if (!providers.IsAvailable || !plugins.IsAvailable) return "registry-unavailable";
        var ids = subscriptions.Select(s => s.DestinationId).ToList();
        var destinations = await control.ChatDestinations.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var connectionIds = destinations.Select(d => d.ConnectionId).ToList();
        var connections = await control.ChatConnections.AsNoTracking().Where(c => connectionIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var subscription in subscriptions.Where(s => !s.Paused && s.CatalogVersion == 1))
        {
            var destination = destinations.Find(d => d.Id == subscription.DestinationId && d.DeletedAt is null);
            var connection = connections.Find(c => c.Id == destination?.ConnectionId && c.Enabled && c.DeletedAt is null);
            if (connection is not null && this.ConnectionReason(connection) is null) return "plugin-delivery-not-implemented";
        }
        return "no-available-subscriptions";
    }

    private async Task RetireBindingsAsync(string scopeKey, CancellationToken ct)
    {
        var ids = await control.ChatSubscriptions.Where(s => s.ScopeKey == scopeKey).Select(s => s.Id).ToListAsync(ct);
        foreach (var binding in await control.ChatConversationBindings.Where(b => ids.Contains(b.SubscriptionId) && b.RetiredAt == null).ToListAsync(ct))
        { binding.RetiredAt = DateTimeOffset.UtcNow; binding.Revision++; }
    }
}