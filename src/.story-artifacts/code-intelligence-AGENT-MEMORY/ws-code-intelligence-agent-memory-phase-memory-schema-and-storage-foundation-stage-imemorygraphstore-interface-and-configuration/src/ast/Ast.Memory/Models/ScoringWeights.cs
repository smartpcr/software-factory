// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weights for the multi-signal scoring formula used during context retrieval.
/// Per architecture.md §2.4 — values must sum to 1.00.
/// </summary>
public record ScoringWeights
{
    /// <summary>Weight for structural graph proximity. Default 0.30.</summary>
    public double Structural { get; init; } = 0.30;

    /// <summary>Weight for semantic / embedding similarity. Default 0.25.</summary>
    public double Semantic { get; init; } = 0.25;

    /// <summary>Weight for graph centrality (PageRank / betweenness). Default 0.20.</summary>
    public double Centrality { get; init; } = 0.20;

    /// <summary>Weight for temporal recency. Default 0.15.</summary>
    public double Recency { get; init; } = 0.15;

    /// <summary>Weight for expertise relevance. Default 0.10.</summary>
    public double Expertise { get; init; } = 0.10;

    /// <summary>
    /// Returns the sum of all weights. Must equal 1.00 for a valid configuration.
    /// </summary>
    public double Sum => this.Structural + this.Semantic + this.Centrality + this.Recency + this.Expertise;

    /// <summary>
    /// Validates that all weights are non-negative and sum to 1.00 (within floating-point tolerance).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when weights are negative or do not sum to 1.00.</exception>
    public void Validate()
    {
        if (this.Structural < 0 || this.Semantic < 0 || this.Centrality < 0 || this.Recency < 0 || this.Expertise < 0)
            throw new InvalidOperationException("All scoring weights must be non-negative.");

        if (Math.Abs(this.Sum - 1.0) > 1e-9)
            throw new InvalidOperationException(
                $"Scoring weights must sum to 1.00, but got {this.Sum:F10}.");
    }
}