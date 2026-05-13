// -----------------------------------------------------------------------
// <copyright file="IMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Models;

/// <summary>
/// Extended graph store for memory layer operations per architecture §5.1.
/// Extends <see cref="IGraphStore"/>. Implementations: SqliteGraphStore (local), Neo4jMemoryGraphStore (team).
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // Layer-filtered queries with temporal support

    /// <summary>
    /// Gets nodes by memory layer with optional temporal and filter criteria.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex,
        Models.NodeFilter? filter,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets edges by semantic relation with optional layer and temporal criteria.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer,
        int? atCommitIndex,
        int limit,
        CancellationToken cancellationToken);

    // Temporal-aware upsert and retirement

    /// <summary>
    /// Upserts a node with version range for temporal tracking.
    /// Serialized via SemaphoreSlim(1,1) (SQLite) or ACID transactions (Neo4j).
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires a node and cascades retirement to all connected edges.
    /// </summary>
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires an edge by setting its UntilIndex.
    /// </summary>
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates metadata on an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken);

    // Expertise CRUD

    /// <summary>
    /// Gets expertise entries for a target node. Excludes archived entries.
    /// minConfidence threshold is evaluated against stored confidence.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upserts an expertise entry.
    /// </summary>
    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken);

    /// <summary>
    /// Archives an expertise entry (soft-delete).
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets archived expertise entries.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken);

    // Statistics and audit

    /// <summary>
    /// Gets memory-specific statistics.
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Logs an audit event.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);

    // Convenience bulk operations (supplement §5.1 for pipeline use)

    /// <summary>
    /// Bulk upsert nodes. Delegates to individual UpsertNodeAsync calls.
    /// </summary>
    Task<int> UpsertNodesAsync(IEnumerable<HypergraphNode> nodes, VersionRange version, CancellationToken ct = default);

    /// <summary>
    /// Bulk upsert edges.
    /// </summary>
    Task<int> UpsertEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken ct = default);

    /// <summary>
    /// Gets nodes associated with a given file path.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByFilePathAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Gets edges where at least one endpoint is in the given node IDs.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesForNodesAsync(IEnumerable<string> nodeIds, CancellationToken ct = default);
}