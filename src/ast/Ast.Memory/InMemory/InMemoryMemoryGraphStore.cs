// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.InMemory;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory implementation of <see cref="IMemoryGraphStore"/> for testing and lightweight use.
/// Stores nodes, edges, and expertise entries in concurrent dictionaries.
/// </summary>
public sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertise = new();
    private readonly ConcurrentDictionary<string, List<string>> auditLog = new();

    public string Name => "InMemory";

    // --- IGraphStore base methods ---

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
        NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        IEnumerable<HypergraphNode> result = this.nodes.Values;
        if (filter != null)
        {
            if (filter.Language != null)
            {
                result = result.Where(n => n.Language == filter.Language);
            }

            if (filter.FilePath != null)
            {
                result = result.Where(n => n.FilePath.Contains(filter.FilePath, StringComparison.OrdinalIgnoreCase));
            }

            if (filter.NamePattern != null)
            {
                result = result.Where(n => n.Name.Contains(filter.NamePattern, StringComparison.OrdinalIgnoreCase));
            }
        }

        return Task.FromResult<IReadOnlyList<HypergraphNode>>(result.Take(limit).ToList());
    }

    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
    {
        this.edges.TryGetValue(edgeId, out var edge);
        return Task.FromResult(edge);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default)
    {
        var types = edgeTypes?.ToHashSet();
        var result = this.edges.Values
            .Where(e => e.NodeIds.Contains(nodeId))
            .Where(e => types == null || types.Contains(e.EdgeType))
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default)
    {
        var paths = new List<GraphPath>();
        var queue = new Queue<(string nodeId, List<string> visitedIds, List<Hyperedge> pathEdges)>();
        queue.Enqueue((startNodeId, new List<string> { startNodeId }, new List<Hyperedge>()));

        while (queue.Count > 0 && paths.Count < 10)
        {
            var (currentId, visitedIds, pathEdges) = queue.Dequeue();
            if (pathEdges.Count > maxDepth)
            {
                continue;
            }

            if (currentId == endNodeId && pathEdges.Count > 0)
            {
                var pathNodes = visitedIds
                    .Where(id => this.nodes.ContainsKey(id))
                    .Select(id => this.nodes[id])
                    .ToList();
                paths.Add(new GraphPath { Nodes = pathNodes, Edges = new List<Hyperedge>(pathEdges) });
                continue;
            }

            foreach (var edge in this.edges.Values.Where(e => e.NodeIds.Contains(currentId)))
            {
                foreach (var neighborId in edge.NodeIds.Where(id => id != currentId && !visitedIds.Contains(id)))
                {
                    var newVisited = new List<string>(visitedIds) { neighborId };
                    var newEdges = new List<Hyperedge>(pathEdges) { edge };
                    queue.Enqueue((neighborId, newVisited, newEdges));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<GraphPath>>(paths);
    }

    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        var result = new List<NodeWithDepth>();
        var visited = new HashSet<string> { nodeId };
        var frontier = new List<string> { nodeId };

        for (var d = 1; d <= depth && frontier.Count > 0; d++)
        {
            var nextFrontier = new List<string>();
            foreach (var currentId in frontier)
            {
                var callerEdges = this.edges.Values
                    .Where(e => e.NodeIds.Contains(currentId)
                        && (e.EdgeType == HyperedgeType.MethodInvocation || e.EdgeType == HyperedgeType.ConstructorInvocation)
                        && e.Roles.TryGetValue(currentId, out var role) && role == "callee")
                    .ToList();

                foreach (var edge in callerEdges)
                {
                    foreach (var otherId in edge.NodeIds.Where(id => id != currentId && !visited.Contains(id)))
                    {
                        visited.Add(otherId);
                        if (this.nodes.TryGetValue(otherId, out var callerNode))
                        {
                            result.Add(new NodeWithDepth { Node = callerNode, Depth = d });
                            nextFrontier.Add(otherId);
                        }
                    }
                }
            }

            frontier = nextFrontier;
        }

        return Task.FromResult<IReadOnlyList<NodeWithDepth>>(result);
    }

    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        var result = new List<NodeWithDepth>();
        var visited = new HashSet<string> { nodeId };
        var frontier = new List<string> { nodeId };

        for (var d = 1; d <= depth && frontier.Count > 0; d++)
        {
            var nextFrontier = new List<string>();
            foreach (var currentId in frontier)
            {
                var calleeEdges = this.edges.Values
                    .Where(e => e.NodeIds.Contains(currentId)
                        && (e.EdgeType == HyperedgeType.MethodInvocation || e.EdgeType == HyperedgeType.ConstructorInvocation)
                        && e.Roles.TryGetValue(currentId, out var role) && role == "caller")
                    .ToList();

                foreach (var edge in calleeEdges)
                {
                    foreach (var otherId in edge.NodeIds.Where(id => id != currentId && !visited.Contains(id)))
                    {
                        visited.Add(otherId);
                        if (this.nodes.TryGetValue(otherId, out var calleeNode))
                        {
                            result.Add(new NodeWithDepth { Node = calleeNode, Depth = d });
                            nextFrontier.Add(otherId);
                        }
                    }
                }
            }

            frontier = nextFrontier;
        }

        return Task.FromResult<IReadOnlyList<NodeWithDepth>>(result);
    }

    public Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default)
    {
        this.nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(new ImpactAnalysis
        {
            SourceNode = node ?? new HypergraphNode { Id = nodeId, Name = nodeId, NodeType = "Unknown", Language = "Unknown", FilePath = "" }
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
        this.expertise.Clear();
        return Task.CompletedTask;
    }

    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GraphStatistics
        {
            TotalNodes = this.nodes.Count,
            TotalEdges = this.edges.Count
        });
    }

    public Task CreateIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    // --- IMemoryGraphStore extension methods ---

    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex = null, int limit = 100, CancellationToken ct = default)
    {
        var result = this.nodes.Values
            .Where(n => n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var layer)
                && (string?)layer == memoryLayer)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<HypergraphNode>>(result);
    }

    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null,
        int limit = 100, CancellationToken ct = default)
    {
        var result = this.edges.Values
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel)
                && (string?)rel == semanticRelation)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        this.nodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            foreach (var kvp in updates)
            {
                edge.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        var result = this.expertise.Values
            .Where(e => e.TargetNodeId == targetNodeId && !e.Archived && e.Confidence >= minConfidence)
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
        if (this.expertise.TryGetValue(expertiseId, out var entry))
        {
            entry.Archived = true;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        var result = this.expertise.Values
            .Where(e => e.Archived && (targetNodeId == null || e.TargetNodeId == targetNodeId))
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        var nodesByLayer = this.nodes.Values
            .Where(n => n.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
            .GroupBy(n => (string)n.Metadata[MemoryMetadataKeys.MemoryLayer]!)
            .ToDictionary(g => g.Key, g => g.Count());

        var edgesByLayer = this.edges.Values
            .Where(e => e.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
            .GroupBy(e => (string)e.Metadata[MemoryMetadataKeys.MemoryLayer]!)
            .ToDictionary(g => g.Key, g => g.Count());

        var nodesByType = this.nodes.Values
            .GroupBy(n => n.NodeType)
            .ToDictionary(g => g.Key, g => g.Count());

        return Task.FromResult(new MemoryGraphStatistics
        {
            TotalNodes = this.nodes.Count,
            TotalEdges = this.edges.Count,
            TotalExpertise = this.expertise.Values.Count(e => !e.Archived),
            ArchivedExpertise = this.expertise.Values.Count(e => e.Archived),
            NodesByLayer = nodesByLayer,
            EdgesByLayer = edgesByLayer,
            NodesByType = nodesByType
        });
    }

    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        this.auditLog.GetOrAdd(sessionId, _ => new List<string>())
            .Add($"{operation}: {detail}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}
