// -----------------------------------------------------------------------
// <copyright file="VersionRange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Tracks the temporal validity window of a memory entity.
/// Per architecture.md §4.3, A-19.
/// <para>
/// External boundary nodes have <c>VersionRange = null</c> (timeless).
/// Non-external nodes always have a valid <see cref="SinceCommit"/> and <see cref="SinceIndex"/>.
/// </para>
/// </summary>
public record VersionRange(string SinceCommit, int SinceIndex, string? UntilCommit, int? UntilIndex);