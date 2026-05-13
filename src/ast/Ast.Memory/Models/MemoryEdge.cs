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
public sealed class MemoryEdge
{
    /// <summary>The underlying hyperedge.</summary>
    public Hyperedge Inner { get; }

    /// <summary>
    /// Temporal version range for this edge. Per architecture.md §4.1.
    /// Null for edges without version metadata (e.g. External boundary edges).
    /// </summary>
    public VersionRange? Version { get; }

    public MemoryEdge(Hyperedge inner, VersionRange version)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = version ?? throw new ArgumentNullException(nameof(version));
    }

    public MemoryEdge(Hyperedge inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = VersionRangeHelper.ExtractVersion(inner.Metadata);
    }

    /// <summary>Semantic relation type from metadata (e.g. "APPLIES_TO", "CALLS").</summary>
    public string? SemanticRelation => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.SemanticRelation);

    /// <summary>Memory layer from metadata.</summary>
    public string? MemoryLayer => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.MemoryLayer);
}