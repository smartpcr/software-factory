// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the memory system.
/// </summary>
public class MemoryOptions
{
    /// <summary>Cosine-similarity floor for SIMILAR_TO and DERIVED_FROM edges.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>Confidence multiplier on substantive code change.</summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>Confidence multiplier on whitespace/comment-only change.</summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>Expertise nodes below this are soft-deleted.</summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>Time-based decay half-life in days (3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    /// <summary>Per-query default token budget (Budget Controller).</summary>
    public int TokenBudgetDefault { get; set; } = 8_000;

    /// <summary>Maximum allowed token budget (Budget Controller).</summary>
    public int TokenBudgetCeiling { get; set; } = 32_000;

    /// <summary>Debounce for file-system watcher in milliseconds.</summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;

    /// <summary>
    /// Multiplied by normalized PageRank and added to 1.0 to form centrality_boost
    /// (range 1.0–1.5). See architecture.md §3.5.1.
    /// </summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>Storage backend selection.</summary>
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>Path to the SQLite database file.</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Neo4j connection URI.</summary>
    public string? Neo4jUri { get; set; }

    /// <summary>Qdrant vector-store endpoint.</summary>
    public string? QdrantEndpoint { get; set; }

    /// <summary>
    /// Operator-configured model name for the Ollama LLM used by
    /// ConsolidationService and DetectCommunitiesAsync. No hardcoded
    /// default — depends on what the operator has deployed.
    /// </summary>
    public string? ConsolidationModelName { get; set; }

    public ScoringWeights Weights { get; set; } = new();
}

/// <summary>
/// Storage backend selection for the memory graph.
/// </summary>
public enum MemoryBackend
{
    /// <summary>Local SQLite storage.</summary>
    Sqlite,

    /// <summary>Neo4j graph database.</summary>
    Neo4j
}