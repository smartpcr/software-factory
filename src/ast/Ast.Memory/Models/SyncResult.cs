// -----------------------------------------------------------------------
// <copyright file="SyncResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Result of an incremental sync operation.
/// Per architecture.md §5.5.
/// </summary>
public record SyncResult(
    int FilesChanged,
    int NodesAdded,
    int NodesRetired,
    int EdgesAdded,
    int EdgesRetired,
    int ExpertiseDecayed,
    TimeSpan Duration)
{
    /// <summary>
    /// Extraction errors encountered during sync. Non-empty when one or more
    /// files could not be extracted; callers should inspect this to detect
    /// missed updates rather than assuming FilesChanged==0 means success.
    /// </summary>
    public IReadOnlyList<string> ExtractionErrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Number of SIMILAR_TO edges created or updated by the SemanticIndexer
    /// during incremental embedding of changed nodes.
    /// </summary>
    public int SemanticEdgesCreated { get; init; }
}