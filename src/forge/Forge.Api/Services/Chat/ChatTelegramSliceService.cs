// -----------------------------------------------------------------------
// <copyright file="ChatTelegramSliceService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed record ChatNotificationSetupWrite(long ExpectedRevision, bool Enabled, string? DestinationId, bool ActivateNewRun = false);
public sealed record ChatNotificationSetupView(bool Enabled, long Revision, string State, string? Reason, string? DestinationId,
    string? ConnectionId, bool CanManage, DateTimeOffset? LastSentAt, string Preview);

/// <summary>Atomic row setup and a single-recipient outbox. No fan-out, receive loop, domain actions, or automatic send retries.</summary>
public sealed partial class ChatTelegramSliceService(ControlDbContext control, IProductDbContextFactory products,
    ForgeCapabilityAuthorizer authorizer, IChatProviderRegistry providers, WorkstreamPluginRegistry plugins, ChatTestEnvironmentSecrets secrets,
    ChatContentPolicy policy, IOptions<ChatPluginOptions> options, IForgeDbPaths paths, ChatSliceCoordinator coordinator)
{
    public const string Platform = "telegram-1s";
    internal sealed record Target(int Version, string BindingId, long ConnectionRevision, long DestinationRevision, string RunIdentity, string StoryId);
    private sealed record Route(ProductRow Product, StorySpawnRow Spawn, SpawnPluginRow Plugin,
        ChatSubscriptionRow Subscription, ChatDestinationRow Destination, ChatConnectionRow Connection, ChatConversationBindingRow Binding)
    {
        public ChatAddress Address => new(this.Connection.Id, this.Destination.ExternalTenantId, this.Destination.ChannelId);
        public Target Snapshot => new(1, this.Binding.Id, this.Connection.Revision, this.Destination.Revision, this.Plugin.RunIdentity, this.Spawn.StoryId);
    }

    public async Task<ChatNotificationSetupView> GetAsync(ForgeActor? actor, string productId, string spawnId, CancellationToken ct = default)
    {
        var product = await this.RequireAsync(actor, productId, spawnId, false, ct);
        var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
        var plugin = await control.SpawnPlugins.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == spawnId && p.PluginId == "chat", ct);
        var current = plugin?.RunIdentity == ChatConfigurationService.RunIdentity(spawn);
        var enabled = current && plugin!.Enabled;
        var route = enabled ? await this.RouteAsync(product.Id, spawnId, ct) : null;
        var reason = await this.GuardAsync(ct);
        if (plugin is not null && !current) reason = "new-run-needs-activation";
        if (enabled && reason is null) reason = route is null ? "route-needs-setup"
            : route.Binding.ProvisioningState == "ready" ? null : route.Binding.ProvisioningState;
        if (reason == "setup-pending" && route is not null && !await control.ChatNotifications.AnyAsync(n => n.Platform == Platform
            && n.DedupeKey == "slice1:" + route.Binding.Id + ":introduction" && (n.Status == "pending" || n.Status == "sending"), ct))
            reason = "route-needs-setup";
        var sent = route is null ? new List<ChatNotificationRow>() : await control.ChatNotifications.AsNoTracking()
            .Where(n => n.Platform == Platform && n.DedupeKey.StartsWith("slice1:" + route.Binding.Id + ":") && n.SentAt != null).ToListAsync(ct);
        var preview = policy.Create("preview", "open-questions", spawn.StoryId, []);
        return new(enabled, plugin?.Revision ?? 0, !enabled ? "off" : reason is null ? "ready" : "needs-setup", reason,
            route?.Destination.Id, route?.Connection.Id,
            await authorizer.IsAllowedAsync(actor, ForgeCapabilities.SubscriptionManage, new(product.Id, spawnId), ct),
            sent.Select(n => n.SentAt).Max(), preview.Title + "\n\n" + preview.Body);
    }

    public async Task<ChatNotificationSetupView> SaveAsync(ForgeActor? actor, string productId, string spawnId,
        ChatNotificationSetupWrite write, CancellationToken ct = default)
    {
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            var product = await this.RequireAsync(actor, productId, spawnId, true, ct);
            var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
            var plugin = await control.SpawnPlugins.SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == spawnId && p.PluginId == "chat", ct);
            if ((plugin?.Revision ?? 0) != write.ExpectedRevision) throw new ChatConfigurationException("revision-conflict", 409);
            var run = ChatConfigurationService.RunIdentity(spawn);
            if (write.Enabled && plugin is not null && plugin.RunIdentity != run && !write.ActivateNewRun)
                throw new ChatConfigurationException("new-run-needs-activation", 409);

            ChatDestinationRow? destination = null;
            ChatConnectionRow? connection = null;
            if (write.Enabled)
            {
                if (await this.GuardAsync(ct) is { } unavailable) throw new ChatConfigurationException(unavailable, 409);
                if (await control.SpawnPlugins.AnyAsync(p => p.PluginId == "chat" && p.Enabled && (p.ProductId != product.Id || p.SpawnId != spawnId), ct)
                    || await control.ChatConnections.CountAsync(c => c.Enabled && c.DeletedAt == null, ct) != 1)
                    throw new ChatConfigurationException("slice-requires-one-route", 409);
                destination = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == write.DestinationId && d.DeletedAt == null, ct)
                    ?? throw new ChatConfigurationException("destination-required");
                connection = await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == destination.ConnectionId && c.Enabled && c.DeletedAt == null && c.ProviderId == "telegram", ct)
                    ?? throw new ChatConfigurationException("telegram-connection-required");
                if (!await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connection.Id && g.ProductId == product.Id, ct)) throw new ForgeAccessDeniedException();
                await using var session = await this.OpenAsync(connection, ct);
                var validated = await session.ValidateDestinationAsync(new(connection.Id, destination.ExternalTenantId, destination.ChannelId), ct);
                if (!validated.Succeeded) throw new ChatConfigurationException(validated.Failure!.Code, 409);
            }

            // No persistent or provider-send side effects occurred before this transaction.
            await using var transaction = await control.Database.BeginTransactionAsync(ct);
            var scope = ChatConfigurationScope.Spawn(product.Id, spawnId).Key;
            if (write.Enabled && await control.ChatSubscriptions.AnyAsync(s => s.RetiredAt == null && s.ScopeKey != scope, ct))
                throw new ChatConfigurationException("slice-requires-one-route", 409);
            await this.RetireRouteAsync(product.Id, spawnId, ct);
            // Retire the old unique active route before inserting its replacement; the outer transaction keeps Save atomic.
            await control.SaveChangesAsync(ct);
            plugin ??= new() { ProductId = product.Id, SpawnId = spawnId, Revision = 0 };
            if (plugin.Revision == 0) control.SpawnPlugins.Add(plugin);
            plugin.Enabled = write.Enabled; plugin.RunIdentity = run; plugin.Revision++;
            plugin.SettingsVersion = 1; plugin.SettingsJson = "{}"; plugin.RetiredAt = write.Enabled ? null : DateTimeOffset.UtcNow;
            if (write.Enabled)
            {
                plugin.ActivationGeneration++;
                destination!.AccessState = "verified"; destination.ValidatedAt = DateTimeOffset.UtcNow; destination.Revision++;
                var subscription = new ChatSubscriptionRow
                {
                    Id = Guid.NewGuid().ToString("N"), ScopeKey = scope, ProductId = product.Id, SpawnId = spawnId, PluginId = "chat",
                    ActivationGeneration = plugin.ActivationGeneration, DestinationId = destination.Id, EventKindsJson = "[\"open-questions\"]",
                };
                var binding = new ChatConversationBindingRow
                {
                    Id = Guid.NewGuid().ToString("N"), SubscriptionId = subscription.Id, SubscriptionRevision = subscription.Revision,
                    ActivationGeneration = plugin.ActivationGeneration, ConnectionId = connection!.Id,
                    ExternalTenantId = destination.ExternalTenantId, ChannelId = destination.ChannelId, ProvisioningState = "setup-pending",
                };
                control.ChatSubscriptions.Add(subscription); control.ChatConversationBindings.Add(binding);
                var route = new Route(product, spawn, plugin, subscription, destination, connection, binding);
                await this.AddNotificationAsync(route, "setup", null, "introduction", ct);
            }
            try { await control.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new ChatConfigurationException("revision-conflict", 409); }
            await transaction.CommitAsync(ct);
            return await this.GetAsync(actor, product.Id, spawnId, ct);
        }
        finally { coordinator.Gate.Release(); }
    }

    internal async Task<string?> GuardAsync(CancellationToken ct)
    {
        if (options.Value.UnavailableReason is { } invalid) return invalid;
        if (!plugins.IsAvailable || plugins.Resolve("chat") is null) return "plugin-unavailable";
        if (!options.Value.PluginsEnabled) return options.Value.Engine == "legacy" ? "legacy-engine-active" : "host-chat-disabled";
        var slice = options.Value.TelegramSlice;
        if (!slice.Enabled) return "telegram-slice-disabled";
        if (!slice.NewTestBotConfirmed) return "new-test-bot-required";
        try
        {
            if (!Path.IsPathFullyQualified(slice.IsolatedProfileRoot)
                || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(slice.IsolatedProfileRoot)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Folder)), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Folder)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(ForgeDbPaths.DefaultFolder)), StringComparison.OrdinalIgnoreCase))
                return "isolated-profile-required";
        }
        catch (ArgumentException) { return "isolated-profile-required"; }
        if (await control.ChatNotifications.AnyAsync(n => n.Platform != Platform, ct)
            || await control.ChatSubscribers.AnyAsync(ct) || await control.ChatSpawnTopics.AnyAsync(ct) || await control.ChatBridgeState.AnyAsync(ct))
            return "empty-legacy-profile-required";
        return providers.IsAvailable && providers.Resolve("telegram") is not null ? null : "provider-unavailable";
    }

    private async Task<ProductRow> RequireAsync(ForgeActor? actor, string productId, string spawnId, bool write, CancellationToken ct)
    {
        var product = await authorizer.ResolveProductAsync(productId, ct) ?? throw new ForgeAccessDeniedException();
        await authorizer.RequireAsync(actor, write ? ForgeCapabilities.SubscriptionManage : ForgeCapabilities.SpawnRead, new(product.Id, spawnId), ct);
        return product;
    }

    private async Task<Route?> RouteAsync(string productId, string spawnId, CancellationToken ct)
    {
        var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return null;
        if (SplitMarkerCache.IsPhase73Applied(paths) && !paths.ProductDbExists(product.Slug))
            throw new ChatConfigurationException("product-db-unavailable", 503);
        var db = products.CreateFor(product.Slug);
        var spawn = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == spawnId, ct);
        var plugin = await control.SpawnPlugins.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == productId && p.SpawnId == spawnId && p.PluginId == "chat", ct);
        if (spawn is null || plugin is not { Enabled: true, SettingsVersion: 1, RetiredAt: null }
            || plugin.RunIdentity != ChatConfigurationService.RunIdentity(spawn)) return null;
        if (await control.SpawnPlugins.CountAsync(p => p.PluginId == "chat" && p.Enabled, ct) != 1
            || await control.ChatConnections.CountAsync(c => c.Enabled && c.DeletedAt == null, ct) != 1) return null;
        if (spawn.RepoId is null || !await db.Repos.AnyAsync(r => r.Id == spawn.RepoId && r.ProductId == product.Id, ct)) return null;
        var subscriptions = await control.ChatSubscriptions.AsNoTracking().Where(s => s.RetiredAt == null).Take(2).ToListAsync(ct);
        var sub = subscriptions.Count == 1 ? subscriptions[0] : null;
        if (sub is null || sub.ProductId != productId || sub.SpawnId != spawnId || sub.ScopeKind != "spawn"
            || sub.Paused || sub.Interactive || sub.ConversationPolicy != "flat" || sub.CatalogVersion != 1
            || sub.ActivationGeneration != plugin.ActivationGeneration || sub.EventKindsJson != "[\"open-questions\"]") return null;
        var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == sub.DestinationId && d.DeletedAt == null && d.AccessState == "verified", ct);
        if (destination is null) return null;
        var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == destination.ConnectionId && c.Enabled && c.DeletedAt == null && c.ProviderId == "telegram", ct);
        if (connection is null || !await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connection.Id && g.ProductId == productId, ct)) return null;
        var binding = await control.ChatConversationBindings.SingleOrDefaultAsync(b => b.SubscriptionId == sub.Id && b.SubscriptionRevision == sub.Revision
            && b.ActivationGeneration == plugin.ActivationGeneration && b.RetiredAt == null, ct);
        if (binding is null || binding.ConnectionId != connection.Id || binding.ExternalTenantId != destination.ExternalTenantId || binding.ChannelId != destination.ChannelId) return null;
        return new(product, spawn, plugin, sub, destination, connection, binding);
    }

    private async Task<IChatConnectionSession> OpenAsync(ChatConnectionRow connection, CancellationToken ct)
    {
        try
        {
            using var settings = JsonDocument.Parse(connection.SettingsJson);
            var refs = JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(connection.SecretReferencesJson);
            if (refs is null || refs.Values.Any(r => r is null)) throw new ChatConfigurationException("invalid-connection-settings", 409);
            var provider = providers.Resolve(connection.ProviderId) ?? throw new ChatConfigurationException("provider-unavailable", 409);
            var result = await provider.OpenSessionAsync(new(connection.Id, connection.ProviderId, connection.InstallationId,
                connection.SettingsVersion, connection.Revision, settings.RootElement.Clone(), refs), secrets, ct);
            return result.Succeeded ? result.Value! : throw new ChatConfigurationException(result.Failure!.Code, 409);
        }
        catch (JsonException) { throw new ChatConfigurationException("invalid-connection-settings", 409); }
    }

    private async Task AddNotificationAsync(Route route, string kind, string? workItemId, string key, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var content = policy.Create(id, kind, route.Spawn.StoryId, await this.CredentialsAsync(route.Connection, ct));
        control.ChatNotifications.Add(new()
        {
            Id = id, Kind = kind, Platform = Platform, ProductId = route.Product.Id, RepoId = route.Spawn.RepoId, SpawnId = route.Spawn.Id,
            WorkItemId = workItemId, ChatId = route.Destination.ChannelId, DedupeKey = "slice1:" + route.Binding.Id + ":" + key,
            Title = content.Title, Body = content.Body, ActionsJson = JsonSerializer.Serialize(route.Snapshot),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), Status = ChatNotificationStatuses.Pending,
        });
    }

    private async Task<List<string>> CredentialsAsync(ChatConnectionRow connection, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var reference in JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(connection.SecretReferencesJson)!.Values)
            if (await secrets.ReadAsync(reference, ct) is { } secret) result.Add(secret);
        return result;
    }

    internal async Task RetireRouteAsync(string productId, string spawnId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = ChatConfigurationScope.Spawn(productId, spawnId).Key;
        var subscriptions = await control.ChatSubscriptions.Where(s => s.ScopeKey == scope && s.RetiredAt == null).ToListAsync(ct);
        var ids = subscriptions.Select(s => s.Id).ToList();
        foreach (var sub in subscriptions) { sub.RetiredAt = now; sub.Paused = true; sub.Revision++; }
        foreach (var binding in await control.ChatConversationBindings.Where(b => ids.Contains(b.SubscriptionId) && b.RetiredAt == null).ToListAsync(ct))
        { binding.RetiredAt = now; binding.Revision++; }
        foreach (var row in await control.ChatNotifications.Where(n => n.Platform == Platform && n.ProductId == productId && n.SpawnId == spawnId && n.Status == "pending").ToListAsync(ct))
        { row.Status = ChatNotificationStatuses.Superseded; row.RespondedAt = now; }
    }
}