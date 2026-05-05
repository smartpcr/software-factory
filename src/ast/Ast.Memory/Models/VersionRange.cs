// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for a memory node or edge.
/// Tracks the commit range during which the entity is active.
/// </summary>
/// <param name="Since">Commit SHA when the entity was introduced.</param>
/// <param name="SinceIndex">Monotonic commit index when introduced.</param>
/// <param name="Until">Commit SHA when the entity was retired (null if still active).</param>
/// <param name="UntilIndex">Monotonic commit index when retired (null if still active).</param>
public record VersionRange(
    string Since,
    int SinceIndex,
    string? Until = null,
    int? UntilIndex = null);