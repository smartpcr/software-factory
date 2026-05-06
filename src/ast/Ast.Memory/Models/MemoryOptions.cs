// -----------------------------------------------------------------------
// <copyright file="MemoryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Configuration options for the agent memory system.
/// Constants and thresholds per architecture.md §2.4.
/// </summary>
public class MemoryOptions
{
    /// <summary>
    /// Cosine-similarity floor for <c>SIMILAR_TO</c> edges.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.85;

    /// <summary>
    /// Confidence multiplier on substantive code change.
    /// </summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>
    /// Confidence multiplier on whitespace/comment-only change.
    /// </summary>
    public double MinorDecayFactor { get; set; } = 0.9;

    /// <summary>
    /// Expertise nodes below this confidence are soft-deleted.
    /// </summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>
    /// Time-based decay half-life in days (3 years).
    /// </summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    /// <summary>
    /// Per-query default token budget.
    /// </summary>
    public int TokenBudgetDefault { get; set; } = 8000;

    /// <summary>
    /// Maximum allowed token budget.
    /// </summary>
    public int TokenBudgetCeiling { get; set; } = 32000;

    /// <summary>
    /// Debounce interval for file-system watcher (ms).
    /// </summary>
    public int IncrementalSyncDebounceMs { get; set; } = 1000;

    /// <summary>
    /// Multiplied by normalized PageRank and added to 1.0 for centrality boost.
    /// </summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>
    /// Storage backend selection.
    /// </summary>
    public MemoryBackend Backend { get; set; } = MemoryBackend.Sqlite;

    /// <summary>
    /// SQLite database file path — shared by both SqliteGraphStore and SqliteVectorStore
    /// (architecture.md §3.2). When unset, local vector storage is disabled.
    /// </summary>
    public string? SqlitePath { get; set; }

    /// <summary>
    /// Neo4j connection URI (when Backend = Neo4j).
    /// </summary>
    public string? Neo4jUri { get; set; }

    /// <summary>
    /// Qdrant endpoint (when Backend = Neo4j / team mode).
    /// </summary>
    public string? QdrantEndpoint { get; set; }

    /// <summary>
    /// Model name for LLM used by ConsolidationService and DetectCommunitiesAsync.
    /// No hardcoded default — depends on operator's deployed Ollama instance.
    /// </summary>
    public string? ConsolidationModelName { get; set; }

    /// <summary>
    /// Batch size for embedding generation requests.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// Vector collection name for memory embeddings.
    /// </summary>
    public string VectorCollectionName { get; set; } = "memory_embeddings";
}