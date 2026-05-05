// -----------------------------------------------------------------------
// <copyright file="IGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.Interfaces;

using Ast.Core;

/// <summary>
/// Base graph storage contract providing CRUD operations on hypergraph nodes and edges.
/// Implementations may target SQLite, Neo4j, or other backends.
/// </summary>
public interface IGraphStore : IDisposable
{
    /// <summary>
    /// Inserts or updates a node in the graph.
    /// </summary>
    Task UpsertNodeAsync(HypergraphNode node, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a node by its identifier.
    /// </summary>
    Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates an edge in the graph.
    /// </summary>
    Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct = default);

    /// <summary>
    /// Retrieves an edge by its identifier.
    /// </summary>
    Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node and all its connected edges.
    /// </summary>
    Task DeleteNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Deletes an edge by its identifier.
    /// </summary>
    Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default);

    /// <summary>
    /// Returns neighbor node IDs up to a given traversal depth.
    /// </summary>
    Task<IReadOnlyList<string>> GetNeighborsAsync(string nodeId, int depth = 1, CancellationToken ct = default);
}