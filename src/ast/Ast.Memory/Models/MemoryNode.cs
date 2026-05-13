// -----------------------------------------------------------------------
// <copyright file="MemoryNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="HypergraphNode"/> that adds memory-layer
/// semantics without inheriting from the code-analysis type.
/// Per architecture.md §4.1 — composition, not inheritance.
/// </summary>
public sealed class MemoryNode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryNode"/> class.
    /// </summary>
    public MemoryNode(HypergraphNode inner)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The underlying code graph node.</summary>
    public HypergraphNode Inner { get; }

    /// <summary>
    /// Memory layer this node belongs to. Canonical values per architecture.md §2.1:
    /// <c>structural</c>, <c>call_chain</c>, <c>semantic</c>, <c>expertise</c>.
    /// </summary>
    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue("MemoryLayer", out var layer) && layer is string s
            ? s
            : "structural";

    /// <summary>Content hash for change detection (§4.3).</summary>
    public string? ContentHash =>
        this.Inner.Metadata.TryGetValue("ContentHash", out var hash) && hash is string s
            ? s
            : null;

    /// <summary>Node kind forwarded from the inner node.</summary>
    public string NodeKind => this.Inner.NodeType;

    /// <summary>Content hash from metadata, or null if not present.</summary>
    public string? ContentHash => MetadataHelper.GetOptionalString(this.Inner.Metadata, MemoryMetadataKeys.ContentHash);
}