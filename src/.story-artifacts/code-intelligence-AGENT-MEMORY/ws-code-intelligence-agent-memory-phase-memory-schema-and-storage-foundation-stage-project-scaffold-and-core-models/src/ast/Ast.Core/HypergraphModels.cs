namespace Ast.Core;

/// <summary>
/// Enumerates the types of hyperedges in the code knowledge graph.
/// </summary>
public enum HyperedgeType
{
    Contains = 0,
    Calls = 1,
    Imports = 2,
    Inherits = 3,
    Expertise = 4,
    SimilarTo = 5,

    /// <summary>
    /// Memory-layer relation carrying a SemanticRelation metadata key.
    /// Added by the Ast.Memory workstream (architecture.md §4.6).
    /// </summary>
    MemoryRelation = 6
}

/// <summary>
/// A node in the code hypergraph, representing a code entity.
/// </summary>
public class HypergraphNode
{
    public string Id { get; }
    public string NodeType { get; }
    public Dictionary<string, string> Metadata { get; }

    public HypergraphNode(string id, string nodeType, Dictionary<string, string>? metadata = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        NodeType = nodeType ?? throw new ArgumentNullException(nameof(nodeType));
        Metadata = metadata ?? new Dictionary<string, string>();
    }
}

/// <summary>
/// A directed hyperedge connecting source nodes to target nodes.
/// </summary>
public class Hyperedge
{
    public string Id { get; }
    public HyperedgeType EdgeType { get; }
    public IReadOnlyList<string> SourceNodeIds { get; }
    public IReadOnlyList<string> TargetNodeIds { get; }
    public Dictionary<string, string> Metadata { get; }

    public Hyperedge(
        string id,
        HyperedgeType edgeType,
        IReadOnlyList<string> sourceNodeIds,
        IReadOnlyList<string> targetNodeIds,
        Dictionary<string, string>? metadata = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        EdgeType = edgeType;
        SourceNodeIds = sourceNodeIds ?? throw new ArgumentNullException(nameof(sourceNodeIds));
        TargetNodeIds = targetNodeIds ?? throw new ArgumentNullException(nameof(targetNodeIds));
        Metadata = metadata ?? new Dictionary<string, string>();
    }
}
