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
public class MemoryNode
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

    /// <summary>Optional summary text for collapsed display.</summary>
    public string? Summary =>
        this.Inner.Metadata.TryGetValue("Summary", out var summary) && summary is string s
            ? s
            : null;

    /// <summary>Qualified name for display.</summary>
    public string? QualifiedName =>
        this.Inner.Metadata.TryGetValue("QualifiedName", out var qn) && qn is string s
            ? s
            : null;

    /// <summary>Parent node ID for hierarchy collapse (structural CONTAINS).</summary>
    public string? ParentId =>
        this.Inner.Metadata.TryGetValue("ParentId", out var pid) && pid is string s
            ? s
            : null;

    /// <summary>Member count for collapsed parent summaries.</summary>
    public int MemberCount =>
        this.Inner.Metadata.TryGetValue("MemberCount", out var mc) && mc is int i
            ? i
            : 0;

    /// <summary>Commit index when this node first appeared (§4.3 VersionRange).</summary>
    public int? SinceIndex =>
        this.Inner.Metadata.TryGetValue("SinceIndex", out var si) && si is int i
            ? i
            : null;

    /// <summary>Commit index when this node was retired (§4.3 VersionRange). Null = active.</summary>
    public int? UntilIndex =>
        this.Inner.Metadata.TryGetValue("UntilIndex", out var ui) && ui is int i
            ? i
            : null;

    /// <summary>Whether this node was collapsed from children by the BudgetController (HiAgent pattern).</summary>
    public bool IsCollapsed =>
        this.Inner.Metadata.TryGetValue("Collapsed", out var c) && c is true;

    /// <summary>
    /// Returns true when the line range is effectively empty (zero-span),
    /// typically for expertise or synthetic nodes.
    /// </summary>
    public bool IsZeroSpan => this.Inner.LineStart == 0 && this.Inner.LineEnd == 0;

    /// <summary>
    /// Returns true when this node's file path and line range structurally
    /// contain the <paramref name="other"/> node (canonical containment per §2.1).
    /// </summary>
    public bool StructurallyContains(MemoryNode other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.Inner.FilePath, other.Inner.FilePath, StringComparison.OrdinalIgnoreCase)
               && this.Inner.LineStart <= other.Inner.LineStart
               && this.Inner.LineEnd >= other.Inner.LineEnd
               && (this.Inner.LineStart < other.Inner.LineStart || this.Inner.LineEnd > other.Inner.LineEnd);
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