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
    /// Temporal version range. Null for External boundary nodes which are timeless.
    /// Per architecture.md §4.3.
    /// </summary>
    public VersionRange? Version { get; }

    public MemoryNode(HypergraphNode inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Version = ExtractVersion(inner.Metadata);
    }

    /// <summary>Memory layer from metadata (e.g. "structural", "semantic").</summary>
    public string MemoryLayer => Inner.Metadata[MemoryMetadataKeys.MemoryLayer];

    /// <summary>Node kind forwarded from the inner node's NodeType.</summary>
    public string NodeKind => Inner.NodeType;

    /// <summary>Content hash from metadata.</summary>
    public string ContentHash => Inner.Metadata[MemoryMetadataKeys.ContentHash];

    private static VersionRange? ExtractVersion(Dictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var since))
            return null;

        if (!metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var sinceIndexStr)
            || !int.TryParse(sinceIndexStr, out var sinceIndex))
            return null;

        string? until = metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var u) ? u : null;
        int? untilIndex = metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui)
                          && int.TryParse(ui, out var parsed)
            ? parsed
            : null;

        return new VersionRange(since, sinceIndex, until, untilIndex);
    }
}