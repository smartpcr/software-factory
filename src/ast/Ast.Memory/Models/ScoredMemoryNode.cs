// -----------------------------------------------------------------------
// <copyright file="ScoredMemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// A memory node scored by the 5-signal weighted model.
/// Produced by the Ranker and consumed by the BudgetController and ContextAssembler.
/// </summary>
/// <param name="Node">The memory node that was scored.</param>
/// <param name="CompositeScore">Weighted aggregate of all signal scores.</param>
/// <param name="StructuralScore">Inverse BFS distance from seed node (weight: 0.30).</param>
/// <param name="SemanticScore">Cosine similarity of query embedding to node embedding (weight: 0.25).</param>
/// <param name="CentralityScore">Normalized fan-in PageRank-light score (weight: 0.20).</param>
/// <param name="RecencyScore">Commit-index proximity to HEAD (weight: 0.15).</param>
/// <param name="ExpertiseScore">Max effective confidence of APPLIES_TO expertise (weight: 0.10).</param>
public record ScoredMemoryNode(
    MemoryNode Node,
    double CompositeScore,
    double StructuralScore,
    double SemanticScore,
    double CentralityScore,
    double RecencyScore,
    double ExpertiseScore);