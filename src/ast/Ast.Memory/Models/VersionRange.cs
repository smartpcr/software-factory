// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for nodes and edges in the memory graph.
/// Uses dual-axis time — monotonic commit indices for ordering, plus
/// wall-clock timestamps available via join to the commits table.
/// Per architecture.md §4.3.
/// </summary>
public record VersionRange(
    string Since,
    int SinceIndex,
    string? UntilCommit = null,
    int? UntilIndex = null
);