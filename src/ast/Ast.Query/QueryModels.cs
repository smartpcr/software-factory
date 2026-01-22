// -----------------------------------------------------------------------
// <copyright file="QueryModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query;

using Ast.Core;
using Ast.GraphDb;

/// <summary>
/// Result of a code query.
/// </summary>
public class CodeQueryResult
{
    /// <summary>
    /// Gets or sets the matching nodes.
    /// </summary>
    public List<HypergraphNode> Nodes { get; set; } = new();

    /// <summary>
    /// Gets or sets the matching edges.
    /// </summary>
    public List<Hyperedge> Edges { get; set; } = new();

    /// <summary>
    /// Gets or sets the paths found (for path queries).
    /// </summary>
    public List<GraphPath> Paths { get; set; } = new();

    /// <summary>
    /// Gets or sets the impact analysis (if requested).
    /// </summary>
    public ImpactAnalysis? Impact { get; set; }

    /// <summary>
    /// Gets or sets semantic search results with scores.
    /// </summary>
    public List<ScoredNode> SemanticMatches { get; set; } = new();

    /// <summary>
    /// Gets the total count of results.
    /// </summary>
    public int TotalCount => Nodes.Count + SemanticMatches.Count;

    /// <summary>
    /// Gets whether the query returned any results.
    /// </summary>
    public bool HasResults => TotalCount > 0 || Paths.Count > 0 || Impact != null;
}

/// <summary>
/// A node with a similarity score from semantic search.
/// </summary>
public class ScoredNode
{
    /// <summary>
    /// Gets or sets the node.
    /// </summary>
    public required HypergraphNode Node { get; set; }

    /// <summary>
    /// Gets or sets the similarity score (0-1).
    /// </summary>
    public float Score { get; set; }
}

/// <summary>
/// Query execution options.
/// </summary>
public class QueryOptions
{
    /// <summary>
    /// Gets or sets the maximum number of results.
    /// </summary>
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Gets or sets the minimum similarity score for semantic search.
    /// </summary>
    public float MinScore { get; set; } = 0.5f;

    /// <summary>
    /// Gets or sets whether to include related nodes.
    /// </summary>
    public bool IncludeRelated { get; set; }

    /// <summary>
    /// Gets or sets the depth for traversal operations.
    /// </summary>
    public int TraversalDepth { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to use semantic search.
    /// </summary>
    public bool UseSemanticSearch { get; set; }

    /// <summary>
    /// Gets or sets whether to use graph search.
    /// </summary>
    public bool UseGraphSearch { get; set; } = true;
}

/// <summary>
/// Types of code queries.
/// </summary>
public enum QueryType
{
    /// <summary>
    /// Find nodes by criteria.
    /// </summary>
    FindNodes,

    /// <summary>
    /// Find paths between nodes.
    /// </summary>
    FindPaths,

    /// <summary>
    /// Find callers of a node.
    /// </summary>
    FindCallers,

    /// <summary>
    /// Find callees of a node.
    /// </summary>
    FindCallees,

    /// <summary>
    /// Analyze impact of changes.
    /// </summary>
    AnalyzeImpact,

    /// <summary>
    /// Semantic search.
    /// </summary>
    SemanticSearch,

    /// <summary>
    /// Hybrid search (graph + semantic).
    /// </summary>
    HybridSearch,

    /// <summary>
    /// Find implementations of an interface.
    /// </summary>
    FindImplementations,

    /// <summary>
    /// Find derived classes.
    /// </summary>
    FindDerivedClasses,

    /// <summary>
    /// Find cross-language relationships.
    /// </summary>
    FindCrossLanguage
}

/// <summary>
/// Direction of relationship traversal.
/// </summary>
public enum TraversalDirection
{
    /// <summary>
    /// Follow outgoing relationships.
    /// </summary>
    Outgoing,

    /// <summary>
    /// Follow incoming relationships.
    /// </summary>
    Incoming,

    /// <summary>
    /// Follow both directions.
    /// </summary>
    Both
}
