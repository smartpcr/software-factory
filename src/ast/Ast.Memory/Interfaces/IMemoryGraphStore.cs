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
/// Persistence contract for the agent memory hypergraph.
/// Extends <see cref="IGraphStore"/> with memory-specific operations
/// for versioned nodes/edges, expertise, auditing, and statistics.
/// Per architecture.md §5.1 — signatures match the canonical spec exactly.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // ── Canonical §5.1 — Layer-filtered queries with temporal support ───

    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex,
        MemoryNodeFilter? filter,
        int limit,
        CancellationToken cancellationToken);

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

    // ── Canonical §5.1 — Temporal-aware upsert and retirement ──────────

    /// <summary>
    /// Temporal-aware upsert: creates or updates a node with the given version range.
    /// If a node with the same ID already exists, the store atomically closes the
    /// previous active version (sets its <c>UntilIndex = version.SinceIndex</c>)
    /// and activates the new version. This preserves temporal history per §4.3
    /// without cascading edge retirement — edges reference the logical node ID
    /// and remain valid for the successor version.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a node AND cascades retirement to all connected edges.
    /// Used when a node is permanently removed (e.g., deleted file, removed symbol).
    /// Sets UntilIndex on the node and all edges connected to it.
    /// Per architecture.md §5.1.
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

    // ── Canonical §5.1 — Expertise CRUD ─────────────────────────────────

    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts an expertise entry.
    /// </summary>
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

    // ── Canonical §5.1 — Statistics and audit ───────────────────────────

    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an audit event.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);

}