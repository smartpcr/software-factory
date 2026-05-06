// -----------------------------------------------------------------------
// <copyright file="RetrievalDiagnostics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Diagnostic information about a single retrieval mode's execution.
/// </summary>
/// <param name="Mode">The retrieval mode that was executed.</param>
/// <param name="CandidateCount">Number of candidate nodes discovered by this mode.</param>
/// <param name="Duration">Wall-clock time spent executing this mode.</param>
public record ModeDiagnostic(
    RetrievalMode Mode,
    int CandidateCount,
    TimeSpan Duration);

/// <summary>
/// Aggregated diagnostics for a retrieval operation.
/// Contains per-mode candidate counts, timing, and overall statistics.
/// </summary>
/// <param name="ModeMetrics">Per-mode diagnostic information.</param>
/// <param name="TotalDuration">Total wall-clock time for the entire retrieval operation.</param>
/// <param name="TotalCandidatesBeforeRanking">Total candidates discovered across all modes before dedup/ranking.</param>
/// <param name="TotalCandidatesAfterRanking">Candidates remaining after ranking and budget trimming.</param>
/// <param name="Errors">Any non-fatal errors or warnings encountered during retrieval.</param>
public record RetrievalDiagnostics(
    IReadOnlyList<ModeDiagnostic> ModeMetrics,
    TimeSpan TotalDuration,
    int TotalCandidatesBeforeRanking = 0,
    int TotalCandidatesAfterRanking = 0,
    IReadOnlyList<string>? Errors = null);