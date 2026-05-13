// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the memory system.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>Cosine similarity threshold for consolidation merges.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>Multiplicative decay factor for substantive code changes.</summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>Multiplicative decay factor for minor (whitespace/comment) changes.</summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>Confidence floor below which expertise is archived.</summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>Half-life for time-based decay in days (3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    /// <summary>Default token budget for context assembly.</summary>
    public int TokenBudgetDefault { get; set; } = 8000;

    /// <summary>Maximum token budget ceiling.</summary>
    public int TokenBudgetCeiling { get; set; } = 32000;

    /// <summary>Debounce interval for incremental sync in milliseconds.</summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1000;

    /// <summary>Boost factor for high-centrality nodes during decay.</summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>
    /// Model name for the Ollama LLM used by <c>ConsolidationService</c>
    /// and <c>DetectCommunitiesAsync</c>. Operator-configured — depends on
    /// what the operator has deployed to their local Ollama instance.
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