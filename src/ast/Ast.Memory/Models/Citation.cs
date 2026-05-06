// -----------------------------------------------------------------------
// <copyright file="Citation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Evidence citation linking expertise to a specific code location.
/// </summary>
/// <param name="NodeId">The ID of the referenced code node.</param>
/// <param name="CommitSha">Optional commit SHA for temporal anchoring.</param>
/// <param name="Snippet">Optional code snippet as evidence.</param>
public record Citation(string NodeId, string? CommitSha, string? Snippet);