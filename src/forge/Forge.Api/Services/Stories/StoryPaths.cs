// -----------------------------------------------------------------------
// <copyright file="StoryPaths.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

/// <summary>
/// Sanitizers for embedding a story id (or any caller-supplied identifier)
/// into a git branch ref OR a Windows file path. Story ids may contain
/// characters that are legal in URLs / DB keys but illegal as path segments
/// on Windows (`:` is reserved for drive letters and ADS) — every place
/// that turns an id into a filesystem segment must route through here so
/// the orchestrator's gate-check, the strategy's commit paths, and the
/// artifacts endpoint all agree on the same encoding.
/// </summary>
internal static class StoryPaths
{
    /// <summary>
    /// Replaces every character that isn't ASCII alphanumeric, `-`, `_`, or
    /// `.` with `-` and collapses runs. Returns "story" for null/empty
    /// input so callers can drop the empty-segment guard.
    /// </summary>
    public static string Safe(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "story";
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }

    /// <summary>
    /// Phase 83 — default plan-doc base directory used when a repo does not
    /// set <see cref="Persistence.RepoRow.PlanDocBaseDir"/>. Matches the
    /// historical hardcoded location so behaviour is unchanged for repos
    /// whose <c>.gitignore</c> already permits <c>docs/</c>.
    /// </summary>
    public const string DefaultPlanDocBaseDir = "docs/stories";

    /// <summary>
    /// Phase 83 — normalize an operator-supplied plan-doc base dir into a
    /// repo-relative, forward-slash path with no leading/trailing slash.
    /// Null / empty / absolute / traversal inputs collapse to
    /// <see cref="DefaultPlanDocBaseDir"/> — the base dir must stay inside
    /// the repo (it is fed straight into a commit path).
    /// </summary>
    public static string NormalizePlanDocBaseDir(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPlanDocBaseDir;
        var s = raw.Trim().Replace('\\', '/').Trim('/');
        if (s.Length == 0) return DefaultPlanDocBaseDir;
        // Reject absolute paths, drive-letter roots, and parent traversal.
        if (s.StartsWith('/') || s.Contains("..") || (s.Length >= 2 && s[1] == ':'))
        {
            return DefaultPlanDocBaseDir;
        }
        return s;
    }

    /// <summary>
    /// Phase 83 — the repo-relative directory that holds a story's plan docs:
    /// <c>{baseDir}/{Safe(storyId)}</c>. <paramref name="planDocBaseDir"/> is
    /// the owning repo's <see cref="Persistence.RepoRow.PlanDocBaseDir"/>
    /// (null => default). Every write site (CrewStoryArtifactStrategy) and
    /// every read/gate site (StorySpawnOrchestrator, PlanReconciler, the
    /// artifacts endpoints) must route through here so they all agree on the
    /// same location — a mismatch strands the plan gate waiting for docs that
    /// landed somewhere else.
    /// </summary>
    public static string PlanDocDir(string? planDocBaseDir, string? storyId)
        => $"{NormalizePlanDocBaseDir(planDocBaseDir)}/{Safe(storyId)}";
}
