// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the agent memory subsystem.
/// All constants sourced from architecture.md §2.4.
/// Bind via <c>IConfiguration.GetSection("Memory").Get&lt;MemoryOptions&gt;()</c>.
/// </summary>
public class MemoryOptions
{
    /// <summary>Configuration section key for binding.</summary>
    public const string SectionName = "Memory";

    // ── Similarity & Decay ──────────────────────────────────────────

    /// <summary>
    /// Cosine-similarity threshold for treating two embeddings as equivalent.
    /// Default: 0.85.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>
    /// Multiplicative decay applied to expertise confidence when the target
    /// node is marked stale (major refactor / delete). Default: 0.5.
    /// </summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>
    /// Multiplicative decay applied to expertise confidence on a minor
    /// content change to the target node. Default: 0.9.
    /// </summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>
    /// Expertise entries whose confidence drops below this floor are
    /// automatically archived. Default: 0.1.
    /// </summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>
    /// Half-life in days for time-based recency decay.
    /// Default: 1095 (~3 years).
    /// </summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    // ── Token Budgets ───────────────────────────────────────────────

    /// <summary>
    /// Default token budget for context retrieval. Default: 8000.
    /// </summary>
    public int TokenBudgetDefault { get; set; } = 8000;

    /// <summary>
    /// Maximum token budget ceiling. Default: 32000.
    /// </summary>
    public int TokenBudgetCeiling { get; set; } = 32000;

    // ── Sync ────────────────────────────────────────────────────────

    /// <summary>
    /// Debounce interval in milliseconds for incremental sync after file changes.
    /// Default: 1000.
    /// </summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1000;

    // ── Scoring ─────────────────────────────────────────────────────

    /// <summary>
    /// Additive boost factor applied to a node's score based on its
    /// graph centrality. Default: 0.5.
    /// </summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>
    /// Multi-signal scoring weights. Defaults per architecture.md §2.4.
    /// </summary>
    public ScoringWeights ScoringWeights { get; set; } = new();

    // ── Backend ─────────────────────────────────────────────────────

    /// <summary>
    /// Which storage backend to use. Default: Sqlite.
    /// </summary>
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>
    /// File path for the SQLite database when <see cref="Backend"/> is <see cref="MemoryBackend.Sqlite"/>.
    /// Default: "memory.db" (relative to the repository root).
    /// </summary>
    public string SqlitePath { get; set; } = "memory.db";

    /// <summary>
    /// Connection URI for Neo4j when <see cref="Backend"/> is <see cref="MemoryBackend.Neo4j"/>.
    /// Example: "bolt://localhost:7687".
    /// </summary>
    public string? Neo4jUri { get; set; }

    /// <summary>
    /// Endpoint URL for Qdrant vector search integration.
    /// Example: "http://localhost:6334".
    /// </summary>
    public string? QdrantEndpoint { get; set; }

    // ── Consolidation ───────────────────────────────────────────────

    /// <summary>
    /// Model name for the Ollama LLM used by ConsolidationService and
    /// DetectCommunitiesAsync. No hardcoded default — the operator must
    /// configure this to match a model deployed to their local Ollama
    /// instance at http://localhost:11434.
    /// </summary>
    public string? ConsolidationModelName { get; set; }
}