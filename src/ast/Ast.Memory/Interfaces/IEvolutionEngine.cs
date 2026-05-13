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
    /// Louvain community detection → Topic nodes + BELONGS_TO_CLUSTER edges.
    /// Detects communities in the graph based on CALLS and SIMILAR_TO edges,
    /// creates Topic summary nodes with LLM-generated summaries, and links
    /// member nodes via BELONGS_TO_CLUSTER edges.
    /// </summary>
    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);

}

/// <summary>Options for expertise consolidation.</summary>
public record ConsolidationOptions(
    double SimilarityThreshold = 0.85,
    int MaxMergesPerRun = 50);

/// <summary>Result of expertise consolidation.</summary>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds);

/// <summary>Options for time-based expertise decay.</summary>
public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1);

/// <summary>Result of time-based expertise decay evaluation.</summary>
public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived);

/// <summary>Result of community detection.</summary>
public record CommunityResult(
    int CommunitiesDetected,
    int TopicNodesCreated,
    int ClusterEdgesCreated);