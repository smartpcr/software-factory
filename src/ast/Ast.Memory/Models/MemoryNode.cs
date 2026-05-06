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
    public MemoryNode(HypergraphNode inner, VersionRange? version = null)
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

    /// <summary>Content hash for change detection.</summary>
    public string? ContentHash =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? v as string : null;

    /// <summary>Temporal version range. Null for External boundary nodes.</summary>
    public VersionRange? Version { get; }

    private static VersionRange? ExtractVersionFromMetadata(HypergraphNode node)
    {
        var hasSince = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceObj);
        var hasSinceIdx = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceIdxObj);

        if (!hasSince && !hasSinceIdx)
        {
            return null;
        }

        var since = sinceObj as string;
        int? sinceIndex = sinceIdxObj is int si ? si : null;

        node.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var untilObj);
        node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilIdxObj);
        var until = untilObj as string;
        int? untilIndex = untilIdxObj is int ui ? ui : null;

        return new VersionRange(since, sinceIndex, until, untilIndex);
    }
}