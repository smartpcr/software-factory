// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Represents a temporal version range for a node or edge in the memory graph.
/// Tracks the commit index range during which the entity is/was active.
/// </summary>
public record VersionRange(
    string SinceCommit,
    int SinceIndex,
    string? UntilCommit = null,
    int? UntilIndex = null)
{
    /// <summary>
    /// Creates a version range starting at the given commit with no end (still active).
    /// </summary>
    public static VersionRange Since(string commitSha, int commitIndex) =>
        new(commitSha, commitIndex);
}