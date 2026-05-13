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
public class MemoryOptions
{
    // ── Similarity / Decay ──────────────────────────────────────────────

    /// <summary>Cosine-similarity threshold for semantic deduplication.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>Multiplicative factor applied when a node is stale (no longer in HEAD).</summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>Multiplicative factor applied for minor (whitespace-only) changes.</summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>Confidence floor below which expertise entries are auto-archived.</summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>Half-life in days for time-based confidence decay (≈3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8_000;
    public int TokenBudgetCeiling { get; set; } = 32_000;
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;
    public double CentralityBoostFactor { get; set; } = 0.5;
    public ScoringWeights Scoring { get; set; } = new();
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>File path for the SQLite database (used when Backend is Sqlite).</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Connection URI for Neo4j (used when Backend is Neo4j).</summary>
    public string? Neo4jUri { get; set; }
    public string? Neo4jUser { get; set; }
    public string? Neo4jPassword { get; set; }
    public string? QdrantEndpoint { get; set; }
    public int EmbeddingDimension { get; set; } = 768;
    public string? ConsolidationModelName { get; set; }
}