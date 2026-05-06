// -----------------------------------------------------------------------
// <copyright file="MemoryEdge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="Hyperedge"/> with memory-layer accessors.
/// </summary>
public class MemoryEdge
{
    public MemoryEdge(Hyperedge inner, VersionRange version)
    {
        this.Inner = inner;
        this.Version = version;
    }

    public Hyperedge Inner { get; }

    public VersionRange Version { get; }

    public string SemanticRelation =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
}