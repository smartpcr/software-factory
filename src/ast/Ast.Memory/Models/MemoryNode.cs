// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> with memory-layer accessors.
/// </summary>
public class MemoryNode
{
    public MemoryNode(HypergraphNode inner, VersionRange? version = null)
    {
        this.Inner = inner;
        this.Version = version;
    }

    public HypergraphNode Inner { get; }

    public VersionRange? Version { get; }

    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    public string NodeKind => this.Inner.NodeType;

    public string ContentHash =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
}