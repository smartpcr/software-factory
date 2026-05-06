// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> providing
/// strongly-typed accessors for memory-layer metadata.
/// Per architecture.md §4.1, §4.3 — uses composition, NOT inheritance.
/// </summary>
public class MemoryNode
{
    /// <summary>The underlying hypergraph node.</summary>
    public HypergraphNode Inner { get; }

    /// <summary>
    /// Temporal version range. Null when no temporal metadata is present,
    /// which is expected for External boundary nodes (timeless per architecture.md §4.3).
    /// </summary>
    public VersionRange? Version { get; }

    public MemoryNode(HypergraphNode inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = VersionRangeHelper.ExtractVersion(inner.Metadata);
    }

    /// <summary>Memory layer from metadata (e.g. "structural", "semantic").</summary>
    public string MemoryLayer => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.MemoryLayer);

    /// <summary>Node kind forwarded from the inner node's NodeType.</summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>Content hash from metadata.</summary>
    public string ContentHash => MetadataHelper.GetRequiredString(this.Inner.Metadata, MemoryMetadataKeys.ContentHash);
}