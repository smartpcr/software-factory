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
    /// Returns null when no temporal metadata is present (timeless node).
    /// Throws <see cref="InvalidOperationException"/> when temporal metadata is incomplete
    /// (e.g. SinceCommit without SinceIndex, or UntilCommit without UntilIndex).
    /// </summary>
    internal static VersionRange? ExtractVersion(Dictionary<string, object?> metadata)
    {
        bool hasSinceCommit = metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceObj) && sinceObj is not null;
        bool hasSinceIndex = metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceIndexObj) && sinceIndexObj is not null;

        // Truly timeless: neither Since field present
        if (!hasSinceCommit && !hasSinceIndex)
        {
            // Check for orphaned Until fields
            bool hasAnyUntil = (metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var u1) && u1 is not null)
                || (metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var u2) && u2 is not null);
            if (hasAnyUntil)
            {
                throw new InvalidOperationException(
                    "Malformed temporal metadata: Until fields are present without Since fields.");
            }

            return null;
        }

        // Incomplete Since pair
        if (hasSinceCommit != hasSinceIndex)
        {
            var present = hasSinceCommit ? nameof(MemoryMetadataKeys.SinceCommit) : nameof(MemoryMetadataKeys.SinceIndex);
            var missing = hasSinceCommit ? nameof(MemoryMetadataKeys.SinceIndex) : nameof(MemoryMetadataKeys.SinceCommit);
            throw new InvalidOperationException(
                $"Malformed temporal metadata: {present} is present but {missing} is missing.");
        }

        var since = sinceObj!.ToString()!;
        var sinceIndex = ParseInt(sinceIndexObj!);

        bool hasUntilCommit = metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var untilObj) && untilObj is not null;
        bool hasUntilIndex = metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilIndexObj) && untilIndexObj is not null;

        // Incomplete Until pair
        if (hasUntilCommit != hasUntilIndex)
        {
            var present = hasUntilCommit ? nameof(MemoryMetadataKeys.UntilCommit) : nameof(MemoryMetadataKeys.UntilIndex);
            var missing = hasUntilCommit ? nameof(MemoryMetadataKeys.UntilIndex) : nameof(MemoryMetadataKeys.UntilCommit);
            throw new InvalidOperationException(
                $"Malformed temporal metadata: {present} is present but {missing} is missing.");
        }

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