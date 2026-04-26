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

        var conflicts = pr.TryGetProperty("mergeable", out var mer)
            && mer.ValueKind == JsonValueKind.False;

        var labels = ExtractLabelNames(pr);
        var humanGate = labels.Contains("needs-human-review", StringComparer.OrdinalIgnoreCase)
            || labels.Contains("security", StringComparer.OrdinalIgnoreCase);

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
            Age: pr.TryGetProperty("created_at", out var ca) ? RelativeAge(ca.GetString()) : "—",
            Build: null,
            Review: null,
            Conflicts: conflicts,
            HumanGate: humanGate,
            GateReason: humanGate ? "label requires human review" : null);
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
