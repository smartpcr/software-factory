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
/// Concrete in-memory implementation of <see cref="IMemoryGraphStore"/>
/// for integration-style tests that verify real persistence/edge lifecycle
/// without depending on SQLite or Neo4j.
/// </summary>
internal sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertiseEntries = new();
    private readonly List<AuditLogEntry> auditLog = [];

    public string Name => "InMemory";

    // ── Audit log accessors for test assertions ──────────────────────────

    public IReadOnlyList<AuditLogEntry> AuditLog => this.auditLog;

    public IReadOnlyDictionary<string, ExpertiseEntry> ExpertiseEntries => this.expertiseEntries;

    public IReadOnlyDictionary<string, Hyperedge> Edges => this.edges;

    public IReadOnlyDictionary<string, HypergraphNode> Nodes => this.nodes;

    // ── IGraphStore ──────────────────────────────────────────────────────

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
        IReadOnlyList<HypergraphNode> result = this.nodes.Values.Take(limit).ToList();
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
        var typeSet = edgeTypes?.ToHashSet();
        IReadOnlyList<Hyperedge> result = this.edges.Values
            .Where(e => e.NodeIds.Contains(nodeId) && (typeSet == null || typeSet.Contains(e.EdgeType)))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GraphPath>>([]);

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>([]);

    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default)
    {
        this.nodes.TryGetValue(nodeId, out var sourceNode);
        return Task.FromResult(new ImpactAnalysis
        {
            SourceNode = sourceNode ?? new HypergraphNode { Id = nodeId, Name = nodeId, NodeType = "Unknown", Language = "Unknown", FilePath = string.Empty }
        });
    }

    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryRemove(nodeId, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.nodes.Clear();
        this.edges.Clear();
        this.expertiseEntries.Clear();
        this.auditLog.Clear();
        return Task.CompletedTask;
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GraphStatistics { TotalNodes = this.nodes.Count, TotalEdges = this.edges.Count });

    public Task CreateIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    // ── IMemoryGraphStore extensions ─────────────────────────────────────

    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null, int limit = 100, CancellationToken ct = default)
    {
        IReadOnlyList<HypergraphNode> result = this.nodes.Values
            .Where(n => n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var l) && (string?)l == memoryLayer)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null, int limit = 100, CancellationToken ct = default)
    {
        IReadOnlyList<Hyperedge> result = this.edges.Values
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) && (string?)sr == semanticRelation)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default) => Task.CompletedTask;

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default) => Task.CompletedTask;

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

    // ── Expertise CRUD ──────────────────────────────────────────────────

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        IReadOnlyList<ExpertiseEntry> result = this.expertiseEntries.Values
            .Where(e => e.TargetNodeId == targetNodeId && !e.Archived && e.Confidence >= minConfidence)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ExpertiseEntry?> GetExpertiseByIdAsync(string expertiseId, CancellationToken ct = default)
    {
        this.expertiseEntries.TryGetValue(expertiseId, out var entry);
        return Task.FromResult(entry);
    }

    public Task<ExpertiseEntry?> GetExpertiseByContentHashAsync(string contentHash, CancellationToken ct = default)
    {
        var entry = this.expertiseEntries.Values.FirstOrDefault(e => e.ContentHash == contentHash);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetAllActiveExpertiseAsync(int limit = 1000, CancellationToken ct = default)
    {
        IReadOnlyList<ExpertiseEntry> result = this.expertiseEntries.Values
            .Where(e => !e.Archived)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
    {
        this.expertiseEntries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
    {
        if (this.expertiseEntries.TryGetValue(expertiseId, out var entry))
        {
            entry.Archived = true;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        IReadOnlyList<ExpertiseEntry> result = this.expertiseEntries.Values
            .Where(e => e.Archived && (targetNodeId == null || e.TargetNodeId == targetNodeId))
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new MemoryGraphStatistics
        {
            ActiveExpertiseCount = this.expertiseEntries.Values.Count(e => !e.Archived),
            ArchivedExpertiseCount = this.expertiseEntries.Values.Count(e => e.Archived),
            AuditLogCount = this.auditLog.Count
        });
    }

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        this.auditLog.Add(new AuditLogEntry(sessionId, operation, detail, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    // ── IDisposable / IAsyncDisposable ───────────────────────────────────

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Represents a single audit log entry captured by the in-memory store.
/// </summary>
internal sealed record AuditLogEntry(string SessionId, string Operation, object Detail, DateTime Timestamp);