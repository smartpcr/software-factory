// -----------------------------------------------------------------------
// <copyright file="EvolutionResults.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Options controlling consolidation behavior.
/// Matches the canonical shape in architecture.md §5.3.
/// </summary>
/// <param name="SimilarityThreshold">
/// Per-call cosine-similarity floor override. When <c>null</c>, the service
/// falls back to <see cref="MemoryOptions.SimilarityThreshold"/> (default 0.85).
/// </param>
/// <param name="MaxMergesPerRun">Maximum number of merges per consolidation run.</param>
public record ConsolidationOptions(
    double? SimilarityThreshold = null,
    int MaxMergesPerRun = 50);

/// <summary>
/// Result of a consolidation run.
/// </summary>
/// <param name="CandidatesPaired">Number of candidate pairs evaluated.</param>
/// <param name="MergedCount">Number of successful merges.</param>
/// <param name="MergedEntryIds">IDs of the newly created merged entries.</param>
public record ConsolidationResult(
    int CandidatesPaired,
    int MergedCount,
    IReadOnlyList<string> MergedEntryIds);