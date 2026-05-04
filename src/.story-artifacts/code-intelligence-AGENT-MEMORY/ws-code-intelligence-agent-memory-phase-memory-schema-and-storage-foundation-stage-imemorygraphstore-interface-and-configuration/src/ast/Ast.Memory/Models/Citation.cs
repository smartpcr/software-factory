// -----------------------------------------------------------------------
// <copyright file="Citation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// An evidence citation linking expertise to a specific graph node and commit.
/// Per architecture.md §4.2.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);