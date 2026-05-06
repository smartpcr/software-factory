// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Collections.Concurrent;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory implementation of <see cref="IMemoryGraphStore"/> for testing.
/// Implements the full <see cref="IGraphStore"/> contract so the store
/// can be used with any component expecting the canonical graph interface.
/// </summary>
internal sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();

    public IReadOnlyDictionary<string, HypergraphNode> Nodes => this.nodes;
    public IReadOnlyDictionary<string, Hyperedge> Edges => this.edges;

    // ── IGraphStore implementation ───────────────────────────────────────

    public string Name => "InMemory";

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

    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = this.nodes.Values.AsEnumerable();
        if (filter?.NodeTypes is not null)
        {
            var types = new HashSet<string>(filter.NodeTypes);
            query = query.Where(n => types.Contains(n.NodeType));
        }

        if (filter?.Language is not null)
        {
            query = query.Where(n => n.Language == filter.Language);
        }

        IReadOnlyList<HypergraphNode> result = query.Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
    {
        this.edges.TryGetValue(edgeId, out var edge);
        return Task.FromResult(edge);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId,
        IEnumerable<HyperedgeType>? edgeTypes = null,
        CancellationToken cancellationToken = default)
    {
        var query = this.edges.Values.Where(e => e.NodeIds.Contains(nodeId));
        if (edgeTypes is not null)
        {
            var types = new HashSet<HyperedgeType>(edgeTypes);
            query = query.Where(e => types.Contains(e.EdgeType));
        }

        IReadOnlyList<Hyperedge> result = query.ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId, string endNodeId, int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GraphPath> result = [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId, int depth = 1,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NodeWithDepth> result = [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId, int depth = 1,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NodeWithDepth> result = [];
        return Task.FromResult(result);
    }

    public Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId, int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        var node = this.nodes.TryGetValue(nodeId, out var n) ? n : new HypergraphNode
        {
            Id = nodeId, Name = nodeId, NodeType = "Unknown", Language = "Unknown", FilePath = ""
        };
        return Task.FromResult(new ImpactAnalysis { SourceNode = node });
    }

    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryRemove(nodeId, out _);
        var edgeKeysToRemove = this.edges
            .Where(kv => kv.Value.NodeIds.Contains(nodeId))
            .Select(kv => kv.Key).ToList();
        foreach (var key in edgeKeysToRemove)
        {
            this.edges.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.nodes.Clear();
        this.edges.Clear();
        return Task.CompletedTask;
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphStatistics
        {
            TotalNodes = this.nodes.Count,
            TotalEdges = this.edges.Count
        });

    public Task CreateIndexesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    // ── IMemoryGraphStore-specific operations ────────────────────────────

    public Task<IReadOnlyList<HypergraphNode>> GetAllNodesAsync(
        string? nodeType = null,
        CancellationToken ct = default)
    {
        var query = this.nodes.Values.AsEnumerable();
        if (nodeType is not null)
        {
            query = query.Where(n => n.NodeType == nodeType);
        }

        IReadOnlyList<HypergraphNode> result = query.ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Bridges canonical and memory-layer edges: matches edges by
    /// <c>Metadata["SemanticRelation"]</c> AND by <see cref="HyperedgeType"/>
    /// using <see cref="SemanticRelationMap"/>.
    /// </summary>
    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        IEnumerable<string> semanticRelations,
        CancellationToken ct = default)
    {
        var relations = new HashSet<string>(semanticRelations);
        var mappedTypes = SemanticRelationMap.ToHyperedgeTypes(relations);

        IReadOnlyList<Hyperedge> result = this.edges.Values
            .Where(e =>
                (e.Metadata.TryGetValue("SemanticRelation", out var sr)
                 && sr is string s && relations.Contains(s))
                || mappedTypes.Contains(e.EdgeType))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesByTypeAsync(
        IEnumerable<HyperedgeType> edgeTypes,
        CancellationToken ct = default)
    {
        var types = new HashSet<HyperedgeType>(edgeTypes);
        IReadOnlyList<Hyperedge> result = this.edges.Values
            .Where(e => types.Contains(e.EdgeType))
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpsertNodeAsync(HypergraphNode node, CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct = default)
    {
        this.edges[edge.Id] = edge;
        return Task.CompletedTask;
    }

    public Task RemoveEdgesBySemanticRelationAsync(
        string semanticRelation,
        CancellationToken ct = default)
    {
        var toRemove = this.edges
            .Where(kv => kv.Value.Metadata.TryGetValue("SemanticRelation", out var sr)
                         && sr is string s && s == semanticRelation)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            this.edges.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task RemoveNodesByTypeAsync(string nodeType, CancellationToken ct = default)
    {
        var toRemove = this.nodes
            .Where(kv => kv.Value.NodeType == nodeType)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            this.nodes.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    // ── §5.1 Layer-filtered queries ─────────────────────────────────────

    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var query = this.nodes.Values
            .Where(n => n.Metadata.TryGetValue("MemoryLayer", out var ml) &&
                        ml is string s && s == memoryLayer);

        if (filter?.NodeTypes is not null)
        {
            var types = new HashSet<string>(filter.NodeTypes);
            query = query.Where(n => types.Contains(n.NodeType));
        }

        IReadOnlyList<HypergraphNode> result = query.Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var query = this.edges.Values
            .Where(e => e.Metadata.TryGetValue("SemanticRelation", out var sr) &&
                        sr is string s && s == semanticRelation);

        if (memoryLayer is not null)
        {
            query = query.Where(e =>
                e.Metadata.TryGetValue("MemoryLayer", out var ml) &&
                ml is string s && s == memoryLayer);
        }

        IReadOnlyList<Hyperedge> result = query.Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            foreach (var kv in updates)
            {
                edge.Metadata[kv.Key] = kv.Value;
            }
        }

        return Task.CompletedTask;
    }

    // ── §5.1 Expertise CRUD ─────────────────────────────────────────────

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        IReadOnlyList<ExpertiseEntry> result = [];
        return Task.FromResult(result);
    }

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        IReadOnlyList<ExpertiseEntry> result = [];
        return Task.FromResult(result);
    }

    // ── §5.1 Statistics and audit ───────────────────────────────────────

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default) =>
        Task.FromResult(new MemoryGraphStatistics
        {
            TotalNodes = this.nodes.Count,
            TotalEdges = this.edges.Count,
            TopicNodes = this.nodes.Values.Count(n => n.NodeType == "Topic")
        });

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default) =>
        Task.CompletedTask;

    // ── Community-detection helpers ──────────────────────────────────────

    /// <summary>
    /// Seed the store with a node for test setup.
    /// </summary>
    public void AddNode(HypergraphNode node)
    {
        this.nodes[node.Id] = node;
    }

    /// <summary>
    /// Seed the store with an edge for test setup.
    /// </summary>
    public void AddEdge(Hyperedge edge)
    {
        this.edges[edge.Id] = edge;
    }
}