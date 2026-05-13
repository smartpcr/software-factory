// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> adding memory-layer accessors.
/// </summary>
public sealed class MemoryNode
{
    public MemoryNode(HypergraphNode inner, VersionRange? version)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = version ?? ExtractVersionFromMetadata(inner);
    }

    /// <summary>The underlying hypergraph node.</summary>
    public HypergraphNode Inner { get; }

    /// <summary>Memory layer tag.</summary>
    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v) && v is string s
            ? s
            : string.Empty;

    /// <summary>Node type (Class, Method, etc.).</summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>Content hash from metadata, or null if not present.</summary>
    public string? ContentHash => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.ContentHash);
}