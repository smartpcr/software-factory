// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Central configuration for the agent memory subsystem.
/// All defaults come from architecture.md §2.4.
/// Designed for use with <c>IOptions&lt;MemoryOptions&gt;</c>.
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

    // ── Token Budgets ───────────────────────────────────────────────────

    /// <summary>Default token budget for context retrieval.</summary>
    public int TokenBudgetDefault { get; set; } = 8_000;

    /// <summary>Hard ceiling on token budget regardless of caller request.</summary>
    public int TokenBudgetCeiling { get; set; } = 32_000;

    // ── Sync ────────────────────────────────────────────────────────────

    /// <summary>Debounce interval in milliseconds for incremental sync.</summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1_000;

    // ── Scoring ─────────────────────────────────────────────────────────

    /// <summary>Additive boost applied to nodes with high centrality.</summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>Scoring weight distribution for relevance ranking.</summary>
    public ScoringWeights Scoring { get; set; } = new();

    // ── Backend ─────────────────────────────────────────────────────────

    /// <summary>Graph storage backend.</summary>
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>File path for the SQLite database (used when Backend is Sqlite).</summary>
    public string? SqlitePath { get; set; }

    /// <summary>Connection URI for Neo4j (used when Backend is Neo4j).</summary>
    public string? Neo4jUri { get; set; }

    /// <summary>Endpoint for Qdrant vector store.</summary>
    public string? QdrantEndpoint { get; set; }

    /// <summary>
    /// Model name for the Ollama LLM used by ConsolidationService and
    /// DetectCommunitiesAsync. No hardcoded default — the operator must
    /// configure this to match their locally deployed Ollama model
    /// at <c>http://localhost:11434</c>.
    /// </summary>
    public string? ConsolidationModelName { get; set; }
}