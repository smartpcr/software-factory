// -----------------------------------------------------------------------
// <copyright file="IEvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Evolution engine for the memory graph. Handles expertise write-back,
/// consolidation, decay, and community detection — the mechanisms by
/// which the graph improves over agent sessions.
/// </summary>
public interface IEvolutionEngine
{
    /// <summary>
    /// Write an expertise entry and link it to a code node via an APPLIES_TO edge.
    /// Deduplicates by content hash: if an identical entry already exists, returns it.
    /// </summary>
    Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default);

    /// <summary>
    /// LLM-powered merge of semantically similar expertise entries.
    /// Acquires write-level lock. Creates SUPERSEDES edges from merged entry to originals.
    /// </summary>
    Task<ConsolidationResult> ConsolidateAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Evaluate time-based decay and archive entries below ArchiveConfidenceFloor (0.1).
    /// Event-driven decay (code-change triggers) is handled separately by IncrementalSync.
    /// </summary>
    Task<DecayResult> DecayStaleExpertiseAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Restore a previously archived expertise entry with the specified confidence.
    /// Sets Archived to false and Confidence to the given value.
    /// </summary>
    Task<ExpertiseEntry> RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default);

    /// <summary>
    /// Louvain community detection over CALLS/SIMILAR_TO edges.
    /// Creates BELONGS_TO_CLUSTER edges and Topic summary nodes in the Semantic layer.
    /// </summary>
    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);
}