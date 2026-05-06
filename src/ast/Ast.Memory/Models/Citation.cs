// -----------------------------------------------------------------------
// <copyright file="Citation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Evidence citation for an expertise entry.
/// Per architecture.md §4.2.
/// </summary>
/// <param name="NodeId">The ID of the referenced code node.</param>
/// <param name="CommitSha">Optional commit SHA where evidence was observed.</param>
/// <param name="Snippet">Optional code snippet as evidence.</param>
public record Citation(string NodeId, string? CommitSha, string? Snippet);