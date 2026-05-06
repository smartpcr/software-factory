// -----------------------------------------------------------------------
// <copyright file="RetrievalRequest.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Request model for the memory retrieval engine (architecture.md §5.2).
/// Describes what to retrieve, how to retrieve it, and the token budget constraint.
/// </summary>
/// <param name="QueryText">Free-text query describing what the agent is looking for.</param>
/// <param name="SeedNodeIds">Seed node IDs for structural/call-trace traversal.</param>
/// <param name="Mode">Retrieval mode. Defaults to Hybrid (automatic selection).</param>
/// <param name="TokenBudget">Max tokens for context window. Clamped to [1, 32000]. Default: 8000.</param>
/// <param name="AtCommitIndex">Optional commit index for temporal filtering.</param>
/// <param name="Weights">Optional custom scoring weights for the 5-signal ranker.</param>
public record RetrievalRequest(
    string? QueryText = null,
    IReadOnlyList<string>? SeedNodeIds = null,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    int TokenBudget = RetrievalRequest.DefaultTokenBudget,
    int? AtCommitIndex = null,
    ScoringWeights? Weights = null)
{
    /// <summary>
    /// Maximum allowed token budget. Requests exceeding this ceiling are clamped.
    /// </summary>
    public const int TokenBudgetCeiling = 32_000;

    /// <summary>
    /// Minimum allowed token budget.
    /// </summary>
    public const int TokenBudgetFloor = 1;

    /// <summary>
    /// Default token budget when none is specified.
    /// </summary>
    public const int DefaultTokenBudget = 8_000;

    /// <summary>
    /// Maximum number of tokens for the assembled context window.
    /// Clamped to [<see cref="TokenBudgetFloor"/>, <see cref="TokenBudgetCeiling"/>].
    /// </summary>
    public int TokenBudget { get; init; } =
        Math.Clamp(TokenBudget, TokenBudgetFloor, TokenBudgetCeiling);

    /// <summary>
    /// Returns true if <see cref="QueryText"/> is non-null and non-whitespace.
    /// </summary>
    public bool HasQueryText => !string.IsNullOrWhiteSpace(this.QueryText);

    /// <summary>
    /// Returns true if <see cref="SeedNodeIds"/> contains at least one entry.
    /// </summary>
    public bool HasSeedNodes => this.SeedNodeIds is { Count: > 0 };
}