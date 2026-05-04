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
}
