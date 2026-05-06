// -----------------------------------------------------------------------
// <copyright file="ExtractionResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Result of an extraction operation.
/// Per architecture.md §5.4 — canonical constructor signature.
/// </summary>
public record ExtractionResult(
    int NodesCreated,
    int EdgesCreated,
    int EmbeddingsGenerated,
    int FilesSkipped,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Extracted nodes — populated by ExtractFilesAsync for IncrementalSync
    /// to diff old vs new without a second store round-trip.
    /// </summary>
    public IReadOnlyList<HypergraphNode> Nodes { get; init; } = Array.Empty<HypergraphNode>();

    /// <summary>
    /// Extracted edges — populated by ExtractFilesAsync for IncrementalSync
    /// to diff old vs new edges.
    /// </summary>
    public IReadOnlyList<Hyperedge> Edges { get; init; } = Array.Empty<Hyperedge>();
}