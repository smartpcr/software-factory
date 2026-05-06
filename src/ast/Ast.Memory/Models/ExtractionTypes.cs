// -----------------------------------------------------------------------
// <copyright file="ExtractionTypes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Options for extraction pipeline.
/// </summary>
public record ExtractionOptions(
    IReadOnlyList<string> Languages,
    string? ScopeSubtree = null,
    string? CommitSha = null,
    int? CommitIndex = null);

/// <summary>
/// Result of an extraction pipeline run.
/// </summary>
public record ExtractionResult(
    int NodesCreated,
    int EdgesCreated,
    int EmbeddingsGenerated,
    int FilesSkipped,
    IReadOnlyList<string> Errors);