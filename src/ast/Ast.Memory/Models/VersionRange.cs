// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for nodes and edges per architecture §4.3.
/// Tracks when a node/edge existed in the codebase via dual-axis time:
/// monotonic commit indices for ordering, plus commit SHAs for provenance.
/// </summary>
public record VersionRange(
    string SinceCommit,
    int SinceIndex,
    string? UntilCommit = null,
    int? UntilIndex = null)
{
    /// <summary>
    /// Returns true if this version is active at the given commit index.
    /// </summary>
    public bool IsActiveAt(int commitIndex) =>
        this.SinceIndex <= commitIndex && (this.UntilIndex == null || this.UntilIndex > commitIndex);

    /// <summary>
    /// Returns true if this version has been retired.
    /// </summary>
    public bool IsRetired => this.UntilIndex != null;

    /// <summary>
    /// Creates a new VersionRange from extraction options.
    /// If commit info is not available, uses defaults.
    /// </summary>
    public static VersionRange FromOptions(ExtractionOptions options)
    {
        return new VersionRange(
            SinceCommit: options.CommitSha ?? "working-tree",
            SinceIndex: options.CommitIndex ?? 0);
    }
}
