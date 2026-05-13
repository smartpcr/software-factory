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
    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryNode"/> class.
    /// </summary>
    public MemoryNode(HypergraphNode inner)
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
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var v) ? v as string : null;

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

/// <summary>
/// A memory node with composite relevance scores, produced by the Ranker.
/// Per architecture.md §5.3 and §6.2.
/// </summary>
/// <param name="Node">The underlying memory node.</param>
/// <param name="CompositeScore">Weighted composite of all signals.</param>
/// <param name="StructuralScore">Structural (containment) relevance signal.</param>
/// <param name="SemanticScore">Embedding-similarity signal.</param>
/// <param name="CentralityScore">PageRank-light centrality signal.</param>
/// <param name="RecencyScore">Temporal recency signal.</param>
/// <param name="ExpertiseScore">Max APPLIES_TO confidence signal.</param>
/// <param name="RetrievalPath">
/// Which retrieval mode produced this node. Canonical values from architecture.md §3.3:
/// <c>StructuralDrill</c>, <c>CallTrace</c>, <c>SemanticSearch</c>, <c>ExpertiseRecall</c>.
/// When null, the Context Assembler infers the section from <see cref="MemoryNode.MemoryLayer"/>
/// and node type. Set by the Retrieval Engine.
/// </param>
public record ScoredMemoryNode(
    MemoryNode Node,
    double CompositeScore,
    double StructuralScore,
    double SemanticScore,
    double CentralityScore,
    double RecencyScore,
    double ExpertiseScore,
    string? RetrievalPath = null);