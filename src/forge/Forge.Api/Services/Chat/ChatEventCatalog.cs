// -----------------------------------------------------------------------
// <copyright file="ChatEventCatalog.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;

public static class ChatEventCatalog
{
    public const int Version = 1;
    public static readonly IReadOnlyList<string> SpawnDefaults = new[] { "open-questions", "workstream-stalled" };
    public static readonly IReadOnlyList<string> HostDefaults = new[] { "queue-strain", "human-interaction" };
    /// <summary>The orchestrator halted the story (its LastError banner). Routed to every subscription that takes stall alerts.</summary>
    public const string SpawnHalted = "spawn-halted";
    /// <summary>Story-scope events that carry operator controls.</summary>
    public static readonly IReadOnlyList<string> SpawnAlerts = new[] { "open-questions", "workstream-stalled", SpawnHalted };
    public static bool Known(string kind) => SpawnAlerts.Contains(kind) || HostDefaults.Contains(kind);
    public static bool Matches(ChatSubscriptionRow subscription, string kind)
    {
        if (subscription.CatalogVersion != Version || !Known(kind)) return false;
        try
        {
            var kinds = JsonSerializer.Deserialize<string[]>(subscription.EventKindsJson) ?? [];
            // A halted story is the story-level form of a stall: anyone receiving stall alerts gets it,
            // without migrating every saved event filter.
            return kinds.Contains(kind, StringComparer.Ordinal) || kind == SpawnHalted && kinds.Contains("workstream-stalled", StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record ChatQuestionPayload(string Id, long Revision, string? Text, IReadOnlyList<string>? Choices = null);
// TargetLabel: human label of the target, e.g. "Stage 1.2 — Gate Runner Core"; null for the story itself.
public sealed record ChatEventPayload(int Version, string Title, string Body, IReadOnlyList<ChatQuestionPayload> Questions,
    IReadOnlyList<ChatSemanticAction> LegacyActions, string? StoryId = null, string? TargetLabel = null);
public sealed record ChatEventInput(string Kind, string DedupeKey, string? ProductId = null, string? SpawnId = null,
    string? WorkItemId = null, string? WorkstreamId = null, string? RepoId = null, long Revision = 1, string? ArtifactKey = null, string? LegacyDedupeKey = null,
    string? Detail = null);
public sealed record ChatDeliveryTarget(int Version, string ScopeKind, string? ProductId, string? SpawnId, string? HostId,
    string? RunIdentity, long ActivationGeneration, string SubscriptionId, long SubscriptionRevision,
    string DestinationId, long DestinationRevision, string ConnectionId, long ConnectionRevision, string ProviderId,
    string ExternalTenantId, string ChannelId, string ConversationPolicy, bool Interactive);

public static class ChatPayloadConverter
{
    public static ChatEventPayload? Read(ChatNotificationRow row)
    {
        try
        {
            if (row.PayloadVersion == 1)
            {
                var payload = JsonSerializer.Deserialize<ChatEventPayload>(row.PayloadJson ?? "null");
                return payload is { Version: 1, Questions: not null, LegacyActions: not null } ? payload : null;
            }
            if (row.PayloadVersion != 0 || row.Platform is "plugins" or "telegram-1s") return null;
            // Conversion is explicit, versioned and never dispatches old callback strings as new action tokens.
            return new(1, ChatContentPolicy.Redact(row.Title, []), ChatContentPolicy.Redact(row.Body, []), [],
                (ChatActions.Parse(row.ActionsJson) ?? []).Select(a => new ChatSemanticAction(
                    ChatContentPolicy.Redact(a.Label, []), ChatContentPolicy.Redact(a.CallbackData, []), "legacy-reference")).ToArray());
        }
        catch (JsonException) { return null; }
    }
}