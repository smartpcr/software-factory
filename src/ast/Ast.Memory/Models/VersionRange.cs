// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range tracking when a node/edge existed in the codebase.
/// </summary>
public record VersionRange(
    string? Since,
    int? SinceIndex,
    string? Until,
    int? UntilIndex);