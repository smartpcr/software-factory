// -----------------------------------------------------------------------
// <copyright file="IMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Models;

/// <summary>
/// Extended graph store for the memory layer. Adds temporal queries,
/// expertise CRUD, and audit logging on top of the base IGraphStore.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    /// <summary>
    /// Gets nodes filtered by memory layer with optional temporal and node-type filters.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets edges filtered by semantic relation with optional layer and temporal filters.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates a node with temporal version range.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates an edge.
    /// </summary>
    Task UpsertEdgeAsync(
        Hyperedge edge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a node by setting its UntilIndex.
    /// </summary>
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires an edge by setting its UntilIndex.
    /// </summary>
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates metadata on an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets non-archived expertise entries targeting a node, filtered by minimum confidence.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken cancellationToken = default);

    Task<ExpertiseEntry?> GetExpertiseByIdAsync(
        string expertiseId,
        CancellationToken ct = default);

    Task<ExpertiseEntry?> GetExpertiseByContentHashAsync(
        string contentHash,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpertiseEntry>> GetAllActiveExpertiseAsync(
        int limit = 1000,
        CancellationToken ct = default);

    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives an expertise entry (soft-delete).
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archived expertise entries, optionally filtered by target node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets per-layer statistics for the memory graph.
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a mutation in the audit log.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken cancellationToken = default);
}