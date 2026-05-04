namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// A directed edge in the code knowledge graph connecting two nodes.
/// All edges are directed from <see cref="SourceNodeId"/> to <see cref="TargetNodeId"/>.
/// Duplicate edges (same source, target, and kind) are permitted when properties differ.
/// </summary>
public sealed record GraphEdge
{
    /// <summary>Stable identifier for this edge.</summary>
    public required string Id { get; init; }

    /// <summary>ID of the source (origin) node.</summary>
    public required string SourceNodeId { get; init; }

    /// <summary>ID of the target (destination) node.</summary>
    public required string TargetNodeId { get; init; }

    /// <summary>The semantic relationship this edge represents.</summary>
    public required EdgeKind Kind { get; init; }

    /// <summary>
    /// Optional weight / confidence score in [0.0, 1.0].
    /// Primarily used for <see cref="EdgeKind.Expertise"/> edges.
    /// </summary>
    public double? Weight { get; init; }

    /// <summary>Extensible property bag for producer-specific metadata.</summary>
    public PropertyBag Properties { get; init; } = new();

    /// <summary>When this edge was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
