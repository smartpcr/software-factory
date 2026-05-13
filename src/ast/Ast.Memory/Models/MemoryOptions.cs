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

    /// <summary>Time-based decay half-life in days (3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8_000;
    public int TokenBudgetCeiling { get; set; } = 32_000;
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;
    public double CentralityBoostFactor { get; set; } = 0.5;
    public ScoringWeights Scoring { get; set; } = new();
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>Path to the SQLite database file.</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Neo4j connection URI.</summary>
    public string? Neo4jUri { get; set; }
    public string? Neo4jUser { get; set; }
    public string? Neo4jPassword { get; set; }
    public string? QdrantEndpoint { get; set; }
    public int EmbeddingDimension { get; set; } = 768;
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