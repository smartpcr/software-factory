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