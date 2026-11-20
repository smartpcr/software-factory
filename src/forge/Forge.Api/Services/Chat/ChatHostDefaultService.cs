// -----------------------------------------------------------------------
// <copyright file="ChatHostDefaultService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed record ChatHostDefaultView(string HostId, bool Enabled, long Revision, IReadOnlyList<ChatRouteDraft> Routes,
    DateTimeOffset? ActivatedAt, DateTimeOffset? UpdatedAt, string? UpdatedBy);
public sealed record ChatHostDefaultWrite(long ExpectedRevision, bool Enabled, IReadOnlyList<ChatRouteDraft> Routes);

/// <summary>
/// "Set up once" for story chat (docs/chat/chat-per-workstream-conversations.md DEC-2).
/// A host administrator picks default destinations; every story spawn without chat
/// settings of its own is enabled from them. The routes are COPIED onto the story
/// (<see cref="SpawnPluginRow.CopyOrigin"/> = <see cref="Origin"/>), so delivery,
/// retirement and inbound paths keep working from per-story subscriptions, and a
/// story an operator configured (including turning it off) is never overwritten.
/// </summary>
public sealed class ChatHostDefaultService(
    ControlDbContext control, IProductDbContextFactory products, ForgeCapabilityAuthorizer authorizer,
    ChatConfigurationService configuration, ChatScopeResolver resolver, IOptions<ChatPluginOptions> options, TimeProvider clock)
{
    /// <summary><see cref="SpawnPluginRow.CopyOrigin"/> of settings materialised from the host default.</summary>
    public const string Origin = "host-default";

    public async Task<ChatHostDefaultView> GetAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        return View(await this.RowAsync(ct));
    }

    public async Task<ChatHostDefaultView> PutAsync(ForgeActor? actor, ChatHostDefaultWrite write, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        if (write.Routes is null || write.Routes.Count > 32 || write.Routes.Any(r => r is null || string.IsNullOrWhiteSpace(r.DestinationId))
            || write.Enabled && write.Routes.Count == 0
            || write.Routes.Select(r => r.DestinationId).Distinct(StringComparer.Ordinal).Count() != write.Routes.Count)
            throw new ChatConfigurationException("invalid-destinations");
        foreach (var route in write.Routes)
        {
            var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == route.DestinationId && d.DeletedAt == null, ct)
                ?? throw new ChatConfigurationException("destination-not-found", 404);
            await configuration.ValidateRouteAsync(route, destination, ct);
        }

        var row = await control.ChatHostDefaults.SingleOrDefaultAsync(r => r.HostId == options.Value.HostId, ct);
        if ((row?.Revision ?? 0) != write.ExpectedRevision) throw new ChatConfigurationException("revision-conflict", 409);
        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new ChatHostDefaultRow { HostId = options.Value.HostId, Revision = 0 };
            control.ChatHostDefaults.Add(row);
        }

        // Stories created from now on are auto-enabled; older ones only via ApplyToExistingAsync.
        if (write.Enabled && !row.Enabled) row.ActivatedAt = now;
        row.Enabled = write.Enabled;
        row.RoutesJson = JsonSerializer.Serialize(write.Routes);
        row.UpdatedAt = now;
        row.UpdatedBy = actor?.UserId;
        row.Revision++;
        await control.SaveChangesAsync(ct);
        return View(row);
    }

    /// <summary>
    /// Enable every active story (not done) that has no chat settings yet, across all
    /// products the default's connections are granted to. Explicit operator settings,
    /// including "off", are skipped. Returns the number of stories enabled.
    /// </summary>
    public async Task<int> ApplyToExistingAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        return await this.SweepAsync(includeExisting: true, ct);
    }

    /// <summary>
    /// Periodic pass (<see cref="ChatScopeReconciler"/>): enable stories created since the
    /// default was activated, and re-enable a defaulted story that started a new run.
    /// </summary>
    public Task<int> SweepAsync(CancellationToken ct = default) => this.SweepAsync(includeExisting: false, ct);

    private async Task<int> SweepAsync(bool includeExisting, CancellationToken ct)
    {
        // The single-destination Telegram slice has its own setup path.
        if (options.Value.TelegramSlice.Enabled) return 0;
        var row = await this.RowAsync(ct);
        if (row is null || !row.Enabled) return 0;
        var routes = Routes(row);
        if (routes.Count == 0) return 0;

        var applied = 0;
        foreach (var product in await control.Products.AsNoTracking().ToListAsync(ct))
        {
            if (!resolver.ProductAvailable(product)) continue;
            List<StorySpawnRow> spawns;
            try
            {
                var db = products.CreateFor(product.Slug);
                // Only this product's stories: a product without its own DB file reads the shared
                // control DB, where other products' spawns are visible too.
                var ownRepos = await db.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync(ct);
                spawns = await db.StorySpawns.AsNoTracking()
                    .Where(s => s.Phase != "done" && s.RepoId != null && ownRepos.Contains(s.RepoId)).ToListAsync(ct);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (spawns.Count == 0) continue;
            var spawnIds = spawns.Select(s => s.Id).ToList();
            var plugins = await control.SpawnPlugins.AsNoTracking()
                .Where(p => p.ProductId == product.Id && p.PluginId == "chat" && spawnIds.Contains(p.SpawnId))
                .ToDictionaryAsync(p => p.SpawnId, StringComparer.Ordinal, ct);
            foreach (var spawn in spawns)
            {
                plugins.TryGetValue(spawn.Id, out var plugin);
                if (!ShouldApply(plugin, spawn, row, includeExisting)) continue;
                if (await this.ApplyAsync(product, spawn, plugin, routes, ct)) applied++;
            }
        }

        return applied;
    }

    /// <summary>
    /// Whether the default should (re)enable this story. Never touches a story whose
    /// operator saved settings (<see cref="SpawnPluginRow.CopyOrigin"/> is not ours).
    /// </summary>
    internal static bool ShouldApply(SpawnPluginRow? plugin, StorySpawnRow spawn, ChatHostDefaultRow row, bool includeExisting)
    {
        if (!row.Enabled || spawn.Phase == "done") return false;
        if (plugin is null)
        {
            return includeExisting || row.ActivatedAt is { } activated && spawn.CreatedAt >= activated;
        }

        // A story still on the default: "apply to existing" refreshes it to the current default (e.g. a
        // channel added since, or a connection newly granted to its product); the periodic sweep only
        // re-applies when the story started a new run.
        if (plugin.CopyOrigin != Origin) return false;
        return includeExisting || plugin.RunIdentity != ChatConfigurationService.RunIdentity(spawn);
    }

    private async Task<bool> ApplyAsync(ProductRow product, StorySpawnRow spawn, SpawnPluginRow? plugin, IReadOnlyList<ChatRouteDraft> routes, CancellationToken ct)
    {
        // Only destinations whose connection this product may use.
        var granted = new List<ChatRouteDraft>();
        foreach (var route in routes)
        {
            var connectionId = await control.ChatDestinations.AsNoTracking()
                .Where(d => d.Id == route.DestinationId && d.DeletedAt == null).Select(d => d.ConnectionId).SingleOrDefaultAsync(ct);
            if (connectionId is not null
                && await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connectionId && g.ProductId == product.Id, ct))
                granted.Add(route);
        }

        if (granted.Count == 0) return false;
        try
        {
            await configuration.SaveSetupCoreAsync(product, spawn.Id,
                new ChatSetupWrite(plugin?.Revision ?? 0, true, granted, ActivateNewRun: true), Origin, ct);
            return true;
        }
        catch (ChatConfigurationException)
        {
            // A route that became invalid (provider unavailable, destination retired) leaves the
            // story unchanged; the next sweep retries once the default or connection is fixed.
            control.ChangeTracker.Clear();
            return false;
        }
    }

    private Task<ChatHostDefaultRow?> RowAsync(CancellationToken ct)
        => control.ChatHostDefaults.AsNoTracking().SingleOrDefaultAsync(r => r.HostId == options.Value.HostId, ct);

    private static IReadOnlyList<ChatRouteDraft> Routes(ChatHostDefaultRow row)
        => JsonSerializer.Deserialize<List<ChatRouteDraft>>(row.RoutesJson) ?? new List<ChatRouteDraft>();

    private ChatHostDefaultView View(ChatHostDefaultRow? row) => row is null
        ? new(options.Value.HostId, false, 0, Array.Empty<ChatRouteDraft>(), null, null, null)
        : new(row.HostId, row.Enabled, row.Revision, Routes(row), row.ActivatedAt, row.UpdatedAt, row.UpdatedBy);
}