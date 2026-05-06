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
/// </summary>
public interface IMemoryGraphStore : IGraphStore
{
    Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default);

    Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default);

    Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken ct = default);

    Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> updates,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default);

    Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken ct = default);

    Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 50,
        CancellationToken ct = default);

    Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken ct = default);

    Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default);
}
