// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the agent memory system.
/// All constants from architecture.md §2.4.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// Gets or sets the cosine-similarity floor for SIMILAR_TO and DERIVED_FROM edges.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>
    /// Gets or sets the confidence multiplier on substantive code change.
    /// </summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the confidence multiplier on whitespace/comment-only change.
    /// </summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>
    /// Gets or sets the soft-delete threshold for expertise entries.
    /// </summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the time-based decay half-life in days (3 years).
    /// </summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    /// <summary>
    /// Gets or sets the default per-query token budget.
    /// </summary>
    public int TokenBudgetDefault { get; set; } = 8000;

    /// <summary>
    /// Gets or sets the maximum allowed token budget.
    /// </summary>
    public int TokenBudgetCeiling { get; set; } = 32000;

    /// <summary>
    /// Gets or sets the file-system watcher debounce in milliseconds.
    /// </summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the PageRank multiplier for decay slowdown (range 1.0–1.5).
    /// </summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the storage backend.
    /// </summary>
    public MemoryBackend Backend { get; set; }

    /// <summary>
    /// Gets or sets the SQLite database path.
    /// </summary>
    public string? SqlitePath { get; set; }

    /// <summary>
    /// Gets or sets the Neo4j connection URI.
    /// </summary>
    public string? Neo4jUri { get; set; }

    /// <summary>
    /// Gets or sets the Qdrant endpoint.
    /// </summary>
    public string? QdrantEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the model name for the Ollama LLM used by ConsolidationService.
    /// Operator-configured; no hardcoded default since the model depends on deployment.
    /// </summary>
    public string? ConsolidationModelName { get; set; }

    /// <summary>
    /// Gets or sets the Ollama HTTP endpoint for LLM generation.
    /// </summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
}