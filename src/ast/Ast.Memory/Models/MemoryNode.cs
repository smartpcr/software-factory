// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> adding
/// memory-specific accessors. Does not subclass — avoids coupling
/// agent memory semantics to the code-analysis type hierarchy.
/// Per architecture.md §4.1.
/// </summary>
public sealed class MemoryNode
{
    /// <summary>The underlying hypergraph node.</summary>
    public HypergraphNode Inner { get; }

    /// <summary>Home layer from Metadata["MemoryLayer"].</summary>
    public string MemoryLayer => (string)(this.Inner.Metadata["MemoryLayer"] ?? "structural");

    /// <summary>Delegates to Inner.NodeType.</summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>Content hash from metadata, or null if not present.</summary>
    public string? ContentHash => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.ContentHash);
}