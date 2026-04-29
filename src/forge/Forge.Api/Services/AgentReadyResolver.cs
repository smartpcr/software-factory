// -----------------------------------------------------------------------
// <copyright file="AgentReadyResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;

/// <summary>
/// Phase 23.6 — pure helper that applies the <c>agent-ready</c> auto-assign
/// rule. When upstream tags an item <c>agent-ready</c> and the item has no
/// meaningful WIT-shape assignee, the resolver writes a team-shape
/// <c>AssignedToJson</c> value and returns metadata for audit + SignalR.
/// <para>
/// The resolver performs no DB writes itself — the caller commits the
/// mutation in its own transaction and writes the audit row.
/// </para>
/// </summary>
public static class AgentReadyResolver
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Result of the <see cref="Apply"/> call. When <see cref="WasAssigned"/>
    /// is <c>true</c>, the caller should persist <see cref="NewAssignedToJson"/>
    /// and write a <c>forge_actions(kind='auto-assigned-by-flag')</c> audit row.
    /// </summary>
    public sealed record AgentReadyResult(
        bool WasAssigned,
        string? NewAssignedToJson,
        string? TeamName);

    /// <summary>
    /// Evaluate whether the <c>agent-ready</c> flag should auto-assign the
    /// item to a default team.
    /// </summary>
    /// <param name="assignedAgentJson">
    /// The current <c>WorkItemRow.AssignedAgentJson</c> value (Phase 22).
    /// When non-null, an agent is already working — skip auto-assign.
    /// </param>
    /// <param name="tags">The tags array from the upstream work item.</param>
    /// <param name="agentReadyDefaultTeam">
    /// Per-repo override; falls back to <see cref="SyncConstants.AgentReadyDefaultTeam"/>.
    /// </param>
    public static AgentReadyResult Apply(
        string? assignedAgentJson,
        IReadOnlyList<string> tags,
        string? agentReadyDefaultTeam)
    {
        // No flag → no-op.
        if (!HasAgentReadyFlag(tags))
        {
            return new AgentReadyResult(false, null, null);
        }

        // If an agent is already working on this item (Phase 22
        // AssignedAgent is set), skip — don't re-route mid-execution.
        if (!string.IsNullOrWhiteSpace(assignedAgentJson))
        {
            return new AgentReadyResult(false, null, null);
        }

        // Auto-assign to the default team.
        var team = string.IsNullOrWhiteSpace(agentReadyDefaultTeam)
            ? SyncConstants.AgentReadyDefaultTeam
            : agentReadyDefaultTeam;

        var newJson = JsonSerializer.Serialize(
            new { kind = "team", id = team, displayName = team },
            JsonOpts);

        return new AgentReadyResult(true, newJson, team);
    }

    /// <summary>Case-insensitive check for the <c>agent-ready</c> tag.</summary>
    internal static bool HasAgentReadyFlag(IReadOnlyList<string> tags)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i], SyncConstants.AgentReadyFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="json"/> is a well-formed
    /// Phase 22 WIT-shape assignee: an object with a <c>kind</c> property
    /// whose value is one of <c>agent</c>, <c>human</c>, or <c>team</c>.
    /// Legacy runtime-shape values (no <c>kind</c> property) return <c>false</c>
    /// so the auto-assign overwrites them (Q22.7 backfill).
    /// </summary>
    internal static bool IsWitShapeAssignee(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("kind", out var kindProp)
                && kindProp.ValueKind == JsonValueKind.String)
            {
                var kind = kindProp.GetString();
                return kind is "agent" or "human" or "team";
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
