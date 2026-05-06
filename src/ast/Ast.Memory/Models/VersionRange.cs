// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Tracks the temporal validity window of a memory entity.
/// Per architecture.md §4.3.
/// <para>
/// <see cref="SinceIndex"/> is nullable to support External boundary nodes
/// (timeless nodes that are always included in temporal queries).
/// </para>
/// </summary>
public record VersionRange(string? Since, int? SinceIndex, string? Until, int? UntilIndex);