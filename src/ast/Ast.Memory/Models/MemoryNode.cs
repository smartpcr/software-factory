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
    public MemoryNode(HypergraphNode inner, VersionRange? version)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = version ?? TryExtractVersionRange(inner);
    }

    /// <summary>
    /// The underlying hypergraph node.
    /// </summary>
    public HypergraphNode Inner { get; }

    /// <summary>
    /// The memory layer this node belongs to (e.g., "structural", "call_chain").
    /// </summary>
    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v) && v is string s
            ? s
            : MemoryLayers.Structural;

    /// <summary>
    /// The node kind (delegates to NodeType).
    /// </summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>
    /// Content hash for change detection.
    /// </summary>
    public string? ContentHash =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? (string?)v : null;

    /// <summary>
    /// Temporal version range. Null for External boundary nodes (timeless stubs).
    /// </summary>
    public VersionRange? Version { get; }

    /// <summary>
    /// Checks whether this node is active at the given commit index.
    /// External nodes (null version) are always active.
    /// </summary>
    public bool IsActiveAt(int commitIndex) =>
        this.Version?.IsActiveAt(commitIndex) ?? true;

    private static VersionRange? TryExtractVersionRange(HypergraphNode node)
    {
        if (!node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceCommitObj)
            || sinceCommitObj is not string sinceCommit)
        {
            return null;
        }

        var sinceIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si)
            ? Convert.ToInt32(si)
            : 0;

        var untilCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var uc)
            ? uc as string
            : null;

        int? untilIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) && ui != null
            ? Convert.ToInt32(ui)
            : null;

        return new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex);
    }
}

        node.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var untilObj);
        node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilIdxObj);
        var until = untilObj as string;
        int? untilIndex = untilIdxObj is int ui ? ui : null;

        return new VersionRange(since, sinceIndex, until, untilIndex);
    }
}