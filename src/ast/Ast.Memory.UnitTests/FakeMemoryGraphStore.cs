// -----------------------------------------------------------------------
// <copyright file="FakeMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using MemoryNodeFilter = Ast.Memory.Models.NodeFilter;

/// <summary>
/// Minimal in-memory fake for <see cref="IMemoryGraphStore"/> that captures
/// upserted edges for test assertions. Only the methods exercised by
/// <see cref="Ast.Memory.Extraction.CallChainAnalyzer"/> are implemented;
/// remaining methods throw <see cref="NotImplementedException"/>.
/// </summary>
internal sealed class FakeMemoryGraphStore : IMemoryGraphStore
{
    public List<(Hyperedge Edge, VersionRange Version)> UpsertedEdges { get; } = [];
    public List<(HypergraphNode Node, VersionRange Version)> UpsertedNodes { get; } = [];

    public string Name => "FakeMemoryGraphStore";

    public bool ShouldFailOnUpsertEdge { get; set; }

    public Task UpsertEdgeAsync(Hyperedge edge, VersionRange version, CancellationToken ct)
    {
        if (this.ShouldFailOnUpsertEdge)
        {
            throw new InvalidOperationException("Simulated persistence failure");
        }

        this.UpsertedEdges.Add((edge, version));
        return Task.CompletedTask;
    }

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct)
    {
        this.UpsertedNodes.Add((node, version));
        return Task.CompletedTask;
    }

    // ── IGraphStore stubs (not exercised by CallChainAnalyzer) ───────────

    public Task CreateNodeAsync(HypergraphNode node, CancellationToken ct) => Task.CompletedTask;
    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken ct) => Task.FromResult(0);
    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken ct) => Task.CompletedTask;
    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken ct) => Task.FromResult(0);
    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken ct) => Task.FromResult<HypergraphNode?>(null);
    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(Ast.GraphDb.NodeFilter? filter, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<HypergraphNode>>([]);
    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken ct) => Task.FromResult<Hyperedge?>(null);
    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes, CancellationToken ct) => Task.FromResult<IReadOnlyList<Hyperedge>>([]);
    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphPath>>([]);
    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth, CancellationToken ct) => Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);
    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth, CancellationToken ct) => Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);
    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth, CancellationToken ct) => throw new NotImplementedException();
    public Task DeleteNodeAsync(string nodeId, CancellationToken ct) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken ct) => Task.FromResult(new GraphStatistics());
    public Task CreateIndexesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    // ── IMemoryGraphStore stubs ─────────────────────────────────────────

    public Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(string memoryLayer, int? atCommitIndex, MemoryNodeFilter? filter, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryNode>>([]);
    public Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(string semanticRelation, string? memoryLayer, int? atCommitIndex, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<MemoryEdge>>([]);
    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, string? untilCommit, CancellationToken ct) => Task.CompletedTask;
    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, string? untilCommit, CancellationToken ct) => Task.CompletedTask;
    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> metadata, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(string targetNodeId, double minConfidence, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExpertiseEntry>>([]);
    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct) => Task.CompletedTask;
    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(string? targetNodeId, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExpertiseEntry>>([]);
    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct) => Task.FromResult(new MemoryGraphStatistics());
    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct) => Task.CompletedTask;

    // ── IDisposable / IAsyncDisposable ──────────────────────────────────

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}