// -----------------------------------------------------------------------
// <copyright file="EvolutionResults.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Result of a consolidation run that merges semantically similar expertise entries.
/// Per architecture.md §5.3.
/// </summary>
/// <param name="CandidatesPaired">Number of candidate pairs evaluated.</param>
/// <param name="MergedCount">Number of successful merges.</param>
/// <param name="MergedEntryIds">IDs of the newly created merged entries.</param>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    int SupersededCount,
    IReadOnlyList<string> MergedEntryIds);

/// <summary>
/// Result of a decay evaluation run.
/// Per architecture.md §5.3.
/// </summary>
public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived);

/// <summary>
/// Result of community detection.
/// Per architecture.md §5.3.
/// </summary>
public record CommunityResult(
    int CommunitiesDetected,
    int TopicNodesCreated,
    int ClusterEdgesCreated);