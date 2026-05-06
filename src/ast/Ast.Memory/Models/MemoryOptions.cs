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
    public double SimilarityThreshold { get; set; } = 0.85;
    public double StaleDecayFactor { get; set; } = 0.5;
    public double MinorDecayFactor { get; set; } = 0.9;
    public double ArchiveConfidenceFloor { get; set; } = 0.1;
    public int DecayHalfLifeDays { get; set; } = 1095;
    public int TokenBudgetDefault { get; set; } = 8_000;
    public int TokenBudgetCeiling { get; set; } = 32_000;
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;
    public double CentralityBoostFactor { get; set; } = 0.5;
    public ScoringWeights Scoring { get; set; } = new();
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;
    public string? SqlitePath { get; set; }
    public string? Neo4jUri { get; set; }
    public string? Neo4jUser { get; set; }
    public string? Neo4jPassword { get; set; }
    public string? QdrantEndpoint { get; set; }
    public int EmbeddingDimension { get; set; } = 768;
    public string? ConsolidationModelName { get; set; }
}