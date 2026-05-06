// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> that provides
/// strongly-typed accessors for memory-layer metadata and temporal versioning.
/// Uses composition (not inheritance) per architecture.md §4.1.
/// </summary>
public class MemoryNode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryNode"/> class.
    /// </summary>
    /// <param name="inner">The underlying hypergraph node.</param>
    public MemoryNode(HypergraphNode inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// The underlying hypergraph node.
    /// </summary>
    public HypergraphNode Inner { get; }

    /// <summary>
    /// The memory layer this node belongs to (structural, semantic, expertise, external).
    /// </summary>
    public string? MemoryLayer => this.GetMetadataString(MemoryMetadataKeys.MemoryLayer);

    /// <summary>
    /// The kind of node (class, method, file, namespace, etc.).
    /// Delegates to <see cref="HypergraphNode.NodeType"/>.
    /// </summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>
    /// SHA-256 content hash for deduplication and change detection.
    /// </summary>
    public string? ContentHash => this.GetMetadataString(MemoryMetadataKeys.ContentHash);

    /// <summary>
    /// Temporal version range. Null for External boundary nodes which are timeless.
    /// </summary>
    public VersionRange? Version
    {
        get
        {
            var since = this.GetMetadataString(MemoryMetadataKeys.SinceCommit);
            var sinceIdx = this.GetMetadataInt(MemoryMetadataKeys.SinceIndex);
            if (since == null || sinceIdx == null)
            {
                return null;
            }

            return new VersionRange(
                since,
                sinceIdx.Value,
                this.GetMetadataString(MemoryMetadataKeys.UntilCommit),
                this.GetMetadataInt(MemoryMetadataKeys.UntilIndex));
        }
    }

    private string? GetMetadataString(string key)
    {
        return this.Inner.Metadata.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private int? GetMetadataInt(string key)
    {
        if (!this.Inner.Metadata.TryGetValue(key, out var val))
        {
            return null;
        }

        return val switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}