// -----------------------------------------------------------------------
// <copyright file="VersionRangeHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Shared helper for extracting <see cref="VersionRange"/> from metadata dictionaries.
/// Used by both <see cref="MemoryNode"/> and <see cref="MemoryEdge"/>.
/// </summary>
internal static class VersionRangeHelper
{
    /// <summary>
    /// Extracts a <see cref="VersionRange"/> from a metadata dictionary.
    /// Returns null when no temporal metadata is present at all (truly timeless).
    /// Accepts partial temporal data — e.g. <c>UntilIndex</c> without <c>SinceIndex</c>
    /// (External boundary nodes may acquire an until_index via retirement).
    /// </summary>
    internal static VersionRange? ExtractVersion(Dictionary<string, object?> metadata)
    {
        bool hasSinceCommit = metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceObj) && sinceObj is not null;
        bool hasSinceIndex = metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceIndexObj) && sinceIndexObj is not null;
        bool hasUntilCommit = metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var untilObj) && untilObj is not null;
        bool hasUntilIndex = metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilIndexObj) && untilIndexObj is not null;

        // Truly timeless: no temporal fields at all
        if (!hasSinceCommit && !hasSinceIndex && !hasUntilCommit && !hasUntilIndex)
        {
            return null;
        }

        string? since = hasSinceCommit ? sinceObj!.ToString() : null;
        int? sinceIndex = hasSinceIndex ? ParseInt(sinceIndexObj!) : null;
        string? until = hasUntilCommit ? untilObj!.ToString() : null;
        int? untilIndex = hasUntilIndex ? ParseInt(untilIndexObj!) : null;

        return new VersionRange(since, sinceIndex, until, untilIndex);
    }

    private static int ParseInt(object value)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => Convert.ToInt32(value)
        };
    }
}
