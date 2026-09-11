// -----------------------------------------------------------------------
// <copyright file="ChatSummaryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed record ChatDestinationSummary(string DestinationId, string ConnectionId, string ProviderId, string DisplayName);
public sealed record ChatRowSummary(string State, int UnroutedCount, IReadOnlyDictionary<string, int> UnroutedReasons,
    int PendingCount, int FailedCount, int SentCount, long Revision = 0, IReadOnlyList<ChatDestinationSummary>? Destinations = null);

/// <summary>Exactly one control query per hundred existing parent rows. No provider or product-DB lookups.</summary>
public static class ChatSummaryService
{
    public static async Task<IReadOnlyDictionary<string, ChatRowSummary>> ReadAsync(ControlDbContext control,
        IReadOnlyList<(StorySpawnRow Spawn, string ProductId)> rows, CancellationToken ct = default, ChatPluginOptions? options = null)
    {
        var result = new Dictionary<string, ChatRowSummary>(StringComparer.Ordinal);
        foreach (var chunk in rows.DistinctBy(r => (r.ProductId, r.Spawn.Id)).Chunk(100))
        {
            var parameters = new List<object>();
            var values = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                values.Add($"(@k{i},@p{i},@s{i})");
                parameters.Add(new SqliteParameter($"@k{i}", ChatConfigurationScope.Spawn(chunk[i].ProductId, chunk[i].Spawn.Id).Key));
                parameters.Add(new SqliteParameter($"@p{i}", chunk[i].ProductId));
                parameters.Add(new SqliteParameter($"@s{i}", chunk[i].Spawn.Id));
            }
            var sql = "WITH requested(ScopeKey,ProductId,SpawnId) AS (VALUES " + string.Join(",", values) + ") " + """
                SELECT r.ScopeKey, COALESCE(p.Enabled,0) AS Enabled, COALESCE(p.Revision,0) AS Revision,
                  COALESCE(p.RunIdentity,'') AS RunIdentity, COALESCE(p.SettingsVersion,1) AS SettingsVersion,
                  (SELECT json_group_array(json_object('DestinationId',d.Id,'ConnectionId',c.Id,'ProviderId',c.ProviderId,
                    'DisplayName',d.DisplayName,'AccessState',d.AccessState,'Health',h.Health,'Enabled',c.Enabled,'Paused',s.Paused,
                    'BindingState',(SELECT b.ProvisioningState FROM chat_conversation_bindings b WHERE b.SubscriptionId=s.Id AND b.RetiredAt IS NULL LIMIT 1)))
                   FROM chat_subscriptions s JOIN chat_destinations d ON d.Id=s.DestinationId AND d.DeletedAt IS NULL
                   JOIN chat_connections c ON c.Id=d.ConnectionId AND c.DeletedAt IS NULL
                   JOIN chat_connection_products g ON g.ConnectionId=c.Id AND g.ProductId=r.ProductId
                   LEFT JOIN chat_connection_state h ON h.ConnectionId=c.Id
                   WHERE s.ScopeKey=r.ScopeKey AND s.RetiredAt IS NULL AND s.ActivationGeneration=p.ActivationGeneration) AS Routes,
                  (SELECT json_group_array(json_object('State',dc.State,'Count',dc.Cnt)) FROM
                    (SELECT d.State,COUNT(*) AS Cnt FROM chat_deliveries d JOIN chat_subscriptions s ON s.Id=d.SubscriptionId
                     WHERE s.ScopeKey=r.ScopeKey GROUP BY d.State) dc) AS Counts,
                  (SELECT json_group_array(json_object('State',COALESCE(nc.RouteReason,'unknown'),'Count',nc.Cnt)) FROM
                    (SELECT n.RouteReason,COUNT(*) AS Cnt FROM chat_notifications n WHERE n.Platform='plugins' AND n.ScopeKind='spawn'
                     AND n.ProductId=r.ProductId AND n.SpawnId=r.SpawnId AND n.RouteState='unrouted' GROUP BY n.RouteReason) nc) AS Reasons
                FROM requested r LEFT JOIN spawn_plugins p ON p.ProductId=r.ProductId AND p.SpawnId=r.SpawnId AND p.PluginId='chat'
                """;
            var found = await control.Database.SqlQueryRaw<SummaryRecord>(sql, parameters.ToArray()).ToListAsync(ct);
            foreach (var item in found)
            {
                var row = chunk.Single(r => ChatConfigurationScope.Spawn(r.ProductId, r.Spawn.Id).Key == item.ScopeKey);
                var routes = JsonSerializer.Deserialize<RouteRecord[]>(item.Routes)!;
                var counts = JsonSerializer.Deserialize<CountRecord[]>(item.Counts)!.ToDictionary(c => c.State, c => c.Count);
                var reasons = JsonSerializer.Deserialize<CountRecord[]>(item.Reasons)!.ToDictionary(c => c.State, c => c.Count);
                var pending = counts.GetValueOrDefault("pending") + counts.GetValueOrDefault("retry") + counts.GetValueOrDefault("sending");
                var failed = counts.GetValueOrDefault("failed") + counts.GetValueOrDefault("uncertain");
                var current = item.RunIdentity == ChatConfigurationService.RunIdentity(row.Spawn);
                var state = !item.Enabled || !current ? "off" : routes.Length == 0 || item.SettingsVersion != 1 ? "needs-setup"
                    : failed > 0 || routes.Any(r => r.Health == "degraded" || r.AccessState == "unavailable") ? "degraded"
                    : options is not null && (!options.PluginsEnabled || options.UnavailableReason is not null) ? "needs-setup"
                    : routes.Any(r => r.Enabled == 0 || r.Paused != 0 || r.AccessState != "ready" || r.BindingState != "ready") ? "needs-setup" : "ready";
                result[item.ScopeKey] = new(state, reasons.Values.Sum(), reasons, pending, failed, counts.GetValueOrDefault("sent"), item.Revision,
                    current ? routes.Select(r => new ChatDestinationSummary(r.DestinationId, r.ConnectionId, r.ProviderId, r.DisplayName)).ToArray() : []);
            }
        }
        return result;
    }

    private sealed record CountRecord(string State, int Count);
    private sealed record RouteRecord(string DestinationId, string ConnectionId, string ProviderId, string DisplayName, string AccessState, string? Health, int Enabled, int Paused, string? BindingState);
    public sealed class SummaryRecord
    {
        public string ScopeKey { get; set; } = "";
        public bool Enabled { get; set; }
        public long Revision { get; set; }
        public string RunIdentity { get; set; } = "";
        public int SettingsVersion { get; set; }
        public string Routes { get; set; } = "[]";
        public string Counts { get; set; } = "[]";
        public string Reasons { get; set; } = "[]";
    }
}