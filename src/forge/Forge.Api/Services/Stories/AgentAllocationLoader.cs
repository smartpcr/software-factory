// -----------------------------------------------------------------------
// <copyright file="AgentAllocationLoader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — server-side mirror of the UI's
/// <c>AGENT_ALLOCATION</c>. Loads <c>agents/allocation.json</c> at the repo
/// root so the Phase 28 orchestrator can answer "give me the agents on team
/// X" without round-tripping to the UI.
/// </summary>
public sealed class AgentAllocationLoader
{
    private readonly Lazy<IReadOnlyList<AgentAllocationEntry>> entries;

    public AgentAllocationLoader()
    {
        this.entries = new Lazy<IReadOnlyList<AgentAllocationEntry>>(this.Load);
    }

    public IReadOnlyList<AgentAllocationEntry> All => this.entries.Value;

    public IReadOnlyList<AgentAllocationEntry> ForTeam(string team) =>
        this.All.Where(a => string.Equals(a.Team, team, StringComparison.OrdinalIgnoreCase)).ToArray();

    private IReadOnlyList<AgentAllocationEntry> Load()
    {
        try
        {
            var path = Path.Combine(ForgeSeed.FindRepoRoot(), "agents", "allocation.json");
            if (!File.Exists(path)) return Array.Empty<AgentAllocationEntry>();
            using var stream = File.OpenRead(path);
            var doc = JsonDocument.Parse(stream);
            var rows = new List<AgentAllocationEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                rows.Add(new AgentAllocationEntry(
                    el.GetProperty("id").GetString() ?? string.Empty,
                    el.GetProperty("name").GetString() ?? string.Empty,
                    GetStringOrEmpty(el, "role_label"),
                    GetStringOrEmpty(el, "team"),
                    GetStringOrEmpty(el, "pod"),
                    GetStringOrEmpty(el, "role_bucket"),
                    GetStringOrEmpty(el, "crew_role")));
            }
            return rows;
        }
        catch
        {
            return Array.Empty<AgentAllocationEntry>();
        }
    }

    private static string GetStringOrEmpty(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}

public sealed record AgentAllocationEntry(
    string Id,
    string Name,
    string RoleLabel,
    string Team,
    string Pod,
    string RoleBucket,
    string CrewRole);
