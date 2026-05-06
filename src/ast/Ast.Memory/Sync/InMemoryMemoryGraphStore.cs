// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Collections.Concurrent;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory implementation of <see cref="IMemoryGraphStore"/> that provides
/// real storage semantics for temporal versioning, cascading retirement, and
/// expertise management. Used for integration-level tests that verify actual
/// storage behavior rather than mock interactions.
///
/// Storage strategy: each node is stored under its logical ID only. When
/// UpsertNodeAsync is called, the previous version's metadata is updated
/// with UntilIndex and the node object is replaced in the dictionary.
/// A separate history list preserves retired versions for temporal queries.
/// This avoids returning duplicate active nodes from GetNodesAsync.
/// </summary>
public sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertise = new();
    private readonly List<HypergraphNode> nodeHistory = new();
    private readonly List<Hyperedge> edgeHistory = new();
    private readonly List<object> auditLog = new();

    public string Name => "InMemory";

    // ── IGraphStore ─────────────────────────────────────────────────

    public Task CreateNodeAsync(HypergraphNode node, CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken ct = default)
    {
        int count = 0;
        foreach (var n in nodes)
        {
            this.nodes[n.Id] = n;
            count++;
        }

        return Task.FromResult(count);
    }

    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken ct = default)
    {
        // If an existing edge with the same ID is retired, preserve it in history
        // before overwriting so temporal edge versions are not lost.
        if (this.edges.TryGetValue(edge.Id, out var existing) && IsRetired(existing))
        {
            this.edgeHistory.Add(existing);
        }

        this.edges[edge.Id] = edge;
        return Task.CompletedTask;
    }

    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken ct = default)
    {
        int count = 0;
        foreach (var e in edges)
        {
            this.edges[e.Id] = e;
            count++;
        }

        return Task.FromResult(count);
    }

    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        this.nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(NodeFilter? filter = null, int limit = 100, CancellationToken ct = default)
    {
        IEnumerable<HypergraphNode> query;
        if (filter?.IncludeRetired == true)
        {
            // Include both current nodes and historical (retired) versions.
            // nodeHistory contains prior versions that were superseded by UpsertNodeAsync.
            // Without this, orphan edge cleanup for deleted files would miss edges
            // connected to historical node versions that are no longer in the active dict.
            var combined = new Dictionary<string, HypergraphNode>();
            foreach (var h in this.nodeHistory)
            {
                combined[h.Id + "::hist:" + h.GetHashCode()] = h;
            }

            foreach (var n in this.nodes.Values)
            {
                combined[n.Id] = n;
            }

            query = combined.Values;
        }
        else
        {
            query = this.nodes.Values.Where(n => !IsRetiredNode(n));
        }
        if (filter?.FilePath is not null)
        {
            query = query.Where(n => n.FilePath == filter.FilePath || n.FilePath.StartsWith(filter.FilePath));
        }

        if (filter?.Language is not null)
        {
            query = query.Where(n => n.Language == filter.Language);
        }

        if (filter?.NodeTypes is not null)
        {
            var types = filter.NodeTypes.ToHashSet();
            query = query.Where(n => types.Contains(n.NodeType));
        }

        return Task.FromResult<IReadOnlyList<HypergraphNode>>(query.Take(limit).ToList());
    }

    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken ct = default)
    {
        this.edges.TryGetValue(edgeId, out var edge);
        return Task.FromResult(edge);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken ct = default)
    {
        var query = this.edges.Values.Where(e => e.NodeIds.Contains(nodeId));
        if (edgeTypes is not null)
        {
            var types = edgeTypes.ToHashSet();
            query = query.Where(e => types.Contains(e.EdgeType));
        }

        return Task.FromResult<IReadOnlyList<Hyperedge>>(query.ToList());
    }

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GraphPath>>(Array.Empty<GraphPath>());

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth = 1, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth = 1, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NodeWithDepth>>(Array.Empty<NodeWithDepth>());

    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth = 3, CancellationToken ct = default)
        => Task.FromResult(new ImpactAnalysis { SourceNode = this.nodes.GetValueOrDefault(nodeId)! });

    public Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        this.nodes.TryRemove(nodeId, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        this.nodes.Clear();
        this.edges.Clear();
        this.nodeHistory.Clear();
        return Task.CompletedTask;
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken ct = default)
        => Task.FromResult(new GraphStatistics { TotalNodes = this.nodes.Count, TotalEdges = this.edges.Count });

    public Task CreateIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    // ── IMemoryGraphStore ───────────────────────────────────────────

    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null,
        int limit = 100, CancellationToken ct = default)
    {
        var query = this.nodes.Values.Where(n =>
            !IsRetiredNode(n) &&
            n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) &&
            ml?.ToString() == memoryLayer);
        return Task.FromResult<IReadOnlyList<HypergraphNode>>(query.Take(limit).ToList());
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null,
        int limit = 100, CancellationToken ct = default)
    {
        var query = this.edges.Values.Where(e =>
            !IsRetired(e) &&
            e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) &&
            sr?.ToString() == semanticRelation);
        return Task.FromResult<IReadOnlyList<Hyperedge>>(query.Take(limit).ToList());
    }

    /// <summary>
    /// Temporal-aware upsert: if a node with the same ID exists and is active
    /// (no UntilIndex), atomically close it by setting its UntilIndex to
    /// the new version's SinceIndex, move it to history, then store the new version
    /// under the same logical key. Only the logical key is used — no versioned
    /// keys — so GetNodesAsync never returns duplicates.
    /// </summary>
    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        if (this.nodes.TryGetValue(node.Id, out var existing) && existing != node)
        {
            var existingVersion = VersionRangeHelper.ExtractVersion(existing.Metadata);
            if (existingVersion is not null && existingVersion.UntilIndex is null)
            {
                existing.Metadata[MemoryMetadataKeys.UntilCommit] = version.SinceCommit;
                existing.Metadata[MemoryMetadataKeys.UntilIndex] = version.SinceIndex;
            }

            // Preserve the retired version in the history list for temporal queries
            this.nodeHistory.Add(existing);
        }

        node.Metadata[MemoryMetadataKeys.SinceCommit] = version.SinceCommit;
        node.Metadata[MemoryMetadataKeys.SinceIndex] = version.SinceIndex;
        if (version.UntilCommit is not null)
        {
            node.Metadata[MemoryMetadataKeys.UntilCommit] = version.UntilCommit;
        }

        if (version.UntilIndex is not null)
        {
            node.Metadata[MemoryMetadataKeys.UntilIndex] = version.UntilIndex;
        }

        // Store under the logical key only — overwrites the old entry
        this.nodes[node.Id] = node;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Retires a node AND cascades retirement to all connected edges.
    /// Sets UntilIndex on the node and all edges connected to it.
    /// </summary>
    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default)
    {
        if (this.nodes.TryGetValue(nodeId, out var node))
        {
            node.Metadata[MemoryMetadataKeys.UntilIndex] = untilCommitIndex;
        }

        // Cascade to connected edges
        foreach (var edge in this.edges.Values)
        {
            if (edge.NodeIds.Contains(nodeId) && !IsRetired(edge))
            {
                edge.Metadata[MemoryMetadataKeys.UntilIndex] = untilCommitIndex;
            }
        }

        return Task.CompletedTask;
    }

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
    {
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            edge.Metadata[MemoryMetadataKeys.UntilIndex] = untilCommitIndex;
        }

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

    // ── Expertise ───────────────────────────────────────────────────

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        var results = this.expertise.Values
            .Where(e => e.TargetNodeId == targetNodeId && e.Confidence >= minConfidence && !e.Archived)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(results);
    }

    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
    {
        this.expertise[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
    {
        if (this.expertise.TryGetValue(expertiseId, out var entry))
        {
            // Preserve the current (already-decayed) confidence when archiving.
            // Per architecture.md §3.5.1 (lines 456-458): event-driven decay
            // persists the decayed confidence BEFORE archiving, so the entry's
            // current Confidence reflects the multiplied decay. We must preserve
            // it — not reset or reconstruct from the original.
            this.expertise[expertiseId] = new ExpertiseEntry(entry.Kind, entry.Title, entry.Body, entry.TargetNodeId, entry.SourceAgent)
            {
                Archived = true,
                Confidence = entry.Confidence,
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        var results = this.expertise.Values.Where(e => e.Archived);
        if (targetNodeId is not null)
        {
            results = results.Where(e => e.TargetNodeId == targetNodeId);
        }

        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(results.Take(limit).ToList());
    }

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new MemoryGraphStatistics
        {
            TotalNodes = this.nodes.Count,
            TotalEdges = this.edges.Count,
            TotalExpertiseEntries = this.expertise.Count,
        });
    }

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        this.auditLog.Add(new { sessionId, operation, detail });
        return Task.CompletedTask;
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Returns ALL stored nodes (active only, no duplicates) for test inspection.</summary>
    public IReadOnlyDictionary<string, HypergraphNode> AllNodes => this.nodes;

    /// <summary>Returns ALL stored edges for test inspection.</summary>
    public IReadOnlyDictionary<string, Hyperedge> AllEdges => this.edges;

    /// <summary>Returns ALL expertise entries for test inspection.</summary>
    public IReadOnlyDictionary<string, ExpertiseEntry> AllExpertise => this.expertise;

    /// <summary>Returns historical (retired) node versions for temporal query inspection.</summary>
    public IReadOnlyList<HypergraphNode> NodeHistory => this.nodeHistory;

    private static bool IsRetiredNode(HypergraphNode node) =>
        node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var v) && v is not null;

    private static bool IsRetired(Hyperedge edge) =>
        edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var v) && v is not null;
}