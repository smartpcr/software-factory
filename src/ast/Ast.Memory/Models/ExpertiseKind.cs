// -----------------------------------------------------------------------
// <copyright file="ExpertiseKind.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Kind of expertise entry — per architecture.md §4.2.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>Reusable design pattern or code pattern.</summary>
    Pattern,

    /// <summary>Known pitfall or anti-pattern.</summary>
    Pitfall,

    /// <summary>Coding preference or convention.</summary>
    Preference,

    /// <summary>General insight about the codebase.</summary>
    Insight
}