// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weights for the 5-signal scoring model used by the Ranker.
/// Values should sum to 1.0 for normalized composite scores.
/// </summary>
/// <param name="Structural">Weight for structural proximity (inverse BFS distance). Default: 0.30.</param>
/// <param name="Semantic">Weight for semantic similarity (cosine). Default: 0.25.</param>
/// <param name="Centrality">Weight for centrality (normalized fan-in PageRank-light). Default: 0.20.</param>
/// <param name="Recency">Weight for recency (commit-index proximity to HEAD). Default: 0.15.</param>
/// <param name="Expertise">Weight for expertise (max confidence of APPLIES_TO edges). Default: 0.10.</param>
public record ScoringWeights(
    double Structural = 0.30,
    double Semantic = 0.25,
    double Centrality = 0.20,
    double Recency = 0.15,
    double Expertise = 0.10);