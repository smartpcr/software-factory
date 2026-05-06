// -----------------------------------------------------------------------
// <copyright file="EvolutionResults.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Result of community detection via Louvain algorithm.
/// </summary>
public record CommunityResult(
    int CommunitiesDetected,
    int TopicNodesCreated,
    int ClusterEdgesCreated
);

/// <summary>
/// Result of expertise consolidation.
/// </summary>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds
);

/// <summary>
/// Result of expertise decay evaluation.
/// </summary>
public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived
);

/// <summary>
/// Options for consolidation.
/// </summary>
public record ConsolidationOptions(
    double SimilarityThreshold = 0.85,
    int MaxMergesPerRun = 50
);

/// <summary>
/// Options for decay evaluation.
/// </summary>
public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1
);