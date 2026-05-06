// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Helpers;

using System.Collections.Concurrent;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Concrete in-memory implementation of <see cref="IMemoryGraphStore"/> for integration-level
/// tests that prove edge and expertise persistence works against real storage, not just mocks.
/// </summary>
internal sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertise = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> archivedExpertise = new();
    private readonly List<object> auditLog = [];

    public string Name => "InMemory";

    // ── IGraphStore ──

    public Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var node in nodes)
        {
            this.nodes[node.Id] = node;
            count++;
        }

        return Task.FromResult(count);
    }

    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default)
    {
        this.edges[edge.Id] = edge;
        return Task.CompletedTask;
    }

    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var edge in edges)
        {
            this.edges[edge.Id] = edge;
            count++;
        }

        return Task.FromResult(count);
    }

    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(this.nodes.TryGetValue(nodeId, out var node) ? node : null);

    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HypergraphNode>>(this.nodes.Values.Take(limit).ToList());

    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(this.edges.TryGetValue(edgeId, out var edge) ? edge : null);

    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default)
    {
        var result = this.edges.Values
            .Where(e => e.NodeIds.Contains(nodeId))
            .Where(e => edgeTypes is null || edgeTypes.Contains(e.EdgeType))
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphPath>>([]);

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);

    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImpactAnalysis
        {
            SourceNode = new HypergraphNode
            {
                Id = nodeId,
                NodeType = "unknown",
                Name = nodeId,
                Language = "unknown",
                FilePath = string.Empty
            }
        });

    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryRemove(nodeId, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.nodes.Clear();
        this.edges.Clear();
        this.expertise.Clear();
        this.archivedExpertise.Clear();
        return Task.CompletedTask;
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphStatistics { TotalNodes = this.nodes.Count, TotalEdges = this.edges.Count });

    public Task CreateIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    // ── IMemoryGraphStore ──

    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HypergraphNode>>([]);

    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null, int limit = 100, CancellationToken ct = default)
    {
        var result = this.edges.Values
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) && sr?.ToString() == semanticRelation)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default) => Task.CompletedTask;

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
    {
        this.edges.TryRemove(edgeId, out _);
        return Task.CompletedTask;
    }

    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            foreach (var (key, value) in updates)
            {
                edge.Metadata[key] = value;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        var result = this.expertise.Values
            .Where(e => e.TargetNodeId == targetNodeId && e.Confidence >= minConfidence)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
    {
        this.expertise[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
    {
        if (this.expertise.TryRemove(expertiseId, out var entry))
        {
            this.archivedExpertise[expertiseId] = entry;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        var result = this.archivedExpertise.Values
            .Where(e => targetNodeId is null || e.TargetNodeId == targetNodeId)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default) =>
        Task.FromResult(new MemoryGraphStatistics());

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        this.auditLog.Add(new { sessionId, operation, detail });
        return Task.CompletedTask;
    }

    // ── Query helpers for test assertions ──

    public IReadOnlyList<Hyperedge> GetAllEdges() => this.edges.Values.ToList();

    public IReadOnlyList<ExpertiseEntry> GetAllExpertise() => this.expertise.Values.ToList();

    public IReadOnlyList<ExpertiseEntry> GetAllArchived() => this.archivedExpertise.Values.ToList();

    // ── IDisposable / IAsyncDisposable ──

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
