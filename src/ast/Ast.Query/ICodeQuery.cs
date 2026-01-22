// -----------------------------------------------------------------------
// <copyright file="ICodeQuery.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query;

using Ast.Core;
using Ast.GraphDb;

/// <summary>
/// Fluent interface for building code queries.
/// </summary>
public interface ICodeQuery
{
    /// <summary>
    /// Start a query for nodes.
    /// </summary>
    /// <returns>A node query builder.</returns>
    INodeQuery Nodes();

    /// <summary>
    /// Start a query for a specific node by ID.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <returns>A single node query builder.</returns>
    ISingleNodeQuery Node(string nodeId);

    /// <summary>
    /// Start a query for a specific node by name.
    /// </summary>
    /// <param name="name">The node name.</param>
    /// <returns>A single node query builder.</returns>
    ISingleNodeQuery NodeByName(string name);

    /// <summary>
    /// Start a semantic search query.
    /// </summary>
    /// <param name="searchText">The text to search for.</param>
    /// <returns>A semantic query builder.</returns>
    ISemanticQuery Search(string searchText);

    /// <summary>
    /// Find paths between two nodes.
    /// </summary>
    /// <param name="fromNodeId">Starting node ID.</param>
    /// <param name="toNodeId">Ending node ID.</param>
    /// <returns>A path query builder.</returns>
    IPathQuery PathsBetween(string fromNodeId, string toNodeId);

    /// <summary>
    /// Find paths between two nodes by name.
    /// </summary>
    /// <param name="fromName">Starting node name.</param>
    /// <param name="toName">Ending node name.</param>
    /// <returns>A path query builder.</returns>
    IPathQuery PathsBetweenNames(string fromName, string toName);
}

/// <summary>
/// Query builder for filtering multiple nodes.
/// </summary>
public interface INodeQuery
{
    /// <summary>
    /// Filter by language.
    /// </summary>
    INodeQuery OfLanguage(string language);

    /// <summary>
    /// Filter by node type.
    /// </summary>
    INodeQuery OfType(string nodeType);

    /// <summary>
    /// Filter by multiple node types.
    /// </summary>
    INodeQuery OfTypes(params string[] nodeTypes);

    /// <summary>
    /// Filter by file path (contains).
    /// </summary>
    INodeQuery InFile(string filePath);

    /// <summary>
    /// Filter by file path pattern.
    /// </summary>
    INodeQuery InFiles(string filePattern);

    /// <summary>
    /// Filter by name pattern (supports wildcards).
    /// </summary>
    INodeQuery WithName(string namePattern);

    /// <summary>
    /// Filter by scope.
    /// </summary>
    INodeQuery InScope(string scope);

    /// <summary>
    /// Filter by modifier.
    /// </summary>
    INodeQuery WithModifier(string modifier);

    /// <summary>
    /// Filter classes only.
    /// </summary>
    INodeQuery Classes();

    /// <summary>
    /// Filter interfaces only.
    /// </summary>
    INodeQuery Interfaces();

    /// <summary>
    /// Filter methods only.
    /// </summary>
    INodeQuery Methods();

    /// <summary>
    /// Filter properties only.
    /// </summary>
    INodeQuery Properties();

    /// <summary>
    /// Filter functions only (PowerShell).
    /// </summary>
    INodeQuery Functions();

    /// <summary>
    /// Limit the number of results.
    /// </summary>
    INodeQuery Take(int count);

    /// <summary>
    /// Skip a number of results.
    /// </summary>
    INodeQuery Skip(int count);

    /// <summary>
    /// Execute the query and return results.
    /// </summary>
    Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute and return nodes directly.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> ToListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute and return count.
    /// </summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query builder for a single node with traversal options.
/// </summary>
public interface ISingleNodeQuery
{
    /// <summary>
    /// Get the node.
    /// </summary>
    Task<HypergraphNode?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Find callers of this node.
    /// </summary>
    ITraversalQuery Callers();

    /// <summary>
    /// Find callees of this node.
    /// </summary>
    ITraversalQuery Callees();

    /// <summary>
    /// Find nodes that depend on this node.
    /// </summary>
    ITraversalQuery Dependents();

    /// <summary>
    /// Find nodes that this node depends on.
    /// </summary>
    ITraversalQuery Dependencies();

    /// <summary>
    /// Find implementations (for interfaces).
    /// </summary>
    ITraversalQuery Implementations();

    /// <summary>
    /// Find derived classes (for classes).
    /// </summary>
    ITraversalQuery DerivedClasses();

    /// <summary>
    /// Find base classes/interfaces.
    /// </summary>
    ITraversalQuery BaseTypes();

    /// <summary>
    /// Analyze impact of changing this node.
    /// </summary>
    IImpactQuery Impact();

    /// <summary>
    /// Get edges connected to this node.
    /// </summary>
    IEdgeQuery Edges();
}

/// <summary>
/// Query builder for traversal operations.
/// </summary>
public interface ITraversalQuery
{
    /// <summary>
    /// Set the maximum depth.
    /// </summary>
    ITraversalQuery WithDepth(int depth);

    /// <summary>
    /// Filter by edge type.
    /// </summary>
    ITraversalQuery ViaEdgeType(HyperedgeType edgeType);

    /// <summary>
    /// Filter by multiple edge types.
    /// </summary>
    ITraversalQuery ViaEdgeTypes(params HyperedgeType[] edgeTypes);

    /// <summary>
    /// Filter by language.
    /// </summary>
    ITraversalQuery OfLanguage(string language);

    /// <summary>
    /// Include cross-language relationships.
    /// </summary>
    ITraversalQuery IncludeCrossLanguage();

    /// <summary>
    /// Limit results.
    /// </summary>
    ITraversalQuery Take(int count);

    /// <summary>
    /// Execute the query.
    /// </summary>
    Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute and return nodes with depth.
    /// </summary>
    Task<IReadOnlyList<NodeWithDepth>> ToListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query builder for impact analysis.
/// </summary>
public interface IImpactQuery
{
    /// <summary>
    /// Set the maximum depth for impact analysis.
    /// </summary>
    IImpactQuery WithDepth(int depth);

    /// <summary>
    /// Include only direct impacts.
    /// </summary>
    IImpactQuery DirectOnly();

    /// <summary>
    /// Execute the impact analysis.
    /// </summary>
    Task<ImpactAnalysis> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query builder for edge queries.
/// </summary>
public interface IEdgeQuery
{
    /// <summary>
    /// Filter by edge type.
    /// </summary>
    IEdgeQuery OfType(HyperedgeType edgeType);

    /// <summary>
    /// Filter by multiple edge types.
    /// </summary>
    IEdgeQuery OfTypes(params HyperedgeType[] edgeTypes);

    /// <summary>
    /// Filter incoming edges only.
    /// </summary>
    IEdgeQuery Incoming();

    /// <summary>
    /// Filter outgoing edges only.
    /// </summary>
    IEdgeQuery Outgoing();

    /// <summary>
    /// Execute the query.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query builder for semantic search.
/// </summary>
public interface ISemanticQuery
{
    /// <summary>
    /// Filter by language.
    /// </summary>
    ISemanticQuery InLanguage(string language);

    /// <summary>
    /// Filter by node type.
    /// </summary>
    ISemanticQuery OfType(string nodeType);

    /// <summary>
    /// Filter by file path.
    /// </summary>
    ISemanticQuery InFile(string filePath);

    /// <summary>
    /// Set minimum similarity score.
    /// </summary>
    ISemanticQuery WithMinScore(float minScore);

    /// <summary>
    /// Limit results.
    /// </summary>
    ISemanticQuery Take(int count);

    /// <summary>
    /// Also search the graph for related nodes.
    /// </summary>
    ISemanticQuery WithGraphExpansion(int depth = 1);

    /// <summary>
    /// Execute the query.
    /// </summary>
    Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute and return scored nodes.
    /// </summary>
    Task<IReadOnlyList<ScoredNode>> ToListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query builder for path queries.
/// </summary>
public interface IPathQuery
{
    /// <summary>
    /// Set maximum path length.
    /// </summary>
    IPathQuery WithMaxLength(int maxLength);

    /// <summary>
    /// Filter paths by edge types.
    /// </summary>
    IPathQuery ViaEdgeTypes(params HyperedgeType[] edgeTypes);

    /// <summary>
    /// Limit number of paths.
    /// </summary>
    IPathQuery Take(int count);

    /// <summary>
    /// Find shortest paths only.
    /// </summary>
    IPathQuery ShortestOnly();

    /// <summary>
    /// Execute the query.
    /// </summary>
    Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute and return paths.
    /// </summary>
    Task<IReadOnlyList<GraphPath>> ToListAsync(CancellationToken cancellationToken = default);
}
