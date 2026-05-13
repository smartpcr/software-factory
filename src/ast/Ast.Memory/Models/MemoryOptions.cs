// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the agent memory subsystem.
/// Matches architecture.md §2.4 constants.
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

    /// <summary>
    /// Ollama API endpoint. Defaults to the standard local instance.
    /// </summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Minimum community size for Topic node creation.
    /// Communities smaller than this threshold are ignored.
    /// </summary>
    public int MinCommunitySize { get; set; } = 5;

    /// <summary>
    /// Scoring weights used by the Ranker. Sum should equal 1.00.
    /// Per architecture.md §2.4.
    /// </summary>
    public ScoringWeights ScoringWeights { get; set; } = new();
}