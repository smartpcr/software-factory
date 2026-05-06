// -----------------------------------------------------------------------
// <copyright file="ExtractionResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Result of an extraction pipeline run. See architecture.md §5.4.
/// Includes the extracted nodes and edges so that IncrementalSync can diff
/// them against the existing graph baseline and perform explicit upsert/retire.
/// </summary>
public record ExtractionResult(
    int NodesCreated,
    int EdgesCreated,
    int EmbeddingsGenerated,
    int FilesSkipped,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// The actual HypergraphNode objects produced by extraction.
    /// Used by IncrementalSync to diff against the current graph state.
    /// </summary>
    public IReadOnlyList<HypergraphNode> ExtractedNodes { get; init; } = Array.Empty<HypergraphNode>();

    /// <summary>
    /// The actual Hyperedge objects produced by extraction.
    /// Used by IncrementalSync to diff against the current graph state.
    /// </summary>
    public IReadOnlyList<Hyperedge> ExtractedEdges { get; init; } = Array.Empty<Hyperedge>();

    /// <summary>
    /// Paths of files that were skipped because the pipeline does not
    /// support their language/extension. IncrementalSync must NOT retire
    /// existing graph nodes for these files since extraction was not attempted.
    /// </summary>
    public IReadOnlyList<string> SkippedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Options for extraction. See architecture.md §5.4.
/// </summary>
public record ExtractionOptions(
    IReadOnlyList<string> Languages,
    string? ScopeSubtree = null,
    string? CommitSha = null,
    int? CommitIndex = null);
