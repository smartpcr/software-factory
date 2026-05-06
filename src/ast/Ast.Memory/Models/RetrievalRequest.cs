// -----------------------------------------------------------------------
// <copyright file="RetrievalRequest.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Request for memory retrieval.
/// </summary>
public record RetrievalRequest(
    string? QueryText,
    IReadOnlyList<string>? SeedNodeIds = null,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    int TokenBudget = 8_000,
    int? AtCommitIndex = null,
    ScoringWeights? Weights = null);