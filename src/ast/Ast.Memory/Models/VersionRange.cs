// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Dual-axis temporal version range: monotonic commit index for ordering,
/// commit SHA for provenance. See architecture.md §4.3.
/// </summary>
public record VersionRange(
    string SinceCommit,
    int SinceIndex,
    string? UntilCommit,
    int? UntilIndex);
