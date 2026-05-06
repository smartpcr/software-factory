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
        CancellationToken ct = default);

    /// <summary>
    /// Retires a node AND cascades retirement to all connected edges.
    /// Used when a node is permanently removed (e.g., deleted file, removed symbol).
    /// Sets UntilIndex on the node and all edges connected to it.
    /// Per architecture.md §5.1.
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

    // ── Canonical §5.1 — Expertise CRUD ─────────────────────────────────

    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

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

    // ── Canonical §5.1 — Statistics and audit ───────────────────────────

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