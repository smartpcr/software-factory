// -----------------------------------------------------------------------
// <copyright file="Citation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Evidence citation for an expertise entry.
/// </summary>
public record Citation(string NodeId, string? CommitSha = null, string? Snippet = null);