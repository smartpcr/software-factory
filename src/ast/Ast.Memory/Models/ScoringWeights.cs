// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weight distribution for memory-node relevance scoring.
/// Defaults sum to 1.00 per architecture.md §2.4.
/// </summary>
public record ScoringWeights
{
    public double Structural { get; init; } = 0.30;
    public double Semantic { get; init; } = 0.25;
    public double Centrality { get; init; } = 0.20;
    public double Recency { get; init; } = 0.15;
    public double Expertise { get; init; } = 0.10;

    public double Sum => this.Structural + this.Semantic + this.Centrality + this.Recency + this.Expertise;
}