// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around HypergraphNode adding memory-specific accessors.
/// Uses composition, never inheritance. See architecture.md §4.1.
/// </summary>
public sealed class MemoryNode
{
    public MemoryNode(HypergraphNode inner, VersionRange? version)
    {
        this.Inner = inner;
        this.Version = version;
    }

    public HypergraphNode Inner { get; }

    public string MemoryLayer => (string)this.Inner.Metadata[MemoryMetadataKeys.MemoryLayer]!;

    public string NodeKind => this.Inner.NodeType;

    public string? ContentHash =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? (string?)v : null;

    /// <summary>
    /// Nullable — null for External boundary nodes (timeless stubs).
    /// </summary>
    public VersionRange? Version { get; }
}
