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
    /// Temporal version range for this edge. Always present — every memory edge
    /// belongs to a versioned timeline (architecture.md §4.1).
    /// </summary>
    public VersionRange Version { get; }

    /// <summary>
    /// Creates a MemoryEdge from the underlying hyperedge and an explicit version range.
    /// </summary>
    public MemoryEdge(Hyperedge inner, VersionRange version)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Version = version ?? throw new ArgumentNullException(nameof(version));
    }

    /// <summary>Semantic relation type from metadata (e.g. "APPLIES_TO", "CALLS").</summary>
    public string SemanticRelation => Inner.Metadata[MemoryMetadataKeys.SemanticRelation];

    /// <summary>Memory layer from metadata.</summary>
    public string MemoryLayer => Inner.Metadata[MemoryMetadataKeys.MemoryLayer];
}