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
    // ── Node queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns active nodes belonging to the specified memory layer,
    /// optionally filtered to a point-in-time commit index.
    /// </summary>
    Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(
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

    // ── Node mutations ──────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates a node with the given version range.
    /// If the node already exists, its metadata and version range are merged.
    /// </summary>
    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a node as retired (no longer in HEAD) by setting its
    /// UntilCommitIndex and optionally UntilCommit SHA to the specified values.
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

    // ── Expertise ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns all non-archived expertise entries across every node.
    /// Used by <see cref="Evolution.DecayService"/> for store-wide stale scans.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetAllActiveExpertiseAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns non-archived expertise entries linked to the target node
    /// whose confidence meets or exceeds <paramref name="minConfidence"/>.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence,
        CancellationToken cancellationToken);

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

    // ── Diagnostics ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the last content hash that was applied for event-driven decay
    /// on the specified node. Returns null if no event-driven decay has been
    /// recorded for this node. Used for idempotence across process restarts.
    /// </summary>
    Task<string?> GetLastAppliedContentHashAsync(
        string nodeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the content hash that was just applied for event-driven decay
    /// on the specified node. Atomically persisted so that concurrent or
    /// restarted syncs do not double-apply the same decay.
    /// </summary>
    Task SetLastAppliedContentHashAsync(
        string nodeId,
        string contentHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns aggregate statistics for the memory graph (active counts only).
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends an immutable audit record for the given operation.
    /// </summary>
    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken cancellationToken);
}
