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
    /// <summary>The underlying hypergraph node.</summary>
    public HypergraphNode Inner { get; }

    public string MemoryLayer => (string)this.Inner.Metadata[MemoryMetadataKeys.MemoryLayer]!;

    public string NodeKind => this.Inner.NodeType;

    /// <summary>Content hash from metadata, or null if not present.</summary>
    public string? ContentHash => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.ContentHash);
}