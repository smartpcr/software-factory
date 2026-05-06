// -----------------------------------------------------------------------
// <copyright file="InMemoryMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory implementation of <see cref="IMemoryGraphStore"/> with JSON file persistence.
/// Supports temporal retirement (soft-delete via UntilIndex) and audit logging.
/// </summary>
public sealed class InMemoryMemoryGraphStore : IMemoryGraphStore
{
    private static readonly string DefaultStorePath = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".ast-memory",
        "memory-graph.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, HypergraphNode> nodes = new();
    private readonly ConcurrentDictionary<string, Hyperedge> edges = new();
    private readonly ConcurrentDictionary<string, ExpertiseEntry> expertise = new();
    private readonly ConcurrentBag<AuditLogEntry> auditLog = new();
    private readonly string storePath;
    private readonly object persistLock = new();

    public InMemoryMemoryGraphStore()
        : this(null)
    {
    }

    public InMemoryMemoryGraphStore(string? storePath)
    {
        this.storePath = storePath ?? DefaultStorePath;
        this.LoadFromDisk();
    }

    /// <inheritdoc/>
    public string Name => "InMemory";

    // ── IMemoryGraphStore ─────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null,
        int limit = 100, CancellationToken ct = default)
    {
        IEnumerable<HypergraphNode> query = this.nodes.Values
            .Where(n => n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v)
                        && string.Equals(NormalizeMetadataString(v), memoryLayer, StringComparison.Ordinal))
            .Where(n => atCommitIndex != null ? IsVisibleAtCommit(n, atCommitIndex.Value) : !IsRetired(n));

        if (filter?.Language != null)
        {
            query = query.Where(n => n.Language == filter.Language);
        }

        if (filter?.NodeTypes != null)
        {
            var types = filter.NodeTypes.ToHashSet();
            query = query.Where(n => types.Contains(n.NodeType));
        }

        var result = query.Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<HypergraphNode>>(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null,
        int limit = 100, CancellationToken ct = default)
    {
        var result = this.edges.Values
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v)
                        && string.Equals(NormalizeMetadataString(v), semanticRelation, StringComparison.Ordinal))
            .Where(e => memoryLayer == null || (e.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml)
                        && string.Equals(NormalizeMetadataString(ml), memoryLayer, StringComparison.Ordinal)))
            .Where(e => atCommitIndex != null ? IsEdgeVisibleAtCommit(e, atCommitIndex.Value) : !IsRetiredEdge(e))
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    /// <inheritdoc/>
    public Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        node.Metadata[MemoryMetadataKeys.SinceCommit] = version.Since;
        node.Metadata[MemoryMetadataKeys.SinceIndex] = version.SinceIndex;
        // Clear retirement on upsert (node is alive again)
        node.Metadata.Remove(MemoryMetadataKeys.UntilCommit);
        node.Metadata.Remove(MemoryMetadataKeys.UntilIndex);
        this.nodes[node.Id] = node;
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default)
    {
        // Temporal retirement: set UntilIndex metadata instead of deleting
        if (this.nodes.TryGetValue(nodeId, out var node))
        {
            node.Metadata[MemoryMetadataKeys.UntilIndex] = untilCommitIndex;
            this.PersistToDisk();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
    {
        // Temporal retirement: set UntilIndex metadata instead of deleting
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            edge.Metadata[MemoryMetadataKeys.UntilIndex] = untilCommitIndex;
            this.PersistToDisk();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        if (this.edges.TryGetValue(edgeId, out var edge))
        {
            foreach (var (key, value) in updates)
            {
                edge.Metadata[key] = value;
            }

            this.PersistToDisk();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        var result = this.expertise.Values
            .Where(e => e.TargetNodeId == targetNodeId && e.Confidence >= minConfidence && !e.Archived)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    /// <inheritdoc/>
    public Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
    {
        this.expertise[entry.Id] = entry;
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
    {
        if (this.expertise.TryGetValue(expertiseId, out var entry))
        {
            entry.Archived = true;
            this.PersistToDisk();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        var result = this.expertise.Values
            .Where(e => e.Archived && (targetNodeId == null || e.TargetNodeId == targetNodeId))
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ExpertiseEntry>>(result);
    }

    /// <inheritdoc/>
    public Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        var activeNodes = this.nodes.Values.Where(n => !IsRetired(n)).ToList();
        var activeEdges = this.edges.Values.Where(e => !IsRetiredEdge(e)).ToList();
        var archived = this.expertise.Values.Count(e => e.Archived);
        var stats = new MemoryGraphStatistics
        {
            TotalNodes = activeNodes.Count,
            TotalEdges = activeEdges.Count,
            TotalExpertise = this.expertise.Count,
            ArchivedExpertise = archived,
            NodesByLayer = activeNodes
                .GroupBy(n => n.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v)
                    ? NormalizeMetadataString(v) ?? "unknown" : "unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            EdgesByLayer = activeEdges
                .GroupBy(e => e.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v)
                    ? NormalizeMetadataString(v) ?? "unknown" : "unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            ExpertiseByKind = this.expertise.Values
                .GroupBy(e => e.Kind.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
        };

        return Task.FromResult(stats);
    }

    /// <inheritdoc/>
    public Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        this.auditLog.Add(new AuditLogEntry
        {
            SessionId = sessionId,
            Operation = operation,
            Detail = JsonSerializer.Serialize(detail, JsonOptions),
            Timestamp = DateTimeOffset.UtcNow,
        });

        return Task.CompletedTask;
    }

    // ── IGraphStore ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default)
    {
        this.nodes[node.Id] = node;
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var node in nodes)
        {
            this.nodes[node.Id] = node;
            count++;
        }

        this.PersistToDisk();
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default)
    {
        this.edges[edge.Id] = edge;
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var edge in edges)
        {
            this.edges[edge.Id] = edge;
            count++;
        }

        this.PersistToDisk();
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(
        NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        IEnumerable<HypergraphNode> query = this.nodes.Values.Where(n => !IsRetired(n));
        if (filter?.Language != null)
        {
            query = query.Where(n => n.Language == filter.Language);
        }

        if (filter?.NodeTypes != null)
        {
            var types = filter.NodeTypes.ToHashSet();
            query = query.Where(n => types.Contains(n.NodeType));
        }

        return Task.FromResult<IReadOnlyList<HypergraphNode>>(query.Take(limit).ToList());
    }

    /// <inheritdoc/>
    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
    {
        this.edges.TryGetValue(edgeId, out var edge);
        return Task.FromResult(edge);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default)
    {
        var typeSet = edgeTypes?.ToHashSet();
        var result = this.edges.Values
            .Where(e => e.NodeIds.Contains(nodeId) && !IsRetiredEdge(e))
            .Where(e => typeSet == null || typeSet.Contains(e.EdgeType))
            .ToList();
        return Task.FromResult<IReadOnlyList<Hyperedge>>(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default)
    {
        var paths = new List<GraphPath>();
        var visited = new HashSet<string>();
        var currentPath = new List<string> { startNodeId };

        this.DfsFindPaths(startNodeId, endNodeId, maxDepth, visited, currentPath, paths);

        return Task.FromResult<IReadOnlyList<GraphPath>>(paths);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        var callerEdgeTypes = new HashSet<HyperedgeType>
        {
            HyperedgeType.MethodInvocation,
            HyperedgeType.ConstructorInvocation,
            HyperedgeType.CmdletCall,
            HyperedgeType.ModuleFunctionCall,
        };

        var results = new List<NodeWithDepth>();
        var visited = new HashSet<string> { nodeId };

        this.TraverseCallers(nodeId, depth, 1, callerEdgeTypes, visited, results);

        return Task.FromResult<IReadOnlyList<NodeWithDepth>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        var calleeEdgeTypes = new HashSet<HyperedgeType>
        {
            HyperedgeType.MethodInvocation,
            HyperedgeType.ConstructorInvocation,
            HyperedgeType.CmdletCall,
            HyperedgeType.ModuleFunctionCall,
        };

        var results = new List<NodeWithDepth>();
        var visited = new HashSet<string> { nodeId };

        this.TraverseCallees(nodeId, depth, 1, calleeEdgeTypes, visited, results);

        return Task.FromResult<IReadOnlyList<NodeWithDepth>>(results);
    }

    /// <inheritdoc/>
    public Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default)
    {
        this.nodes.TryGetValue(nodeId, out var node);
        var callers = this.GetCallersAsync(nodeId, maxDepth).GetAwaiter().GetResult();
        return Task.FromResult(new ImpactAnalysis
        {
            SourceNode = node ?? new HypergraphNode
            {
                Id = nodeId,
                Name = nodeId,
                NodeType = "unknown",
                Language = "unknown",
                FilePath = string.Empty,
            },
            DirectlyAffected = callers.Where(c => c.Depth == 1).ToList(),
            IndirectlyAffected = callers.Where(c => c.Depth > 1).ToList(),
        });
    }

    /// <inheritdoc/>
    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        this.nodes.TryRemove(nodeId, out _);
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.nodes.Clear();
        this.edges.Clear();
        this.expertise.Clear();
        this.PersistToDisk();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GraphStatistics
        {
            TotalNodes = this.nodes.Values.Count(n => !IsRetired(n)),
            TotalEdges = this.edges.Values.Count(e => !IsRetiredEdge(e)),
        });
    }

    /// <inheritdoc/>
    public Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    // ── Graph traversal helpers ──────────────────────────────────────

    private void DfsFindPaths(
        string current, string target, int maxDepth,
        HashSet<string> visited, List<string> currentPath, List<GraphPath> results)
    {
        if (current == target && currentPath.Count > 1)
        {
            var path = new GraphPath();
            foreach (var nid in currentPath)
            {
                if (this.nodes.TryGetValue(nid, out var n))
                {
                    path.Nodes.Add(n);
                }
            }

            results.Add(path);
            return;
        }

        if (maxDepth <= 0 || results.Count >= 10)
        {
            return;
        }

        var outEdges = this.edges.Values
            .Where(e => e.NodeIds.Contains(current) && !IsRetiredEdge(e))
            .ToList();

        foreach (var edge in outEdges)
        {
            foreach (var neighbor in edge.NodeIds.Where(id => id != current && !visited.Contains(id)))
            {
                visited.Add(neighbor);
                currentPath.Add(neighbor);
                this.DfsFindPaths(neighbor, target, maxDepth - 1, visited, currentPath, results);
                currentPath.RemoveAt(currentPath.Count - 1);
                visited.Remove(neighbor);
            }
        }
    }

    private void TraverseCallers(
        string nodeId, int maxDepth, int currentDepth,
        HashSet<HyperedgeType> edgeTypes, HashSet<string> visited,
        List<NodeWithDepth> results)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        // Callers: find edges where nodeId is the *target* (second node in call edges)
        var callerEdges = this.edges.Values
            .Where(e => edgeTypes.Contains(e.EdgeType) && !IsRetiredEdge(e)
                && e.NodeIds.Count >= 2 && e.NodeIds.Last() == nodeId)
            .ToList();

        foreach (var edge in callerEdges)
        {
            var callerId = edge.NodeIds.First();
            if (!visited.Add(callerId))
            {
                continue;
            }

            if (this.nodes.TryGetValue(callerId, out var callerNode) && !IsRetired(callerNode))
            {
                results.Add(new NodeWithDepth { Node = callerNode, Depth = currentDepth });
                this.TraverseCallers(callerId, maxDepth, currentDepth + 1, edgeTypes, visited, results);
            }
        }
    }

    private void TraverseCallees(
        string nodeId, int maxDepth, int currentDepth,
        HashSet<HyperedgeType> edgeTypes, HashSet<string> visited,
        List<NodeWithDepth> results)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        // Callees: find edges where nodeId is the *source* (first node in call edges)
        var calleeEdges = this.edges.Values
            .Where(e => edgeTypes.Contains(e.EdgeType) && !IsRetiredEdge(e)
                && e.NodeIds.Count >= 2 && e.NodeIds.First() == nodeId)
            .ToList();

        foreach (var edge in calleeEdges)
        {
            var calleeId = edge.NodeIds.Last();
            if (!visited.Add(calleeId))
            {
                continue;
            }

            if (this.nodes.TryGetValue(calleeId, out var calleeNode) && !IsRetired(calleeNode))
            {
                results.Add(new NodeWithDepth { Node = calleeNode, Depth = currentDepth });
                this.TraverseCallees(calleeId, maxDepth, currentDepth + 1, edgeTypes, visited, results);
            }
        }
    }

    // ── Temporal helpers ─────────────────────────────────────────────

    /// <summary>
    /// Normalizes metadata values that may be <see cref="JsonElement"/> after JSON
    /// round-trip deserialization back to their CLR equivalents (string, int, etc.).
    /// </summary>
    private static string? NormalizeMetadataString(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
        }

        return value?.ToString();
    }

    private static int? NormalizeMetadataInt(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : null;
        }

        if (value is int i)
        {
            return i;
        }

        if (value != null && int.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsRetired(HypergraphNode node)
    {
        return node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var v) && v != null
            && NormalizeMetadataInt(v) != null;
    }

    private static bool IsRetiredEdge(Hyperedge edge)
    {
        return edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var v) && v != null
            && NormalizeMetadataInt(v) != null;
    }

    private static bool IsVisibleAtCommit(HypergraphNode node, int commitIndex)
    {
        if (!node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceVal) || sinceVal == null)
        {
            return true;
        }

        var since = NormalizeMetadataInt(sinceVal);
        if (since == null)
        {
            return true;
        }

        if (since.Value > commitIndex)
        {
            return false;
        }

        if (node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilVal) && untilVal != null)
        {
            var until = NormalizeMetadataInt(untilVal);
            if (until != null)
            {
                return commitIndex < until.Value;
            }
        }

        return true;
    }

    private static bool IsEdgeVisibleAtCommit(Hyperedge edge, int commitIndex)
    {
        if (edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilVal) && untilVal != null)
        {
            var until = NormalizeMetadataInt(untilVal);
            if (until != null && commitIndex >= until.Value)
            {
                return false;
            }
        }

        return true;
    }

    // ── Persistence ───────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        if (!File.Exists(this.storePath))
        {
            return;
        }

        var json = File.ReadAllText(this.storePath);
        var snapshot = JsonSerializer.Deserialize<StoreSnapshot>(json, JsonOptions);
        if (snapshot == null)
        {
            return;
        }

        foreach (var node in snapshot.Nodes ?? Array.Empty<HypergraphNode>())
        {
            this.nodes[node.Id] = node;
        }

        foreach (var edge in snapshot.Edges ?? Array.Empty<Hyperedge>())
        {
            this.edges[edge.Id] = edge;
        }

        foreach (var entry in snapshot.Expertise ?? Array.Empty<ExpertiseEntry>())
        {
            this.expertise[entry.Id] = entry;
        }
    }

    private void PersistToDisk()
    {
        lock (this.persistLock)
        {
            var dir = Path.GetDirectoryName(this.storePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var snapshot = new StoreSnapshot
            {
                Nodes = this.nodes.Values.ToArray(),
                Edges = this.edges.Values.ToArray(),
                Expertise = this.expertise.Values.ToArray(),
            };

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(this.storePath, json);
        }
    }

    /// <summary>
    /// Serializable snapshot of the store for JSON persistence.
    /// </summary>
    private sealed class StoreSnapshot
    {
        public HypergraphNode[]? Nodes { get; set; }

        public Hyperedge[]? Edges { get; set; }

        public ExpertiseEntry[]? Expertise { get; set; }
    }

    /// <summary>
    /// Audit log entry for tracking all graph mutations.
    /// </summary>
    private sealed class AuditLogEntry
    {
        public string SessionId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string Detail { get; set; } = string.Empty;

        public DateTimeOffset Timestamp { get; set; }
    }
}