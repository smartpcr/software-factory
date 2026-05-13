// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Commit-index range for temporal node/edge versioning.
/// </summary>
/// <param name="SinceCommit">Git commit SHA at which this entity was introduced.</param>
/// <param name="SinceIndex">Monotonic commit index at which this entity was introduced.</param>
/// <param name="UntilCommit">Git commit SHA at which this entity was retired (null = still active).</param>
/// <param name="UntilIndex">Commit index at which this entity was retired (null = still active).</param>
public record VersionRange(
    string Since,
    int SinceIndex,
    string? UntilCommit = null,
    int? UntilIndex = null);