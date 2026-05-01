// -----------------------------------------------------------------------
// <copyright file="MemoryEdge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// A directed edge in the memory graph linking episodes, expertise records,
/// and code graph nodes. Separate from <see cref="Ast.Core.Hyperedge"/>
/// to avoid coupling memory relationships to code-analysis relationships.
/// </summary>
public class MemoryEdge
{
    /// <summary>
    /// Unique identifier for this edge.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Type of memory relationship.
    /// </summary>
    public required MemoryEdgeType EdgeType { get; init; }

    /// <summary>
    /// Source node ID (episode ID, expertise ID, or code node ID).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Target node ID (episode ID, expertise ID, or code node ID).
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Strength or weight of this relationship (0.0 to 1.0).
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// How the target was touched (only relevant for EpisodeTouchedFile/Symbol edges).
    /// </summary>
    public TouchType? TouchType { get; init; }

    /// <summary>
    /// When this edge was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional metadata for this edge.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}