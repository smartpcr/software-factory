// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for a node or edge in the memory graph.
/// Uses dual-axis time: monotonic commit indices for ordering,
/// plus wall-clock timestamps available via join to the commits table.
/// </summary>
public record VersionRange(
    string Since,
    int SinceIndex,
    string? UntilCommit,
    int? UntilIndex)
{
    /// <summary>
    /// Checks whether this version range is active at the given commit index.
    /// </summary>
    public bool IsActiveAt(int commitIndex)
    {
        return this.SinceIndex <= commitIndex
            && (this.UntilIndex is null || this.UntilIndex > commitIndex);
    }
}
