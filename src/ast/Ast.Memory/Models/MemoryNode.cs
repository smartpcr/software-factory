// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> adding memory-specific accessors.
/// </summary>
public sealed class MemoryNode
{
    public MemoryNode(HypergraphNode inner, VersionRange? version = null)
    {
        this.Inner = inner;
        this.Version = version;
    }

    public HypergraphNode Inner { get; }

    public string MemoryLayer => (string)this.Inner.Metadata[MemoryMetadataKeys.MemoryLayer]!;

    public string NodeKind => this.Inner.NodeType;

    public string? ContentHash =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? (string?)v : null;

    public VersionRange? Version { get; }
}