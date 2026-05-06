// -----------------------------------------------------------------------
// <copyright file="EvolutionTypes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Options for expertise consolidation.
/// </summary>
public record ConsolidationOptions(
    double SimilarityThreshold = 0.85,
    int MaxMergesPerRun = 50);

/// <summary>
/// Result of expertise consolidation.
/// </summary>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds);

/// <summary>
/// Options for time-based expertise decay.
/// </summary>
public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1);

/// <summary>
/// Result of expertise decay evaluation.
/// </summary>
public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived);

/// <summary>
/// Result of community detection.
/// </summary>
public record CommunityResult(
    int CommunitiesDetected,
    int TopicNodesCreated,
    int ClusterEdgesCreated);