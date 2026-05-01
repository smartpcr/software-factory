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
        var id = MakeInitials(name ?? unique!);
        var imageUrl = assignedTo.TryGetProperty("imageUrl", out var img) ? img.GetString() : null;
        // ADO _avatar URLs need &size=2 for a small image.
        if (!string.IsNullOrEmpty(imageUrl) && !imageUrl.Contains("size="))
        {
            imageUrl += (imageUrl.Contains('?') ? "&" : "?") + "size=2";
        }
        return new Agent(Id: id, Role: "human", Name: name ?? unique!, ImageUrl: imageUrl);
    }

    private static string MakeInitials(string s)
    {
        var parts = s.Split(new[] { ' ', '.', '@', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return s.Length <= 2 ? s.ToUpperInvariant() : s[..2].ToUpperInvariant();
        if (parts.Length == 1) return parts[0].Length <= 2 ? parts[0].ToUpperInvariant() : parts[0][..2].ToUpperInvariant();
        return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }

    /// <summary>
    /// Phase 12.4 — map an ADO WIT envelope into the rich
    /// <see cref="WorkItem"/> shape consumed by the work-items sync service.
    /// Differs from <see cref="ToCard"/>: keeps `System.State` straight,
    /// extracts `System.IterationPath`, walks the `relations` collection
    /// for the parent link, and preserves `System.Rev` as the conflict
    /// revision marker.
    /// </summary>
    public static WorkItem ToWorkItem(JsonElement wi, string repoId, string iterationRefPrefix)
    {
        var idLong = wi.GetProperty("id").GetInt64();
        var fields = wi.GetProperty("fields");

        string? get(string key) =>
            fields.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var typeAdo = get("System.WorkItemType");
        var (track, type) = MapTypeAndTrack(typeAdo);
        var state = MapWorkItemState(get("System.State"));
        var title = get("System.Title") ?? "(no title)";
        var area = get("System.AreaPath") ?? string.Empty;
        var iter = get("System.IterationPath") ?? string.Empty;
        var iterationRef = string.IsNullOrEmpty(iter)
            ? string.Empty
            : $"{iterationRefPrefix}:{iter}";
        var tags = MapTags(get("System.Tags"));
        // Phase 14 follow-up — combine the four most useful long-text ADO
        // fields into one markdown blob so the work-item detail panel
        // surfaces everything the operator needs in-place. ADO returns
        // these as HTML; we keep the raw HTML inside fenced sections (the
        // markdown renderer will pass them through). Empty sections are
        // dropped so the resulting blob isn't padded with placeholders.
        var description = ComposeDescription(
            get("System.Description"),
            get("Microsoft.VSTS.TCM.ReproSteps"),
            get("Microsoft.VSTS.TCM.SystemInfo"),
            get("Microsoft.VSTS.Common.AcceptanceCriteria"));

        var pts = 0;
        if (fields.TryGetProperty("Microsoft.VSTS.Scheduling.StoryPoints", out var sp) && sp.TryGetDouble(out var d))
        {
            pts = (int)Math.Round(d);
        }

        WorkItemAssignee? assignee = null;
        if (fields.TryGetProperty("System.AssignedTo", out var at)) assignee = MapWorkItemAssignee(at);

        var priority = (get("Microsoft.VSTS.Common.Priority") ?? "3") switch
        {
            "1" or "2" => "high",
            "3" => "med",
            _ => "low",
        };

        // Parent: walk the relations array for "System.LinkTypes.Hierarchy-Reverse".
        string? parentId = null;
        if (wi.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rels.EnumerateArray())
            {
                var relType = r.TryGetProperty("rel", out var rt) ? rt.GetString() : null;
                if (relType is "System.LinkTypes.Hierarchy-Reverse" &&
                    r.TryGetProperty("url", out var u))
                {
                    var url = u.GetString() ?? string.Empty;
                    var slash = url.LastIndexOf('/');
                    if (slash >= 0 && long.TryParse(url[(slash + 1)..], out var pid))
                    {
                        parentId = "WI-" + pid;
                        break;
                    }
                }
            }
        }

        var rev = fields.TryGetProperty("System.Rev", out var rv) && rv.TryGetInt32(out var rvi)
            ? rvi.ToString()
            : null;

        var createdAt = ParseAdoDate(get("System.CreatedDate")) ?? DateTimeOffset.UtcNow;
        var updatedAt = ParseAdoDate(get("System.ChangedDate")) ?? createdAt;

        return new WorkItem(
            Id: "WI-" + idLong,
            RepoId: repoId,
            Provider: "ado",
            ProviderRef: (int)Math.Min(int.MaxValue, idLong),
            Track: track,
            Type: type,
            State: state,
            Title: title,
            DescriptionMd: description,
            ParentId: parentId,
            FeatureRef: null,
            IterationRef: iterationRef,
            AreaPath: area,
            AssignedTo: assignee,
            Points: pts,
            Priority: priority,
            Tags: tags,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            RemoteRevision: rev);
    }

    /// <summary>
    /// ADO WorkItemType → Forge (track, type). Handles the standard process
    /// templates (Agile, Scrum, CMMI) plus the Phase 12 design-track names.
    /// Anything unknown falls back to a dev-track task.
    /// </summary>
    public static (string Track, string Type) MapTypeAndTrack(string? wit)
    {
        switch ((wit ?? string.Empty).ToLowerInvariant())
        {
            case "epic":                                  return ("dev", "epic");
            case "feature":                               return ("dev", "feature");
            case "user story":
            case "product backlog item":
            case "story":                                 return ("dev", "feature");
            case "task":                                  return ("dev", "task");
            case "bug":                                   return ("dev", "bug");
            case "phase":                                 return ("design", "phase");
            case "stage":                                 return ("design", "stage");
            case "step":                                  return ("design", "step");
            default:                                      return ("dev", "task");
        }
    }

    /// <summary>
    /// ADO state → Forge state per Q12.3:
    ///   New / Approved / Committed → proposed
    ///   Active                     → active
    ///   Resolved                   → resolved
    ///   Closed / Removed / Done    → closed
    /// </summary>
    public static string MapWorkItemState(string? state) =>
        (state ?? string.Empty).ToLowerInvariant() switch
        {
            "new" or "approved" or "committed" or "proposed" or "to do" or "ready" => "proposed",
            "active" or "in progress" or "doing" or "design" => "active",
            "resolved" or "code review" or "in review" or "review" => "resolved",
            "closed" or "done" or "completed" or "removed" => "closed",
            _ => "proposed",
        };

    public static WorkItemAssignee? MapWorkItemAssignee(JsonElement assignedTo)
    {
        if (assignedTo.ValueKind != JsonValueKind.Object) return null;
        var name = assignedTo.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        var unique = assignedTo.TryGetProperty("uniqueName", out var un) ? un.GetString() : null;
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(unique)) return null;
        var display = name ?? unique!;
        var initials = MakeInitials(display);
        // ADO never returns Forge agent ids, so kind is always "human" here.
        return new WorkItemAssignee("human", unique ?? initials, display, initials);
    }

    private static DateTimeOffset? ParseAdoDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;
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
        var prIdNum = pr.GetProperty("pullRequestId").GetInt64();
        var id = "PR #" + prIdNum;
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

        // Phase 27 — extract description, dates, web URL, and work item link.
        var description = pr.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty;

        var createdAt = pr.TryGetProperty("creationDate", out var cd) && cd.ValueKind == JsonValueKind.String
            ? cd.GetString() : null;
        // ADO's PR list API doesn't expose a `lastUpdateDate` directly; closedDate is set only
        // on completed/abandoned PRs. For active PRs, fall back to creationDate so the UI has
        // a date to display; PrSyncJob refreshes UpdatedAt from the comment-sync snapshot.
        string? updatedAt = null;
        if (pr.TryGetProperty("closedDate", out var closedD) && closedD.ValueKind == JsonValueKind.String)
        {
            updatedAt = closedD.GetString();
        }
        if (string.IsNullOrEmpty(updatedAt)) updatedAt = createdAt;

        // Construct the web URL from the repository URL: {org}/{project}/_git/{repo}/pullrequest/{prId}
        string? webUrl = null;
        if (pr.TryGetProperty("repository", out var repo) && repo.TryGetProperty("webUrl", out var repoUrl))
        {
            var repoWebUrl = repoUrl.GetString();
            if (!string.IsNullOrEmpty(repoWebUrl))
            {
                webUrl = $"{repoWebUrl}/pullrequest/{prIdNum}";
            }
        }

        // Extract linked work item IDs.
        string? workItemId = null;
        if (pr.TryGetProperty("workItemRefs", out var wiRefs) && wiRefs.ValueKind == JsonValueKind.Array)
        {
            foreach (var wiRef in wiRefs.EnumerateArray())
            {
                if (wiRef.TryGetProperty("id", out var wiId))
                {
                    workItemId = "WI-" + wiId.GetString();
                    break;
                }
            }
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
            Age: createdAt is not null ? RelativeAge(createdAt) : "—",
            Build: null,
            Review: null,
            Conflicts: hasConflict,
            HumanGate: false,
            Description: description,
            WebUrl: webUrl,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            WorkItemId: workItemId);
    }

    private static string RelativeAge(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, out var when)) return "—";
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    /// <summary>
    /// Combine ADO long-text fields (System.Description, ReproSteps,
    /// SystemInfo, AcceptanceCriteria) into a single markdown blob with
    /// section headings. Empty fields are skipped so a story without
    /// repro steps doesn't get an empty "## Repro steps" placeholder.
    /// HTML stays as-is — the UI's markdown preview renders simple HTML
    /// passthrough, which is good enough for ADO's typical content.
    /// </summary>
    public static string ComposeDescription(string? description, string? repro, string? systemInfo, string? acceptance)
    {
        var parts = new List<string>(8);
        void Add(string heading, string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            parts.Add($"## {heading}");
            parts.Add(body!.Trim());
            parts.Add(string.Empty);
        }
        Add("Description", description);
        Add("Repro steps", repro);
        Add("System info", systemInfo);
        Add("Acceptance criteria", acceptance);
        // No sections set → return empty so an unsynced row stays empty
        // rather than getting a stub.
        if (parts.Count == 0) return string.Empty;
        return string.Join('\n', parts).TrimEnd();
    }
}
