// -----------------------------------------------------------------------
// <copyright file="MemoryEdge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around Hyperedge adding memory-specific accessors.
/// Uses composition, never inheritance. See architecture.md §4.1.
/// </summary>
public sealed class MemoryEdge
{
    public MemoryEdge(Hyperedge inner, VersionRange version)
    {
        this.Inner = inner;
        this.Version = version;
    }

    public Hyperedge Inner { get; }

    public string SemanticRelation => (string)this.Inner.Metadata[MemoryMetadataKeys.SemanticRelation]!;

    public string MemoryLayer => (string)this.Inner.Metadata[MemoryMetadataKeys.MemoryLayer]!;

    public VersionRange Version { get; }
}
