// -----------------------------------------------------------------------
// <copyright file="AdoMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Maps the raw ADO REST shapes (JSON) into our card / PR contracts.
///
/// Field mapping for work items (per Phase 5.5):
///   * <c>System.State</c>           → <c>Card.Col</c>
///   * <c>System.AreaPath</c>        → <c>Card.Epic</c> (last segment)
///   * <c>System.Tags</c> ("a; b")   → <c>Card.Tags</c>
///   * <c>Microsoft.VSTS.Scheduling.StoryPoints</c> → <c>Card.Pts</c>
///   * <c>System.AssignedTo</c>      → <c>Card.Agent</c> with role = "human"
///   * <c>System.WorkItemType</c>    → <c>Card.Type</c>
///   * <c>System.Title</c>           → <c>Card.Title</c>
/// </summary>
public static class AdoMapper
{
    /// <summary>State → board column. Anything unknown lands in "backlog".</summary>
    public static string MapState(string? state) => (state ?? string.Empty).ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" => "backlog",
        "approved" or "design" or "ready" => "design",
        "active" or "in progress" or "committed" or "doing" => "build",
        "code review" or "in review" or "review" or "resolved" => "review",
        "closed" or "done" or "completed" or "removed" => "done",
        _ => "backlog",
    };

    public static string MapType(string? wit) => (wit ?? string.Empty).ToLowerInvariant() switch
    {
        "bug" => "bug",
        "task" => "task",
        _ => "story",
    };

    public static IReadOnlyList<string> MapTags(string? semicolonList)
    {
        if (string.IsNullOrWhiteSpace(semicolonList)) return Array.Empty<string>();
        return semicolonList
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>Last segment of <c>System.AreaPath</c> ("contoso\Backend\Auth" → "Auth").</summary>
    public static string MapEpic(string? areaPath, string projectFallback)
    {
        if (string.IsNullOrWhiteSpace(areaPath)) return projectFallback;
        var i = areaPath.LastIndexOfAny(new[] { '\\', '/' });
        return i < 0 ? areaPath : areaPath[(i + 1)..];
    }

    public static Agent? MapAgent(JsonElement assignedTo)
    {
        if (assignedTo.ValueKind != JsonValueKind.Object) return null;
        var name = assignedTo.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        var unique = assignedTo.TryGetProperty("uniqueName", out var un) ? un.GetString() : null;
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(unique)) return null;
        // Initials of the display name → "id" so the existing agent-chip CSS matches.
        var id = MakeInitials(name ?? unique!);
        return new Agent(Id: id, Role: "human", Name: name ?? unique!);
    }

    private static string MakeInitials(string s)
    {
        var parts = s.Split(new[] { ' ', '.', '@', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return s.Length <= 2 ? s.ToUpperInvariant() : s[..2].ToUpperInvariant();
        if (parts.Length == 1) return parts[0].Length <= 2 ? parts[0].ToUpperInvariant() : parts[0][..2].ToUpperInvariant();
        return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }

    /// <summary>Maps an ADO work-item JSON envelope into a <see cref="Card"/>.</summary>
    public static Card ToCard(JsonElement workItem, string projectFallback, string defaultEpic)
    {
        var id = "#" + workItem.GetProperty("id").GetInt64();
        var fields = workItem.GetProperty("fields");
        string? get(string key) =>
            fields.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var state = get("System.State");
        var col = MapState(state);
        var type = MapType(get("System.WorkItemType"));
        var title = get("System.Title") ?? "(no title)";
        var tags = MapTags(get("System.Tags"));
        var pts = 0;
        if (fields.TryGetProperty("Microsoft.VSTS.Scheduling.StoryPoints", out var sp) && sp.TryGetDouble(out var d))
        {
            pts = (int)Math.Round(d);
        }
        var epic = MapEpic(get("System.AreaPath"), defaultEpic);
        Agent? agent = null;
        if (fields.TryGetProperty("System.AssignedTo", out var at)) agent = MapAgent(at);

        var priority = (get("Microsoft.VSTS.Common.Priority") ?? "3") switch
        {
            "1" => "high",
            "2" => "high",
            "3" => "med",
            _ => "low",
        };

        return new Card(
            Id: id,
            Epic: epic,
            Col: col,
            Type: type,
            Title: title,
            Tags: tags,
            Pts: pts,
            Priority: priority,
            Agent: agent,
            Cost: 0,
            AutoNext: null,
            Live: false,
            Progress: null,
            Branch: null,
            HumanGate: false,
            GateReason: null,
            Blocked: false,
            BlockedReason: null,
            DoneAt: col == "done" ? "recently" : null);
    }

    /// <summary>Maps an ADO PR JSON envelope into a <see cref="Pr"/>.</summary>
    public static Pr ToPr(JsonElement pr, string epicFallback)
    {
        var id = "PR #" + pr.GetProperty("pullRequestId").GetInt64();
        var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;

        // ADO returns "refs/heads/branch-name"; trim the prefix.
        string TrimRef(string? r) => r is null ? string.Empty
            : r.StartsWith("refs/heads/") ? r["refs/heads/".Length..] : r;

        var branch = TrimRef(pr.TryGetProperty("sourceRefName", out var s) ? s.GetString() : null);
        var baseBranch = TrimRef(pr.TryGetProperty("targetRefName", out var b) ? b.GetString() : null);

        var status = pr.TryGetProperty("status", out var st) ? st.GetString() : null; // "active" | "completed" | "abandoned"
        var col = status?.ToLowerInvariant() switch
        {
            "active" => "review",
            "completed" => "merged",
            _ => "review",
        };

        Agent author = new("XX", "human", "unknown");
        if (pr.TryGetProperty("createdBy", out var cb))
        {
            author = MapAgent(cb) ?? author;
        }

        var hasConflict = false;
        if (pr.TryGetProperty("mergeStatus", out var ms))
        {
            hasConflict = string.Equals(ms.GetString(), "conflicts", StringComparison.OrdinalIgnoreCase);
        }

        return new Pr(
            Id: id,
            Col: col,
            Epic: epicFallback,
            EpicColor: epicFallback,
            Workstream: branch,
            Base: baseBranch,
            Branch: branch,
            Title: title,
            Author: author,
            Additions: 0,
            Deletions: 0,
            Files: 0,
            Commits: 0,
            Age: pr.TryGetProperty("creationDate", out var cd) && cd.ValueKind == JsonValueKind.String
                ? RelativeAge(cd.GetString())
                : "—",
            Build: null,
            Review: null,
            Conflicts: hasConflict,
            HumanGate: false);
    }

    private static string RelativeAge(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, out var when)) return "—";
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }
}
