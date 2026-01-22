// -----------------------------------------------------------------------
// <copyright file="IGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Ast.Core;

/// <summary>
/// Interface for graph database operations.
/// </summary>
public interface IGraphStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the name of the graph store implementation.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Stores a hypergraph node in the graph database.
    /// </summary>
    /// <param name="node">The node to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores multiple nodes in batch.
    /// </summary>
    /// <param name="nodes">The nodes to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of nodes created.</returns>
    Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a hyperedge in the graph database.
    /// </summary>
    /// <param name="edge">The hyperedge to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores multiple hyperedges in batch.
    /// </summary>
    /// <param name="edges">The hyperedges to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of edges created.</returns>
    Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a node by its ID.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The node if found, null otherwise.</returns>
    Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all nodes matching the specified criteria.
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching nodes.</returns>
    Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an edge by its ID.
    /// </summary>
    /// <param name="edgeId">The edge ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The edge if found, null otherwise.</returns>
    Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all edges connected to a node.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="edgeTypes">Optional edge types to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connected edges.</returns>
    Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId,
        IEnumerable<HyperedgeType>? edgeTypes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds paths between two nodes.
    /// </summary>
    /// <param name="startNodeId">Starting node ID.</param>
    /// <param name="endNodeId">Ending node ID.</param>
    /// <param name="maxDepth">Maximum path depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paths between the nodes.</returns>
    Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId,
        string endNodeId,
        int maxDepth = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets nodes that call/use the specified node.
    /// </summary>
    /// <param name="nodeId">The target node ID.</param>
    /// <param name="depth">How many levels of callers to traverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Calling nodes with their relationship depth.</returns>
    Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId,
        int depth = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets nodes that are called/used by the specified node.
    /// </summary>
    /// <param name="nodeId">The source node ID.</param>
    /// <param name="depth">How many levels of callees to traverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Called nodes with their relationship depth.</returns>
    Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId,
        int depth = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes the impact of changing a node.
    /// </summary>
    /// <param name="nodeId">The node ID to analyze.</param>
    /// <param name="maxDepth">Maximum depth to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Impact analysis result.</returns>
    Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId,
        int maxDepth = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a node and its connected edges.
    /// </summary>
    /// <param name="nodeId">The node ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all nodes and edges.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about the graph.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph statistics.</returns>
    Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates indexes for optimal query performance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the graph store is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the store is available.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
