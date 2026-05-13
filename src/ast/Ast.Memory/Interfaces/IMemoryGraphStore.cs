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
        int? atCommitIndex,
        Models.NodeFilter? filter,
        int limit,
        CancellationToken cancellationToken);

    // ── Edge queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns active edges matching the given semantic relation,
    /// optionally scoped to a memory layer and/or point-in-time commit index.
    /// </summary>
    Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(
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
        CancellationToken cancellationToken);

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

    // ── Edge mutations ──────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates an edge with the given version range and
    /// populates the edge-node join table.
    /// </summary>
    Task UpsertEdgeAsync(
        Hyperedge edge,
        VersionRange version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an edge as retired by setting its UntilCommitIndex
    /// and optionally UntilCommit SHA.
    /// </summary>
    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Merges the supplied metadata entries into an existing edge.
    /// </summary>
    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken);

    // ── Canonical §5.1 — Expertise CRUD ─────────────────────────────────

    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates an expertise entry (keyed by <see cref="ExpertiseEntry.Id"/>).
    /// </summary>
    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets <see cref="ExpertiseEntry.Archived"/> to true for the specified entry.
    /// </summary>
    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns archived expertise entries, optionally filtered to a target node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken);

    // ── Canonical §5.1 — Statistics and audit ───────────────────────────

    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends an immutable audit record for the given operation.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);

}