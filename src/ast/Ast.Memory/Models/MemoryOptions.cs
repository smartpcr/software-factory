// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the memory subsystem.
/// </summary>
public class MemoryOptions
{
    public double SimilarityThreshold { get; set; } = 0.85;

    public double StaleDecayFactor { get; set; } = 0.5;

    public double MinorDecayFactor { get; set; } = 0.9;

    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    public int DecayHalfLifeDays { get; set; } = 1095;

    public int TokenBudgetDefault { get; set; } = 8000;

    public int TokenBudgetCeiling { get; set; } = 32000;

    public int IncrementalSyncDebounceMs { get; set; } = 1000;

    public double CentralityBoostFactor { get; set; } = 0.5;

    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    public string? SqlitePath { get; set; }

    public string? Neo4jUri { get; set; }

    public string? QdrantEndpoint { get; set; }

    public string? ConsolidationModelName { get; set; }

    public ScoringWeights Weights { get; set; } = new();
}

/// <summary>
/// Scoring weights for composite retrieval ranking. Must sum to 1.00.
/// </summary>
public record ScoringWeights(
    double Structural = 0.30,
    double Semantic = 0.25,
    double Centrality = 0.20,
    double Recency = 0.15,
    double Expertise = 0.10);