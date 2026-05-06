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
/// Extended graph store for the agent memory layer.
/// Extends the base <see cref="IGraphStore"/> with expertise lifecycle,
/// layer-filtered queries, temporal support, and audit logging
/// per architecture.md §5.1.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // ── Layer-filtered queries with temporal support ──

    /// <summary>
    /// Gets nodes filtered by memory layer with optional temporal snapshot.
    /// </summary>
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Gets edges filtered by semantic relation and optional memory layer.
    /// </summary>
    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    // ── Temporal-aware upsert and retirement ──

    /// <summary>
    /// Upserts a node with version range tracking.
    /// Serializes writes via SemaphoreSlim(1,1) for SQLite or ACID transactions for Neo4j.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default);

    /// <summary>
    /// Retires a node at a specific commit index, cascading to connected edges.
    /// </summary>
    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    /// <summary>
    /// Retires an edge at a specific commit index.
    /// </summary>
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    /// <summary>
    /// Updates metadata on an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> updates,
        CancellationToken ct = default);

    // ── Expertise CRUD ──

    /// <summary>
    /// Gets all non-archived expertise entries for a target node.
    /// The minConfidence threshold is evaluated against stored confidence.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates an expertise entry.
    /// </summary>
    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Archives (soft-deletes) an expertise entry.
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets archived expertise entries, optionally filtered by target node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 50,
        CancellationToken ct = default);

    // ── Statistics and audit ──

    /// <summary>
    /// Gets memory-specific graph statistics (node/edge counts by layer).
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Logs an audit trail entry.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);
}
