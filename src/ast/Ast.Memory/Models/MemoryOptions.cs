// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the memory graph system.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>Cosine-similarity floor for SIMILAR_TO and DERIVED_FROM edges.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>Confidence multiplier on substantive code change.</summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>Confidence multiplier on whitespace/comment-only change.</summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>Expertise nodes below this are soft-deleted.</summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;
    public double DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8000;
    public int TokenBudgetCeiling { get; set; } = 32000;
    public int IncrementalSyncDebounceMs { get; set; } = 1000;
    public double CentralityBoostFactor { get; set; } = 0.5;

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