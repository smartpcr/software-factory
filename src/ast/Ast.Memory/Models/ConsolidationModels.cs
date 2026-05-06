// -----------------------------------------------------------------------
// <copyright file="ConsolidationModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Options for expertise consolidation.
/// </summary>
public record ConsolidationOptions(
    double SimilarityThreshold = 0.85,
    int MaxMergesPerRun = 50);

/// <summary>
/// Result of a consolidation run.
/// </summary>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds);

/// <summary>
/// Options for time-based decay evaluation.
/// </summary>
public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1);

/// <summary>
/// Result of a decay evaluation run.
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

/// <summary>
/// Result of an incremental sync operation.
/// </summary>
public record SyncResult(
    int FilesChanged,
    int NodesAdded,
    int NodesRetired,
    int EdgesAdded,
    int EdgesRetired,
    int ExpertiseDecayed,
    System.TimeSpan Duration);