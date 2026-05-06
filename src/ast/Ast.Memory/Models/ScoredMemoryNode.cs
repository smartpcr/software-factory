// -----------------------------------------------------------------------
// <copyright file="ScoredMemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// A memory node annotated with per-signal and composite relevance scores.
/// </summary>
public sealed class ScoredMemoryNode
{
    /// <summary>The scored node.</summary>
    public required MemoryNode Node { get; init; }

    /// <summary>Weighted composite score (descending sort key).</summary>
    public required double CompositeScore { get; init; }

    /// <summary>Structural signal: inverse BFS distance from seed.</summary>
    public required double StructuralScore { get; init; }

    /// <summary>Semantic signal: cosine similarity to query embedding.</summary>
    public required double SemanticScore { get; init; }

    /// <summary>Centrality signal: normalized fan-in (PageRank-light).</summary>
    public required double CentralityScore { get; init; }

    /// <summary>Recency signal: temporal proximity to HEAD.</summary>
    public required double RecencyScore { get; init; }

    /// <summary>Expertise signal: max Confidence of APPLIES_TO edges targeting this node.</summary>
    public required double ExpertiseScore { get; init; }
}