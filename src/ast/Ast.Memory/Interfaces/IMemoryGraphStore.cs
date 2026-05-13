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
/// Extended graph store interface for memory-layer operations.
/// Extends the canonical <see cref="IGraphStore"/> contract (17 methods) with
/// layer-filtered queries, temporal support, expertise CRUD, and community-
/// detection helpers.
/// Per architecture.md §5.1.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // ── §5.1 Layer-filtered queries with temporal support ────────────────

    /// <summary>
    /// Gets nodes filtered by memory layer, with optional temporal snapshot.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex,
        Models.NodeFilter? filter,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets edges matching a semantic relation, optionally filtered by layer
    /// and temporal snapshot. Per §5.1 canonical signature.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer,
        int? atCommitIndex,
        int limit,
        CancellationToken cancellationToken);

    // ── §5.1 Temporal-aware upsert and retirement ────────────────────────

    /// <summary>
    /// Upserts a node with temporal version range.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a node at the given commit index (cascades to connected edges).
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates specific metadata fields on an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken);

    // ── §5.1 Expertise CRUD ─────────────────────────────────────────────

    /// <summary>
    /// Gets non-archived expertise entries targeting the given node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates an expertise entry.
    /// </summary>
    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes an expertise entry (sets archived = true).
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns archived expertise entries, optionally filtered by target node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken);

    // ── §5.1 Statistics and audit ───────────────────────────────────────

    /// <summary>
    /// Gets memory-specific graph statistics (per-layer counts, expertise metrics).
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an audit entry for a graph mutation.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);

    // ── Community-detection helpers (used by Stage 5.4) ──────────────────

    /// <summary>
    /// Gets all nodes in the graph, optionally filtered by node type.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetAllNodesAsync(
        string? nodeType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets edges matching one or more semantic relation types.
    /// Bridges canonical edges (by <see cref="HyperedgeType"/> label) AND
    /// memory-layer edges (by <c>Metadata["SemanticRelation"]</c>).
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        IEnumerable<string> semanticRelations,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all edges of the specified <see cref="HyperedgeType"/> values.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesByTypeAsync(
        IEnumerable<HyperedgeType> edgeTypes,
        CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a node (non-temporal overload for community detection).
    /// </summary>
    Task UpsertNodeAsync(HypergraphNode node, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates an edge.
    /// </summary>
    Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct = default);

    /// <summary>
    /// Removes edges matching a semantic relation. Used for idempotent re-clustering.
    /// </summary>
    Task RemoveEdgesBySemanticRelationAsync(
        string semanticRelation,
        CancellationToken ct = default);

    /// <summary>
    /// Removes all nodes of a given type (e.g., "Topic" before re-detection).
    /// </summary>
    Task RemoveNodesByTypeAsync(string nodeType, CancellationToken ct = default);
}