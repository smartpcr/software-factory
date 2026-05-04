// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Tracks the temporal validity window of a memory entity.
/// Per architecture.md §4.3.
/// </summary>
public record VersionRange(string Since, int SinceIndex, string? Until, int? UntilIndex);