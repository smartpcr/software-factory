// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Collections.Concurrent;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory implementation of <see cref="IMemoryGraphStore"/> for unit testing.
/// Only expertise-related methods are fully implemented since that is what
/// <see cref="Evolution.DecayService"/> consumes.
/// </summary>
internal sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertise = new();
    private readonly ConcurrentDictionary<string, string> appliedContentHashes = new();

    // ── Expertise (fully implemented for tests) ─────────────────────────

    public Task<IReadOnlyList<ExpertiseEntry>> GetAllActiveExpertiseAsync(
        CancellationToken cancellationToken)
    {
        var result = this.expertise.Values
            .Where(e => !e.Archived)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence,
        CancellationToken cancellationToken)
    {
        var result = this.expertise.Values
            .Where(e => e.TargetNodeId == targetNodeId && !e.Archived && e.Confidence >= minConfidence)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken cancellationToken)
    {
        this.expertise[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken cancellationToken)
    {
        if (this.expertise.TryGetValue(expertiseId, out var entry))
        {
            entry.MarkArchived();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = this.expertise.Values
            .Where(e => e.Archived)
            .Where(e => targetNodeId == null || e.TargetNodeId == targetNodeId)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    // ── Content hash tracking (for idempotent event-driven decay) ──────

    public Task<string?> GetLastAppliedContentHashAsync(string nodeId, CancellationToken cancellationToken)
    {
        this.appliedContentHashes.TryGetValue(nodeId, out var hash);
        return Task.FromResult(hash);
    }

    public Task SetLastAppliedContentHashAsync(string nodeId, string contentHash, CancellationToken cancellationToken)
    {
        this.appliedContentHashes[nodeId] = contentHash;
        return Task.CompletedTask;
    }

    // ── Stub implementations (not used by DecayService) ─────────────────

    public string Name => "InMemoryTest";

    public Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(string memoryLayer, int? atCommitIndex, Models.NodeFilter? filter, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MemoryNode>>(Array.Empty<MemoryNode>());

    public Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(string semanticRelation, string? memoryLayer, int? atCommitIndex, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MemoryEdge>>(Array.Empty<MemoryEdge>());

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpsertEdgeAsync(Hyperedge edge, VersionRange version, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> metadata, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new MemoryGraphStatistics());

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken)
        => Task.FromResult<HypergraphNode?>(null);

    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(Ast.GraphDb.NodeFilter? filter, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HypergraphNode>>(Array.Empty<HypergraphNode>());

    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken)
        => Task.FromResult<Hyperedge?>(null);

    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Hyperedge>>(Array.Empty<Hyperedge>());

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<GraphPath>>(Array.Empty<GraphPath>());

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth, CancellationToken cancellationToken)
        => Task.FromResult(new ImpactAnalysis
        {
            SourceNode = new HypergraphNode
            {
                Id = nodeId,
                NodeType = "stub",
                FilePath = "stub",
                Name = "stub",
                Language = "stub",
            },
        });

    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new GraphStatistics());

    public Task CreateIndexesAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() { }
}
