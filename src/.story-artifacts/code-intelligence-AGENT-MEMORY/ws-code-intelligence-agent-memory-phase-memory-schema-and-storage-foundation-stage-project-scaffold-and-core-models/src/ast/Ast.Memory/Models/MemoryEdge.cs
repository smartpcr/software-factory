// -----------------------------------------------------------------------
// <copyright file="MemoryEdge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="Hyperedge"/> providing
/// strongly-typed accessors for memory-layer edge metadata.
/// Per architecture.md §4.1 — uses composition, NOT inheritance.
/// </summary>
public class MemoryEdge
{
    /// <summary>The underlying hyperedge.</summary>
    public Hyperedge Inner { get; }

    /// <summary>
    /// Temporal version range for this edge. Per architecture.md §4.1.
    /// </summary>
    public VersionRange Version { get; }

    /// <summary>
    /// Creates a MemoryEdge from the underlying hyperedge and an explicit version range.
    /// </summary>
    public MemoryEdge(Hyperedge inner, VersionRange version)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ValidateEdgeType(inner);
        this.Version = version ?? throw new ArgumentNullException(nameof(version));
    }

    /// <summary>
    /// Creates a MemoryEdge by wrapping a Hyperedge and extracting VersionRange
    /// from its Metadata dictionary (SinceCommit, SinceIndex, UntilCommit, UntilIndex).
    /// When temporal metadata is absent, Version defaults to an empty range.
    /// Throws <see cref="InvalidOperationException"/> when temporal metadata is malformed.
    /// Throws <see cref="ArgumentException"/> if the edge type is not <see cref="HyperedgeType.MemoryRelation"/>.
    /// </summary>
    public MemoryEdge(Hyperedge inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ValidateEdgeType(inner);
        this.Version = VersionRangeHelper.ExtractVersion(inner.Metadata)
            ?? new VersionRange("", 0, null, null);
    }

    /// <summary>Semantic relation type from metadata (e.g. "APPLIES_TO", "CALLS").</summary>
    public string SemanticRelation => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.SemanticRelation);

    /// <summary>Memory layer from metadata.</summary>
    public string MemoryLayer => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.MemoryLayer);

    private static void ValidateEdgeType(Hyperedge inner)
    {
        if (inner.EdgeType != HyperedgeType.MemoryRelation)
        {
            throw new ArgumentException(
                $"MemoryEdge requires EdgeType={HyperedgeType.MemoryRelation}, " +
                $"but got {inner.EdgeType}.",
                nameof(inner));
        }
    }
}