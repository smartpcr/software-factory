// -----------------------------------------------------------------------
// <copyright file="ExtractionModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Options controlling the extraction pipeline per architecture §5.4.
/// </summary>
public record ExtractionOptions
{
    /// <summary>Languages to extract. Defaults to C# and PowerShell.</summary>
    public IReadOnlyList<string> Languages { get; init; } = new[] { "C#", "PowerShell" };

    /// <summary>Optional subtree scope (e.g., "src/ast/"). Null means entire repo.</summary>
    public string? ScopeSubtree { get; init; }

    /// <summary>Commit SHA for temporal versioning. Null for working-tree extraction.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Monotonic commit index for temporal versioning.</summary>
    public int? CommitIndex { get; init; }

    /// <summary>Whether to include test projects in extraction. Defaults to false.</summary>
    public bool IncludeTestProjects { get; init; }

    /// <summary>Whether to generate embeddings in the semantic stage. Defaults to true.</summary>
    public bool GenerateEmbeddings { get; init; } = true;

    /// <summary>
    /// Cosine-similarity floor for SIMILAR_TO edges per architecture §2.4.
    /// Defaults to 0.85.
    /// </summary>
    public double SimilarityThreshold { get; init; } = 0.85;
}

/// <summary>
/// Result of an extraction pipeline run per architecture §5.4.
/// </summary>
public record ExtractionResult
{
    /// <summary>Total nodes created or upserted during extraction.</summary>
    public int NodesCreated { get; init; }

    /// <summary>Total edges created during extraction.</summary>
    public int EdgesCreated { get; init; }

    /// <summary>Total embeddings generated during the semantic stage.</summary>
    public int EmbeddingsGenerated { get; init; }

    /// <summary>Number of files that failed to parse and were skipped.</summary>
    public int FilesSkipped { get; init; }

    /// <summary>Error messages from skipped files or other non-fatal failures.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Combines two extraction results.</summary>
    public ExtractionResult Merge(ExtractionResult other) => new()
    {
        NodesCreated = this.NodesCreated + other.NodesCreated,
        EdgesCreated = this.EdgesCreated + other.EdgesCreated,
        EmbeddingsGenerated = this.EmbeddingsGenerated + other.EmbeddingsGenerated,
        FilesSkipped = this.FilesSkipped + other.FilesSkipped,
        Errors = this.Errors.Concat(other.Errors).ToList(),
    };

    /// <summary>An empty result with no nodes, edges, or errors.</summary>
    public static ExtractionResult Empty { get; } = new();
}

/// <summary>
/// Result of a single extraction stage.
/// </summary>
public record StageResult
{
    public int NodesCreated { get; init; }
    public int EdgesCreated { get; init; }
    public int EmbeddingsGenerated { get; init; }
    public int FilesSkipped { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public ExtractionResult ToExtractionResult() => new()
    {
        NodesCreated = this.NodesCreated,
        EdgesCreated = this.EdgesCreated,
        EmbeddingsGenerated = this.EmbeddingsGenerated,
        FilesSkipped = this.FilesSkipped,
        Errors = this.Errors,
    };

    public static StageResult Empty { get; } = new();
}

/// <summary>
/// Result of the normalization pass per architecture §4.2.
/// </summary>
public class NormalizationResult
{
    /// <summary>Number of nodes that received canonical IDs.</summary>
    public int NodesNormalized { get; init; }

    /// <summary>Number of cross-language duplicates merged.</summary>
    public int DuplicatesMerged { get; init; }

    /// <summary>Mapping from old node IDs to canonical IDs.</summary>
    public IReadOnlyDictionary<string, string> IdMappings { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Mapping from stub node IDs to their source file paths (for evidence preservation).</summary>
    public IReadOnlyDictionary<string, string> MergedStubFiles { get; init; } =
        new Dictionary<string, string>();
}