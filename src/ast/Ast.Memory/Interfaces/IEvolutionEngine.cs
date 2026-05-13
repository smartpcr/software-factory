// -----------------------------------------------------------------------
// <copyright file="IEvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Models;

/// <summary>
/// Evolution engine — write-back, consolidation, decay, and community
/// detection. Mechanisms by which the graph improves over sessions.
/// Per architecture.md §5.3.
/// </summary>
public interface IEvolutionEngine
{
    /// <summary>
    /// Write an expertise entry and link it to a code node via APPLIES_TO edge.
    /// Per §5.3 canonical signature.
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
    /// Acquires write-level lock. Creates SUPERSEDES edges from merged
    /// entry to originals.
    /// </summary>
    Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluate time-based decay and archive entries below ArchiveConfidenceFloor (0.1).
    /// Event-driven decay (code-change triggers) is handled separately by IncrementalSync (§3.6).
    /// </summary>
    Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Restore a previously archived expertise entry with new confidence.
    /// </summary>
    Task RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default);

    /// <summary>
    /// Louvain community detection → Topic nodes + BELONGS_TO_CLUSTER edges.
    /// Detects communities in the graph based on CALLS and SIMILAR_TO edges,
    /// creates Topic summary nodes with LLM-generated summaries, and links
    /// member nodes via BELONGS_TO_CLUSTER edges.
    /// </summary>
    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);
}