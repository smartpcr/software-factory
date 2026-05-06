// -----------------------------------------------------------------------
// <copyright file="ScoringWeights.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Weights for the 5-signal scoring model. Defaults sum to 1.0.
/// </summary>
public record ScoringWeights(
    double Structural = 0.30,
    double Semantic = 0.25,
    double Centrality = 0.20,
    double Recency = 0.15,
    double Expertise = 0.10)
{
    /// <summary>
    /// Renormalize weights excluding the semantic signal (when embedding is unavailable).
    /// </summary>
    public ScoringWeights WithoutSemantic()
    {
        var remaining = this.Structural + this.Centrality + this.Recency + this.Expertise;
        if (remaining <= 0)
        {
            return new ScoringWeights(0.25, 0, 0.25, 0.25, 0.25);
        }

        return new ScoringWeights(
            Structural: this.Structural / remaining,
            Semantic: 0,
            Centrality: this.Centrality / remaining,
            Recency: this.Recency / remaining,
            Expertise: this.Expertise / remaining);
    }

    /// <summary>
    /// The sum of all weights.
    /// </summary>
    public double Sum() =>
        this.Structural + this.Semantic + this.Centrality + this.Recency + this.Expertise;
}
