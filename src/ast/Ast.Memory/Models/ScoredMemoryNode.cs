// -----------------------------------------------------------------------
// <copyright file="ScoredMemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// A memory node with per-signal and composite scores from the ranker.
/// </summary>
public class ScoredMemoryNode
{
    public required MemoryNode Node { get; init; }
    public double CompositeScore { get; set; }
    public double StructuralScore { get; set; }
    public double SemanticScore { get; set; }
    public double CentralityScore { get; set; }
    public double RecencyScore { get; set; }
    public double ExpertiseScore { get; set; }
}
