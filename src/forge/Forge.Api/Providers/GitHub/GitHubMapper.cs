// -----------------------------------------------------------------------
// <copyright file="GitHubMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Maps GitHub issue / PR JSON shapes into our card / PR contracts.
///
/// Per Phase 5.6:
///   * issue type derived from labels (bug, enhancement, task → defaults to story)
///   * PR draft status preserved on <c>Pr.Col</c> (drafts → implementation)
/// </summary>
public static class GitHubMapper
{
    /// <summary>
    /// Phase 12.4 — map a GitHub issue envelope into the rich
    /// <see cref="WorkItem"/> shape consumed by the WIT sync service.
    /// Type and track are derived from labels (epic / feature / phase /
    /// stage / step labels override; otherwise bugs/tasks/stories fall
    /// back to the existing <see cref="ChooseType"/>). Parent is parsed
    /// from a <c>Parent: #N</c> line in the body when GitHub's native
    /// sub-issues feature isn't in use.
    /// </summary>
    public static WorkItem IssueToWorkItem(JsonElement issue, string repoId, string milestonePrefix)
    {
        var number = issue.GetProperty("number").GetInt64();
        var nodeId = "WI-GH-" + number;
        var title = issue.GetProperty("title").GetString() ?? "(no title)";
        var bodyMd = issue.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? string.Empty
            : string.Empty;

        var stateOpen = string.Equals(issue.GetProperty("state").GetString(), "open", StringComparison.OrdinalIgnoreCase);
        var stateReason = issue.TryGetProperty("state_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString()
            : null;
        var state = MapWorkItemState(stateOpen, stateReason);

        var labels = ExtractLabelNames(issue);
        var (track, type) = MapTypeAndTrack(labels);
        var priority = ChoosePriority(labels);

        WorkItemAssignee? assignee = null;
        if (issue.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            var login = a.TryGetProperty("login", out var l) ? l.GetString() : null;
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(login))
            {
                assignee = new WorkItemAssignee("human", login!, name ?? login!, ShortId(login!));
            }
        }

        // GitHub sub-issues feature: extract from `sub_issues` / `parent_issue`
        // when present; otherwise fall back to "Parent: #N" body parsing.
        string? parentId = null;
        if (issue.TryGetProperty("parent", out var pp) && pp.ValueKind == JsonValueKind.Object &&
            pp.TryGetProperty("number", out var pn) && pn.TryGetInt64(out var parentNum))
        {
            parentId = "WI-GH-" + parentNum;
        }
        else
        {
            parentId = ParseParentFromBody(bodyMd);
        }

        // Iteration: take the milestone title, namespaced by repo prefix.
        var iterationRef = string.Empty;
        if (issue.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object)
        {
            var msTitle = ms.TryGetProperty("title", out var mt) ? mt.GetString() : null;
            if (!string.IsNullOrEmpty(msTitle))
            {
                iterationRef = $"{milestonePrefix}:{msTitle}";
            }
        }

        var createdAt = issue.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(ca.GetString()!).ToUniversalTime()
            : DateTimeOffset.UtcNow;
        var updatedAt = issue.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(ua.GetString()!).ToUniversalTime()
            : createdAt;

        return new WorkItem(
            Id: nodeId,
            RepoId: repoId,
            Provider: "github",
            ProviderRef: (int)Math.Min(int.MaxValue, number),
            Track: track,
            Type: type,
            State: state,
            Title: title,
            DescriptionMd: bodyMd,
            ParentId: parentId,
            FeatureRef: null,
            IterationRef: iterationRef,
            AreaPath: string.Empty,
            AssignedTo: assignee,
            Points: 0,
            Priority: priority,
            Tags: labels,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            // GitHub doesn't expose a concise revision string on the REST
            // shape; the `updated_at` ISO works as the conflict marker.
            RemoteRevision: updatedAt.ToString("o"));
    }

    /// <summary>
    /// Forge work-item type derivation from issue labels. Epic / feature /
    /// phase / stage / step labels win; bug overrides task; default = task.
    /// </summary>
    public static (string Track, string Type) MapTypeAndTrack(IReadOnlyList<string> labels)
    {
        var lower = labels.Select(l => l.ToLowerInvariant()).ToHashSet();
        if (lower.Contains("epic"))    return ("dev", "epic");
        if (lower.Contains("feature")) return ("dev", "feature");
        if (lower.Contains("phase"))   return ("design", "phase");
        if (lower.Contains("stage"))   return ("design", "stage");
        if (lower.Contains("step"))    return ("design", "step");
        if (lower.Contains("bug"))     return ("dev", "bug");
        return ("dev", "task");
    }

    /// <summary>Q12.3 mapping for GitHub state + state_reason.</summary>
    public static string MapWorkItemState(bool stateOpen, string? stateReason)
    {
        if (stateOpen)
        {
            return string.Equals(stateReason, "not_planned", StringComparison.OrdinalIgnoreCase)
                ? "proposed"
                : "active";
        }
        return string.Equals(stateReason, "completed", StringComparison.OrdinalIgnoreCase)
            ? "resolved"
            : "closed";
    }

    /// <summary>Read a "Parent: #N" line in the markdown body.</summary>
    public static string? ParseParentFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Tolerate "Parent:", "parent:", "Parent issue:" etc.
            const string tag = "parent:";
            var idx = line.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest = line[(idx + tag.Length)..].Trim().TrimStart('#');
            // Pull the leading numeric token.
            var n = 0;
            while (n < rest.Length && char.IsDigit(rest[n])) n++;
            if (n == 0) continue;
            if (long.TryParse(rest[..n], out var parentNum))
            {
                return "WI-GH-" + parentNum;
            }
        }
        return null;
    }

    public static Card IssueToCard(JsonElement issue, string defaultEpic)
    {
        var id = "#" + issue.GetProperty("number").GetInt64();
        var title = issue.GetProperty("title").GetString() ?? "(no title)";
        var stateOpen = string.Equals(issue.GetProperty("state").GetString(), "open", StringComparison.OrdinalIgnoreCase);

        var labelNames = ExtractLabelNames(issue);
        var type = ChooseType(labelNames);
        var priority = ChoosePriority(labelNames);
        var col = stateOpen ? ChooseOpenColumn(labelNames) : "done";

        Agent? agent = null;
        if (issue.TryGetProperty("assignee", out var assignee) && assignee.ValueKind == JsonValueKind.Object)
        {
            var login = assignee.TryGetProperty("login", out var l) ? l.GetString() : null;
            if (!string.IsNullOrEmpty(login))
            {
                agent = new Agent(Id: ShortId(login), Role: "human", Name: login);
            }
        }

        return new Card(
            Id: id,
            Epic: defaultEpic,
            Col: col,
            Type: type,
            Title: title,
            Tags: labelNames,
            Pts: 0,
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
            DoneAt: !stateOpen ? "recently" : null);
    }

    public static Pr PrToPr(JsonElement pr, string epicFallback)
    {
        var id = "PR #" + pr.GetProperty("number").GetInt64();
        var title = pr.GetProperty("title").GetString() ?? string.Empty;
        var draft = pr.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True;
        var merged = pr.TryGetProperty("merged_at", out var mat) && mat.ValueKind == JsonValueKind.String;
        var stateOpen = string.Equals(pr.GetProperty("state").GetString(), "open", StringComparison.OrdinalIgnoreCase);

        var col = merged ? "merged"
            : draft ? "implementation"
            : stateOpen ? "review"
            : "merged";

        var branch = pr.GetProperty("head").GetProperty("ref").GetString() ?? string.Empty;
        var baseBranch = pr.GetProperty("base").GetProperty("ref").GetString() ?? string.Empty;

        Agent author = new("XX", "human", "unknown");
        if (pr.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var login = user.TryGetProperty("login", out var l) ? l.GetString() : null;
            if (!string.IsNullOrEmpty(login))
            {
                author = new Agent(Id: ShortId(login), Role: "human", Name: login);
            }
        }

        var additions = pr.TryGetProperty("additions", out var a) ? a.GetInt32() : 0;
        var deletions = pr.TryGetProperty("deletions", out var d) ? d.GetInt32() : 0;
        var files = pr.TryGetProperty("changed_files", out var f) ? f.GetInt32() : 0;
        var commits = pr.TryGetProperty("commits", out var c) ? c.GetInt32() : 0;

        // Phase R — conflict signal. GitHub returns `mergeable: null` while
        // it computes mergeability and `true`/`false` once known. The
        // `mergeable_state` string is computed on a different cadence and
        // is the authoritative "dirty" (= merge conflicts) signal even
        // when `mergeable` is still null. Recognise both so a stale
        // `mergeable: null` doesn't mask a real conflict that GitHub has
        // already flagged via `mergeable_state`.
        var conflicts = (pr.TryGetProperty("mergeable", out var mer)
                && mer.ValueKind == JsonValueKind.False)
            || (pr.TryGetProperty("mergeable_state", out var ms)
                && ms.ValueKind == JsonValueKind.String
                && string.Equals(ms.GetString(), "dirty", StringComparison.OrdinalIgnoreCase));

        var labels = ExtractLabelNames(pr);
        var humanGate = labels.Contains("needs-human-review", StringComparer.OrdinalIgnoreCase)
            || labels.Contains("security", StringComparer.OrdinalIgnoreCase);

        var prCreatedAt = pr.TryGetProperty("created_at", out var prCa) && prCa.ValueKind == JsonValueKind.String
            ? prCa.GetString() : null;
        var prUpdatedAt = pr.TryGetProperty("updated_at", out var prUa) && prUa.ValueKind == JsonValueKind.String
            ? prUa.GetString() : null;
        var prDescription = pr.TryGetProperty("body", out var prBody) && prBody.ValueKind == JsonValueKind.String
            ? prBody.GetString() ?? string.Empty : string.Empty;
        var prWebUrl = pr.TryGetProperty("html_url", out var prHtml) && prHtml.ValueKind == JsonValueKind.String
            ? prHtml.GetString() : null;

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
            Additions: additions,
            Deletions: deletions,
            Files: files,
            Commits: commits,
            Age: prCreatedAt is not null ? RelativeAge(prCreatedAt) : "—",
            Build: null,
            Review: null,
            Conflicts: conflicts,
            HumanGate: humanGate,
            GateReason: humanGate ? "label requires human review" : null,
            Description: prDescription,
            WebUrl: prWebUrl,
            CreatedAt: prCreatedAt,
            UpdatedAt: prUpdatedAt);
    }

    private static IReadOnlyList<string> ExtractLabelNames(JsonElement issueOrPr)
    {
        if (!issueOrPr.TryGetProperty("labels", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var names = new List<string>(arr.GetArrayLength());
        foreach (var l in arr.EnumerateArray())
        {
            var name = l.ValueKind == JsonValueKind.Object && l.TryGetProperty("name", out var n)
                ? n.GetString()
                : (l.ValueKind == JsonValueKind.String ? l.GetString() : null);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    private static string ChooseType(IReadOnlyList<string> labels)
    {
        if (labels.Any(l => l.Contains("bug", StringComparison.OrdinalIgnoreCase))) return "bug";
        if (labels.Any(l => l.Contains("task", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("chore", StringComparison.OrdinalIgnoreCase))) return "task";
        return "story";
    }

    private static string ChoosePriority(IReadOnlyList<string> labels)
    {
        if (labels.Any(l => l.Contains("p0", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("priority/high", StringComparison.OrdinalIgnoreCase))) return "high";
        if (labels.Any(l => l.Contains("p1", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("priority/med", StringComparison.OrdinalIgnoreCase))) return "med";
        return "low";
    }

    private static string ChooseOpenColumn(IReadOnlyList<string> labels)
    {
        if (labels.Any(l => l.Contains("in-design", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("status:design", StringComparison.OrdinalIgnoreCase))) return "design";
        if (labels.Any(l => l.Contains("in-progress", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("status:building", StringComparison.OrdinalIgnoreCase))) return "build";
        if (labels.Any(l => l.Contains("in-review", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("status:review", StringComparison.OrdinalIgnoreCase))) return "review";
        return "backlog";
    }

    private static string ShortId(string login)
    {
        // First 2 letters uppercased — keeps the existing CSS chip happy.
        var s = login.Replace("-", string.Empty).Replace("_", string.Empty);
        return s.Length <= 2 ? s.ToUpperInvariant() : s[..2].ToUpperInvariant();
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
