// -----------------------------------------------------------------------
// <copyright file="FileChange.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Represents a single file change from a git diff.
/// Supports renames with separate old/new paths.
/// </summary>
public sealed record FileChange(
    FileChangeStatus Status,
    string Path,
    string? OldPath = null,
    int? SimilarityScore = null);