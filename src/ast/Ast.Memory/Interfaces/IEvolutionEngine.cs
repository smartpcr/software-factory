// -----------------------------------------------------------------------
// <copyright file="IEvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Engine for evolving the memory graph: expertise write-back, consolidation, and decay.
/// </summary>
public interface IEvolutionEngine
{
    Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default);

    Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken ct = default);

    Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken ct = default);

    Task RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default);

    Task<CommunityResult> DetectCommunitiesAsync(
        CancellationToken ct = default);
}