// -----------------------------------------------------------------------
// <copyright file="RetrievalRequest.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Request for memory retrieval.
/// </summary>
public class RetrievalRequest
{
    /// <summary>
    /// Free-text query describing what to retrieve.
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// Seed node IDs for structural/call-trace traversal.
    /// </summary>
    public IReadOnlyList<string> SeedNodeIds { get; init; } = [];

    /// <summary>
    /// Retrieval mode. Default is Hybrid.
    /// </summary>
    public RetrievalMode Mode { get; init; } = RetrievalMode.Hybrid;

    /// <summary>
    /// Token budget for the response. Clamped to [1, TokenBudgetCeiling].
    /// </summary>
    public int TokenBudget { get; init; } = 8000;

    /// <summary>
    /// Optional commit index for temporal filtering.
    /// </summary>
    public int? AtCommitIndex { get; init; }

    /// <summary>
    /// Optional scoring weight overrides.
    /// </summary>
    public ScoringWeights? Weights { get; init; }
}
