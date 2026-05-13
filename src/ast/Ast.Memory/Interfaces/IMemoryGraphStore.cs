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
    // ── Canonical §5.1 — Layer-filtered queries with temporal support ───

    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    // ── Edge queries ────────────────────────────────────────────────────

    // ── Edge queries ────────────────────────────────────────────────────

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

    // ── Canonical §5.1 — Expertise CRUD ─────────────────────────────────

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

    // ── Canonical §5.1 — Statistics and audit ───────────────────────────

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

}