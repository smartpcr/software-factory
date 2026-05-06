// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Temporal version range for memory nodes and edges.
/// </summary>
public record VersionRange(string Since, int SinceIndex, string? Until = null, int? UntilIndex = null);