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
/// Memory-aware graph store that extends <see cref="IGraphStore"/> with
/// temporal versioning, layer-filtered queries, and expertise CRUD.
/// Per architecture.md §5.1 — implementations: SqliteGraphStore (local),
/// Neo4jMemoryGraphStore (team).
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    /// <summary>
    /// Gets nodes filtered by memory layer with optional temporal snapshot.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex,
        Models.NodeFilter? filter,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets edges filtered by semantic relation with optional layer and temporal snapshot.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer,
        int? atCommitIndex,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upserts a node with temporal version information.
    /// Serializes writes via SemaphoreSlim(1,1) (SQLite) or ACID transactions (Neo4j).
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires a node (and its connected edges) at the given commit index.
    /// </summary>
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires an edge at the given commit index.
    /// </summary>
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates metadata fields on an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets expertise entries for a target node, excluding archived entries.
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
    /// Archives (soft-deletes) an expertise entry.
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

    /// <summary>
    /// Gets memory-layer-aware statistics.
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Records an audit log entry.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken cancellationToken);
}
