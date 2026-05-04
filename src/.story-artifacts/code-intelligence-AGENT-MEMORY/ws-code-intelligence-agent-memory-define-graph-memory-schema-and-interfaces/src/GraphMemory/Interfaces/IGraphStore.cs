using GraphMemory.Models;

namespace GraphMemory.Interfaces;

/// <summary>Diff operation for batch graph updates.</summary>
public sealed class GraphDiff
{
    public List<GraphNode> AddNodes { get; init; } = [];
    public List<GraphNode> UpdateNodes { get; init; } = [];
    public List<string> RemoveNodeIds { get; init; } = [];
    public List<GraphEdge> AddEdges { get; init; } = [];
    public List<GraphEdge> UpdateEdges { get; init; } = [];
    public List<string> RemoveEdgeIds { get; init; } = [];
}

/// <summary>Result of applying a diff.</summary>
public sealed class DiffResult
{
    public int NodesAdded { get; init; }
    public int NodesUpdated { get; init; }
    public int NodesRemoved { get; init; }
    public int EdgesAdded { get; init; }
    public int EdgesUpdated { get; init; }
    public int EdgesRemoved { get; init; }
}

/// <summary>Options for graph traversal.</summary>
public sealed class TraversalOptions
{
    public required string StartNodeId { get; init; }
    public EdgeKind? EdgeKindFilter { get; init; }
    public int MaxDepth { get; init; } = 3;
    public TraversalDirection Direction { get; init; } = TraversalDirection.Outgoing;
}

public enum TraversalDirection { Outgoing, Incoming, Both }

/// <summary>A path found during graph traversal.</summary>
public sealed class TraversalPath
{
    public required List<GraphNode> Nodes { get; init; }
    public required List<GraphEdge> Edges { get; init; }
}

/// <summary>Options for vector similarity search.</summary>
public sealed class VectorSearchOptions
{
    public required double[] QueryEmbedding { get; init; }
    public int TopK { get; init; } = 10;
    public NodeKind? KindFilter { get; init; }
    public double MinSimilarity { get; init; } = 0.0;
}

/// <summary>Result of a vector search hit.</summary>
public sealed class VectorSearchResult
{
    public required GraphNode Node { get; init; }
    public required double Similarity { get; init; }
}

/// <summary>Snapshot metadata.</summary>
public sealed class GraphSnapshot
{
    public required string SnapshotId { get; init; }
    public required string CreatedAt { get; init; }
    public string? CommitSha { get; init; }
    public string? Description { get; init; }
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
}

/// <summary>
/// Persistence interface for the code knowledge graph.
/// Downstream implementations target Neo4j.
/// </summary>
public interface IGraphStore
{
    // --- Node operations ---
    Task AddNodeAsync(GraphNode node, CancellationToken ct = default);
    Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default);
    Task UpdateNodeAsync(GraphNode node, CancellationToken ct = default);
    Task RemoveNodeAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> GetNodesByKindAsync(NodeKind kind, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> FindByNameAsync(string name, NodeKind? kind = null, CancellationToken ct = default);
    Task<GraphNode?> GetNodeAtCommitAsync(string id, string commitSha, CancellationToken ct = default);

    // --- Edge operations ---
    Task AddEdgeAsync(GraphEdge edge, CancellationToken ct = default);
    Task<GraphEdge?> GetEdgeAsync(string id, CancellationToken ct = default);
    Task UpdateEdgeAsync(GraphEdge edge, CancellationToken ct = default);
    Task RemoveEdgeAsync(string id, CancellationToken ct = default);

    // --- Traversal ---
    Task<IReadOnlyList<GraphEdge>> GetOutgoingEdgesAsync(string nodeId, EdgeKind? kind = null, CancellationToken ct = default);
    Task<IReadOnlyList<GraphEdge>> GetIncomingEdgesAsync(string nodeId, EdgeKind? kind = null, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> GetNeighboursAsync(string nodeId, EdgeKind? edgeKind = null, CancellationToken ct = default);
    Task<IReadOnlyList<TraversalPath>> TraverseAsync(TraversalOptions options, CancellationToken ct = default);

    // --- Vector search ---
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(VectorSearchOptions options, CancellationToken ct = default);

    // --- Diff / batch ---
    Task<DiffResult> ApplyDiffAsync(GraphDiff diff, CancellationToken ct = default);

    // --- Snapshot ---
    Task<GraphSnapshot> SnapshotAsync(string? description = null, CancellationToken ct = default);
    Task RestoreSnapshotAsync(string snapshotId, CancellationToken ct = default);
    Task<IReadOnlyList<GraphSnapshot>> ListSnapshotsAsync(CancellationToken ct = default);

    // --- Bulk ---
    Task<CodeGraph> ExportAsync(CancellationToken ct = default);
    Task ImportAsync(CodeGraph graph, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
