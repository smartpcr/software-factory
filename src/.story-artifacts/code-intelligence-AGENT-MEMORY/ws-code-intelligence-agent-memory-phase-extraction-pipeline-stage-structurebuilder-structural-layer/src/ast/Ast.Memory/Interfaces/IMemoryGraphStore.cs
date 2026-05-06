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
/// Per architecture.md §5.1.
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
        NodeFilter? filter,
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
    /// Returns non-archived expertise entries linked to the target node
    /// whose confidence meets or exceeds <paramref name="minConfidence"/>.
    /// </summary>
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

    // ── Diagnostics ─────────────────────────────────────────────────────

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