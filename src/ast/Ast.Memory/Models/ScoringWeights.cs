// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weight distribution for the 5-signal scoring model (sum = 1.00).
/// </summary>
public sealed class ScoringWeights
{
    /// <summary>Inverse BFS distance from seed (Structural layer).</summary>
    public double Structural { get; init; } = 0.30;

    /// <summary>Cosine similarity of query embedding to node embedding.</summary>
    public double Semantic { get; init; } = 0.25;

    /// <summary>Normalized fan-in from CALLS edges (PageRank-light).</summary>
    public double Centrality { get; init; } = 0.20;

    /// <summary>Temporal recency based on commit index.</summary>
    public double Recency { get; init; } = 0.15;

    /// <summary>Max Confidence of targeting APPLIES_TO edges.</summary>
    public double Expertise { get; init; } = 0.10;

    /// <summary>Sum of all weights.</summary>
    public double Sum => this.Structural + this.Semantic + this.Centrality + this.Recency + this.Expertise;
}