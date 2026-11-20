// -----------------------------------------------------------------------
// <copyright file="ChatScopeLifecycleService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;

public sealed class ChatScopeLifecycleService(ControlDbContext control)
{
    public Task RetireProductAsync(string productId, CancellationToken ct = default) => RetireAsync(control, productId, null, ct);
    public Task RetireSpawnsAsync(string productId, IReadOnlyList<string> spawnIds, CancellationToken ct = default) => RetireAsync(control, productId, spawnIds, ct);

    public static async Task RetireAsync(ControlDbContext control, string productId, IReadOnlyList<string>? spawnIds, CancellationToken ct = default)
    {
        var ids = spawnIds?.ToList();
        var plugins = await control.SpawnPlugins.Where(p => p.ProductId == productId && p.PluginId == "chat"
            && (ids == null || ids.Contains(p.SpawnId)) && p.RetiredAt == null).ToListAsync(ct);
        foreach (var plugin in plugins) { plugin.Enabled = false; plugin.RetiredAt = DateTimeOffset.UtcNow; plugin.Revision++; }
        var subscriptions = await control.ChatSubscriptions.Where(s => s.ProductId == productId
            && (ids == null || ids.Contains(s.SpawnId!)) && s.RetiredAt == null).ToListAsync(ct);
        foreach (var sub in subscriptions) { sub.Paused = true; sub.RetiredAt = DateTimeOffset.UtcNow; sub.Revision++; }
        await CancelSubscriptionsAsync(control, subscriptions.Select(s => s.Id).ToList(), "parent-deleted", ct);
        var subIds = subscriptions.Select(s => s.Id).ToList();
        foreach (var binding in await control.ChatConversationBindings.Where(b => subIds.Contains(b.SubscriptionId) && b.RetiredAt == null).ToListAsync(ct))
        { binding.RetiredAt = DateTimeOffset.UtcNow; binding.ProvisioningState = "orphaned"; binding.Revision++; }
        if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(ct);
    }

    internal static async Task CancelSubscriptionsAsync(ControlDbContext control, List<string> ids, string reason, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(100))
        {
            var batch = chunk.ToList();
            var deliveries = await control.ChatDeliveries.Where(d => batch.Contains(d.SubscriptionId) && d.TerminalAt == null).ToListAsync(ct);
            foreach (var delivery in deliveries)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (delivery.State is not ("uncertain" or "sending")) delivery.State = "cancelled";
                delivery.TerminalAt = now; delivery.LastError = reason;
                var messages = await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id).ToListAsync(ct);
                foreach (var message in messages)
                {
                    if (!message.ControlsRetired) { message.ControlsRetired = true; message.DesiredRevision++; }
                    message.TerminalAt = now;
                    if (message.State == "sent") message.EditState = "pending";
                    else if (message.State == "pending") message.State = "cancelled";
                    foreach (var token in await control.ChatActionTokens.Where(t => t.DeliveryMessageId == message.Id && t.State == "active").ToListAsync(ct)) token.State = "retired";
                }
            }
        }
    }

    internal static async Task ConfigurationChangedAsync(ControlDbContext control, CancellationToken ct)
    {
        control.ChangeTracker.DetectChanges();
        var subscriptions = control.ChangeTracker.Entries<ChatSubscriptionRow>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity.Id).ToList();
        var connections = control.ChangeTracker.Entries<ChatConnectionRow>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity.Id).ToList();
        var destinations = control.ChangeTracker.Entries<ChatDestinationRow>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity.Id).ToList();
        foreach (var plugin in control.ChangeTracker.Entries<SpawnPluginRow>().Where(e => e.State == EntityState.Modified
            && (!e.Entity.Enabled || e.Property(p => p.ActivationGeneration).IsModified || e.Property(p => p.RunIdentity).IsModified)).Select(e => e.Entity).ToArray())
            subscriptions.AddRange(await control.ChatSubscriptions.Where(s => s.ProductId == plugin.ProductId && s.SpawnId == plugin.SpawnId).Select(s => s.Id).ToListAsync(ct));
        destinations.AddRange(await control.ChatDestinations.Where(d => connections.Contains(d.ConnectionId)).Select(d => d.Id).ToListAsync(ct));
        subscriptions.AddRange(await control.ChatSubscriptions.Where(s => destinations.Contains(s.DestinationId)).Select(s => s.Id).ToListAsync(ct));
        await CancelSubscriptionsAsync(control, subscriptions.Distinct(StringComparer.Ordinal).ToList(), "configuration-changed", ct);
    }
}

public sealed class ChatScopeReconciler(ControlDbContext control, IProductDbContextFactory products, ChatScopeResolver resolver,
    ChatHostDefaultService? hostDefaults = null)
{
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        // "Set up once": enable new stories from the host default before reconciling, so a
        // story spawned since the last pass is picked up in this one.
        if (hostDefaults is not null) await hostDefaults.SweepAsync(ct);

        var active = await control.SpawnPlugins.Where(p => p.PluginId == "chat" && p.RetiredAt == null).ToListAsync(ct);
        foreach (var group in active.GroupBy(p => p.ProductId))
        {
            var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == group.Key, ct);
            if (product is null) { await ChatScopeLifecycleService.RetireAsync(control, group.Key, null, ct); continue; }
            var available = resolver.ProductAvailable(product);
            ProductDbContext? db = available ? products.CreateFor(product.Slug) : null;
            foreach (var batch in group.Chunk(100))
            {
                var ids = batch.Select(p => p.SpawnId).ToList();
                Dictionary<string, StorySpawnRow>? spawns = null;
                if (db is not null)
                {
                    try { spawns = await db.StorySpawns.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct); }
                    catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { }
                }
                if (spawns is null)
                {
                    var subIds = await control.ChatSubscriptions.Where(s => s.ProductId == product.Id && ids.Contains(s.SpawnId!)).Select(s => s.Id).ToListAsync(ct);
                    foreach (var binding in await control.ChatConversationBindings.Where(b => subIds.Contains(b.SubscriptionId) && b.RetiredAt == null).ToListAsync(ct))
                        if (binding.ProvisioningState is "pending" or "ready") binding.ProvisioningState = "degraded";
                    if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(ct);
                    continue;
                }
                // Products without their own DB file share the control DB, so a spawn of another product
                // can be visible here; there it only belongs to this product when its repo does.
                HashSet<string>? ownRepos = resolver.SharesControlDb(product)
                    ? (await db!.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal)
                    : null;
                var missing = batch.Where(p => !spawns.TryGetValue(p.SpawnId, out var spawn) || ChatConfigurationService.RunIdentity(spawn) != p.RunIdentity
                    || ownRepos is not null && (spawn.RepoId is null || !ownRepos.Contains(spawn.RepoId))).Select(p => p.SpawnId).ToList();
                if (missing.Count > 0) await ChatScopeLifecycleService.RetireAsync(control, product.Id, missing, ct);
                var liveIds = ids.Except(missing).ToList();
                var liveSubscriptions = await control.ChatSubscriptions.Where(s => s.ProductId == product.Id && liveIds.Contains(s.SpawnId!)).Select(s => s.Id).ToListAsync(ct);
                foreach (var binding in await control.ChatConversationBindings.Where(b => liveSubscriptions.Contains(b.SubscriptionId) && b.ProvisioningState == "degraded" && b.RetiredAt == null).ToListAsync(ct))
                    binding.ProvisioningState = binding.ConversationId is not null ? "ready" : "pending";
                await RetireFinishedWorkstreamBindingsAsync(control, db!, liveSubscriptions, ct);
                var slackBindings = await control.ChatConversationBindings.Where(b => liveSubscriptions.Contains(b.SubscriptionId) && b.RetiredAt == null
                    && control.ChatConnections.Any(c => c.Id == b.ConnectionId && c.ProviderId == "slack")).ToListAsync(ct);
                if (slackBindings.Count > 0)
                {
                    var parents = await control.ChatSubscriptions.Where(s => liveSubscriptions.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.SpawnId!, ct);
                    foreach (var binding in slackBindings)
                    {
                        var desired = spawns[parents[binding.SubscriptionId]].Phase == "done" ? "completed" : "active";
                        if (binding.RootDesiredState != desired) { binding.RootDesiredState = desired; binding.RootNextAttemptAt = 0; }
                    }
                }
                if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>
    /// A per-workstream conversation ends with its workstream: once the workstream is
    /// complete, cancelled, historic or gone, retire its binding. The delivery loop's
    /// retirement pass then closes the Telegram topic (idempotent closeForumTopic) or
    /// marks the Slack root retired. Story conversations (ChildKey empty) and live
    /// siblings are untouched. A workstream restarted later gets a new conversation on
    /// its next message.
    /// </summary>
    internal static async Task<int> RetireFinishedWorkstreamBindingsAsync(
        ControlDbContext control, ProductDbContext db, IReadOnlyCollection<string> liveSubscriptions, CancellationToken ct)
    {
        var childBindings = await control.ChatConversationBindings
            .Where(b => liveSubscriptions.Contains(b.SubscriptionId) && b.RetiredAt == null && b.ChildKey != "").ToListAsync(ct);
        if (childBindings.Count == 0) return 0;
        var keys = childBindings.Select(b => b.ChildKey).Distinct(StringComparer.Ordinal).ToList();
        var statuses = await db.Workstreams.AsNoTracking().Where(w => keys.Contains(w.Id))
            .Select(w => new { w.Id, w.Status }).ToDictionaryAsync(w => w.Id, w => w.Status, StringComparer.Ordinal, ct);
        var retired = 0;
        foreach (var binding in childBindings)
        {
            var finished = !statuses.TryGetValue(binding.ChildKey, out var status)
                || status is "complete" or "cancelled" or WorkstreamStatuses.Historic;
            if (!finished) continue;
            binding.RetiredAt = DateTimeOffset.UtcNow; binding.Revision++;
            retired++;
        }

        return retired;
    }
}