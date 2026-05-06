// -----------------------------------------------------------------------
// <copyright file="RetrievalTypes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

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

/// <summary>
/// Result of a memory retrieval.
/// </summary>
public record RetrievalResult(
    IReadOnlyList<ScoredMemoryNode> Nodes,
    string ContextMarkdown,
    int TokensUsed,
    IReadOnlyList<RetrievalMode> ModesExecuted,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// A memory node with composite score breakdown.
/// </summary>
public record ScoredMemoryNode(
    MemoryNode Node,
    double CompositeScore,
    double StructuralScore,
    double SemanticScore,
    double CentralityScore,
    double RecencyScore,
    double ExpertiseScore);