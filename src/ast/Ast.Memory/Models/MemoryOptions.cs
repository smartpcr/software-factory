// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Central configuration for the agent memory subsystem.
/// All defaults come from architecture.md §2.4.
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

    /// <summary>Time-based decay half-life in days (3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8_000;
    public int TokenBudgetCeiling { get; set; } = 32_000;
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;
    public double CentralityBoostFactor { get; set; } = 0.5;
    public ScoringWeights Scoring { get; set; } = new();
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>Scoring weight distribution for relevance ranking.</summary>
    public ScoringWeights Scoring { get; set; } = new();

    // ── Consolidation ───────────────────────────────────────────────────

    /// <summary>Maximum number of merges per consolidation run.</summary>
    public int MaxMergesPerRun { get; set; } = 50;

    // ── Vector Collection ───────────────────────────────────────────────

    /// <summary>Name of the vector collection for expertise embeddings.</summary>
    public string ExpertiseVectorCollection { get; set; } = "memory_expertise";

    // ── Backend ─────────────────────────────────────────────────────────

    /// <summary>Graph storage backend.</summary>
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>Path to the SQLite database file.</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Neo4j connection URI.</summary>
    public string? Neo4jUri { get; set; }

    /// <summary>Endpoint for Qdrant vector store.</summary>
    public string? QdrantEndpoint { get; set; }

    /// <summary>
    /// Model name for the Ollama LLM used by ConsolidationService and
    /// DetectCommunitiesAsync.
    /// </summary>
    public string? ConsolidationModelName { get; set; }
}

/// <summary>
/// Scoring weight distribution for relevance ranking (§5.2).
/// Weights should sum to 1.0.
/// </summary>
public record ScoringWeights(
    double Structural = 0.30,
    double Semantic = 0.25,
    double Centrality = 0.20,
    double Recency = 0.15,
    double Expertise = 0.10
)
{
    /// <summary>Parameterless constructor with defaults.</summary>
    public ScoringWeights() : this(0.30, 0.25, 0.20, 0.15, 0.10) { }
}