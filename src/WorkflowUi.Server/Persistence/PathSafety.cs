// -----------------------------------------------------------------------
// <copyright file="PathSafety.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.IO;

/// <summary>
/// Canonicalises file paths and rejects traversal attempts.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Canonicalises <paramref name="id"/> against <paramref name="root"/> and returns
    /// the full path. Returns null if the resulting path escapes the root directory
    /// (e.g. via ".." segments, absolute paths, or drive letters).
    /// </summary>
    public static string? CanonicalizeAndCheck(string root, string id)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Reject IDs with path traversal patterns
        if (id.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(id) ||
            id.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        // Reject control characters
        foreach (var c in id)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }

        var normalizedRoot = Path.GetFullPath(root);
        var combined = Path.Combine(normalizedRoot, id);
        var canonicalized = Path.GetFullPath(combined);

        // Ensure the canonicalized path starts with the root
        if (!canonicalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return canonicalized;
    }
}
