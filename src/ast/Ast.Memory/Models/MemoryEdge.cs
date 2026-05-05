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
/// Accepts both code-derived edges (original HyperedgeType) and
/// memory-only edges (HyperedgeType.MemoryRelation).
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
        this.Version = version ?? throw new ArgumentNullException(nameof(version));
    }

    /// <summary>
    /// Creates a MemoryEdge by wrapping a Hyperedge and extracting VersionRange
    /// from its Metadata dictionary.
    /// </summary>
    public MemoryEdge(Hyperedge inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = VersionRangeHelper.ExtractVersion(inner.Metadata)
            ?? new VersionRange(null, null, null, null);
    }

    /// <summary>Semantic relation type from metadata (e.g. "CALLS", "EXTENDS").</summary>
    public string SemanticRelation => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.SemanticRelation);

    /// <summary>Memory layer from metadata.</summary>
    public string MemoryLayer => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.MemoryLayer);
}