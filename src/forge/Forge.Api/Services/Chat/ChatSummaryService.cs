// -----------------------------------------------------------------------
// <copyright file="ChatSummaryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record ChatRowSummary(string State, int UnroutedCount, IReadOnlyDictionary<string, int> UnroutedReasons,
    int PendingCount, int FailedCount, int SentCount);

/// <summary>Joins requested parent rows in control-DB batches; never opens a product database per row.</summary>
public static class ChatSummaryService
{
    public static async Task<IReadOnlyDictionary<string, ChatRowSummary>> ReadAsync(ControlDbContext control,
        IReadOnlyList<(StorySpawnRow Spawn, string ProductId)> rows, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ChatRowSummary>(StringComparer.Ordinal);
        foreach (var chunk in rows.Chunk(100))
        {
            var keys = chunk.Select(r => ChatConfigurationScope.Spawn(r.ProductId, r.Spawn.Id).Key).ToList();
            var productIds = chunk.Select(r => r.ProductId).Distinct().ToList(); var spawnIds = chunk.Select(r => r.Spawn.Id).Distinct().ToList();
            var plugins = await control.SpawnPlugins.AsNoTracking().Where(p => p.PluginId == "chat" && productIds.Contains(p.ProductId) && spawnIds.Contains(p.SpawnId)).ToListAsync(ct);
            var subscriptions = await control.ChatSubscriptions.AsNoTracking().Where(s => keys.Contains(s.ScopeKey) && s.RetiredAt == null).ToListAsync(ct);
            var health = await (from s in control.ChatSubscriptions.AsNoTracking()
                join d in control.ChatDestinations on s.DestinationId equals d.Id
                join h in control.ChatConnectionStates on d.ConnectionId equals h.ConnectionId
                where keys.Contains(s.ScopeKey) && s.RetiredAt == null && !s.Paused && h.Health == "degraded"
                select s.ScopeKey).Distinct().ToListAsync(ct);
            var counts = await (from d in control.ChatDeliveries.AsNoTracking() join s in control.ChatSubscriptions on d.SubscriptionId equals s.Id
                where keys.Contains(s.ScopeKey) group d by new { s.ScopeKey, d.State } into g select new { g.Key.ScopeKey, g.Key.State, Count = g.Count() }).ToListAsync(ct);
            var unrouted = await control.ChatNotifications.AsNoTracking().Where(n => n.Platform == "plugins" && n.ScopeKind == "spawn" && n.RouteState == "unrouted"
                && productIds.Contains(n.ProductId) && spawnIds.Contains(n.SpawnId!)).GroupBy(n => new { n.ProductId, n.SpawnId, n.RouteReason })
                .Select(g => new { g.Key.ProductId, g.Key.SpawnId, g.Key.RouteReason, Count = g.Count() }).ToListAsync(ct);
            foreach (var row in chunk)
            {
                var key = ChatConfigurationScope.Spawn(row.ProductId, row.Spawn.Id).Key;
                var plugin = plugins.SingleOrDefault(p => p.ProductId == row.ProductId && p.SpawnId == row.Spawn.Id);
                var grouped = counts.Where(c => c.ScopeKey == key).ToDictionary(c => c.State, c => c.Count);
                var reasons = unrouted.Where(n => n.ProductId == row.ProductId && n.SpawnId == row.Spawn.Id).ToDictionary(n => n.RouteReason ?? "unknown", n => n.Count);
                var pending = grouped.GetValueOrDefault("pending") + grouped.GetValueOrDefault("retry") + grouped.GetValueOrDefault("sending");
                var failed = grouped.GetValueOrDefault("failed") + grouped.GetValueOrDefault("uncertain"); var sent = grouped.GetValueOrDefault("sent");
                var state = plugin is null || !plugin.Enabled || plugin.RunIdentity != ChatConfigurationService.RunIdentity(row.Spawn) ? "off"
                    : !subscriptions.Any(s => s.ScopeKey == key && !s.Paused) ? "needs-setup" : failed > 0 || health.Contains(key) ? "degraded" : pending > 0 ? "pending" : sent > 0 ? "ready" : "configured";
                result[key] = new(state, reasons.Values.Sum(), reasons, pending, failed, sent);
            }
        }
        return result;
    }
}