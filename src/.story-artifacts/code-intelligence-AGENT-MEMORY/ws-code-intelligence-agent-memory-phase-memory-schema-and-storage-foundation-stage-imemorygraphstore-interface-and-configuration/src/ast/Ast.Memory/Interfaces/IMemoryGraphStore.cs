// -----------------------------------------------------------------------
// <copyright file="IMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Core;
using Ast.GraphDb.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Extended graph store contract for the agent memory layer.
/// Extends <see cref="IGraphStore"/> with temporal versioning, memory-layer
/// queries, expertise management, statistics, and audit logging.
/// Per architecture.md §5.1.
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    // ── Layer-aware queries ─────────────────────────────────────────

    /// <summary>
    /// Returns nodes belonging to the specified memory layer, optionally filtered
    /// by commit index and additional criteria.
    /// </summary>
    /// <param name="memoryLayer">Memory layer name (e.g. "structural", "semantic").</param>
    /// <param name="atCommitIndex">If set, only return nodes valid at this commit index.</param>
    /// <param name="filter">Optional additional filter criteria.</param>
    /// <param name="limit">Maximum number of nodes to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Returns edges matching the given semantic relation type, optionally scoped
    /// to a layer and/or commit index.
    /// </summary>
    /// <param name="semanticRelation">Semantic relation type (e.g. "CALLS", "CONTAINS").</param>
    /// <param name="memoryLayer">If set, restrict to edges in this layer.</param>
    /// <param name="atCommitIndex">If set, only return edges valid at this commit index.</param>
    /// <param name="limit">Maximum number of edges to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    // ── Temporal upsert / retire ────────────────────────────────────

    /// <summary>
    /// Inserts or updates a node with an associated version range.
    /// </summary>
    Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default);

    /// <summary>
    /// Retires (soft-deletes) a node by setting its version range's UntilIndex.
    /// The node remains queryable for historical commit indices.
    /// </summary>
    Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default);

    /// <summary>
    /// Retires (soft-deletes) an edge by setting its version range's UntilIndex.
    /// </summary>
    Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default);

    /// <summary>
    /// Merges the supplied metadata entries into the edge's existing metadata dictionary.
    /// Null values remove the corresponding key.
    /// </summary>
    Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> metadata, CancellationToken ct = default);

    // ── Expertise ───────────────────────────────────────────────────

    /// <summary>
    /// Returns active (non-archived) expertise entries attached to a node
    /// whose confidence meets the minimum threshold.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates an expertise entry. Matching is by <see cref="ExpertiseEntry.Id"/>.
    /// </summary>
    Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Archives an expertise entry by ID, marking it as no longer active.
    /// </summary>
    Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default);

    /// <summary>
    /// Returns archived expertise entries, optionally filtered to a specific target node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 100,
        CancellationToken ct = default);

    // ── Statistics & Audit ──────────────────────────────────────────

    /// <summary>
    /// Returns aggregate statistics about the memory graph (per-layer node/edge counts).
    /// </summary>
    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Appends an audit log entry for traceability of memory mutations.
    /// </summary>
    /// <param name="sessionId">Identifier for the agent session.</param>
    /// <param name="operation">Operation name (e.g. "upsert_node", "retire_edge").</param>
    /// <param name="detail">Arbitrary detail object (serialized to JSON by the implementation).</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default);
}