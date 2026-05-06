// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Extended statistics for the memory graph, including layer-specific
/// counts and expertise metrics.
/// </summary>
public class MemoryGraphStatistics
{
    public int TotalNodes { get; set; }
    public int TotalEdges { get; set; }
    public int StructuralNodes { get; set; }
    public int CallChainEdges { get; set; }
    public int SemanticEdges { get; set; }
    public int TopicNodes { get; set; }
    public int ExpertiseEntries { get; set; }
    public int ArchivedExpertise { get; set; }
    public int ClusterEdges { get; set; }
}

/// <summary>
/// Scoring weights for the Ranker, per architecture.md §2.4.
/// </summary>
public class ScoringWeights
{
    /// <summary>Inverse BFS distance from seed (Structural layer).</summary>
    public double Structural { get; set; } = 0.30;

    /// <summary>Cosine similarity of query embedding to node embedding.</summary>
    public double Semantic { get; set; } = 0.25;

    /// <summary>Normalized fan-in from CALLS edges (PageRank-light).</summary>
    public double Centrality { get; set; } = 0.20;

    /// <summary>Recency of node relative to HEAD commit.</summary>
    public double Recency { get; set; } = 0.15;

    /// <summary>Max effective confidence of non-archived APPLIES_TO expertise.</summary>
    public double Expertise { get; set; } = 0.10;
}