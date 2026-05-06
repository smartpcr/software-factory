// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weights for the 5-signal retrieval ranker. Sum must equal 1.00.
/// </summary>
public record ScoringWeights(
    double Structural = 0.30,
    double Semantic = 0.25,
    double Centrality = 0.20,
    double Recency = 0.15,
    double Expertise = 0.10);