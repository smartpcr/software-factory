// -----------------------------------------------------------------------
// <copyright file="NodeFilter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Optional filter criteria for memory-graph node queries.
/// All properties are nullable; only non-null values constrain the result set.
/// </summary>
public record NodeFilter(
    string? NodeType = null,
    string? Language = null,
    string? FilePathPrefix = null,
    IReadOnlyList<string>? NodeIds = null);