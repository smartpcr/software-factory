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
using MemoryNodeFilter = Ast.Memory.Models.NodeFilter;

/// <summary>
/// Extended graph store for the memory layer. Adds temporal queries,
/// expertise CRUD, and audit logging on top of the base IGraphStore.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // Layer-filtered queries with temporal support

    /// <summary>
    /// Gets nodes by memory layer with optional temporal and filter criteria.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    // ── Edge queries ────────────────────────────────────────────────────

    // ── Edge queries ────────────────────────────────────────────────────

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
    /// Inserts or updates a node with the given version range.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a node as retired by setting its UntilCommitIndex.
    /// </summary>
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates an edge with the given version range.
    /// </summary>
    Task UpsertEdgeAsync(
        Hyperedge edge,
        VersionRange version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an edge as retired by setting its UntilCommitIndex.
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
        CancellationToken cancellationToken);

    // Expertise CRUD

    /// <summary>
    /// Returns non-archived expertise entries for a target node.
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
    /// Archives an expertise entry.
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns archived expertise entries.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken);

    // Statistics and audit

    /// <summary>
    /// Returns aggregate statistics for the memory graph.
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an audit event.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken cancellationToken);
}