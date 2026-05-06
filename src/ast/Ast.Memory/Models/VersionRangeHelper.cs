// -----------------------------------------------------------------------
// <copyright file="VersionRangeHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Shared helper for extracting <see cref="VersionRange"/> from metadata dictionaries.
/// </summary>
internal static class VersionRangeHelper
{
    internal static VersionRange? ExtractVersion(Dictionary<string, object?> metadata)
    {
        bool hasSinceCommit = metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceObj) && sinceObj is not null;
        bool hasSinceIndex = metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceIndexObj) && sinceIndexObj is not null;
        bool hasUntilCommit = metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var untilObj) && untilObj is not null;
        bool hasUntilIndex = metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilIndexObj) && untilIndexObj is not null;

        // Per architecture.md §4.3: SinceCommit and SinceIndex are required
        // for a valid VersionRange. External nodes have VersionRange = null.
        if (!hasSinceCommit || !hasSinceIndex)
        {
            return null;
        }

        string sinceCommit = sinceObj!.ToString()!;
        int sinceIndex = ParseInt(sinceIndexObj!);
        string? untilCommit = hasUntilCommit ? untilObj!.ToString() : null;
        int? untilIndex = hasUntilIndex ? ParseInt(untilIndexObj!) : null;

        return new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex);
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