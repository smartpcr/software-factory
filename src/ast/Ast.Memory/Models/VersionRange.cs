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
    string Since,
    int SinceIndex,
    string? Until,
    int? UntilIndex);