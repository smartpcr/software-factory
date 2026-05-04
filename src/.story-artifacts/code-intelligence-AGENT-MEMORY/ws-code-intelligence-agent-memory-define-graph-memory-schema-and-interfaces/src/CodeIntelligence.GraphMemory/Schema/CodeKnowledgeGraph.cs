namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Root container for a code knowledge graph snapshot.
/// </summary>
public sealed record CodeKnowledgeGraph
{
    /// <summary>Schema version of this graph document.</summary>
    public required SchemaVersion SchemaVersion { get; init; }

    /// <summary>All nodes in the graph.</summary>
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];

    /// <summary>All directed edges in the graph.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    /// <summary>
    /// Graph-level metadata: extraction tool, repo URL, branch, commit SHA,
    /// snapshot timestamp, language set, or any producer-specific info.
    /// </summary>
    public PropertyBag Metadata { get; init; } = new();
}
