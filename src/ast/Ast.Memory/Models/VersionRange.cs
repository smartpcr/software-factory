// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for nodes and edges in the memory graph.
/// </summary>
public record VersionRange(
    string SinceCommit,
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
