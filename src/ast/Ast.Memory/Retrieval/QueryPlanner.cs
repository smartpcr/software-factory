// -----------------------------------------------------------------------
// <copyright file="QueryPlanner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

using Ast.Memory.Models;

/// <summary>
/// Selects <see cref="RetrievalMode"/> values based on the query shape.
/// Implements automatic seed/query mode selection for Hybrid mode.
/// </summary>
internal sealed class QueryPlanner
{
    /// <summary>
    /// Determines which retrieval modes to execute for the given request.
    /// For non-Hybrid, returns the single requested mode.
    /// For Hybrid, ALWAYS returns all four modes (architecture.md §3.3).
    /// When seeds are absent, SemanticSearch runs first and its results
    /// become seeds for StructuralDrill/CallTrace — the engine handles
    /// the phased execution order.
    /// </summary>
    public IReadOnlyList<RetrievalMode> PlanQuery(RetrievalRequest request)
    {
        if (request.Mode != RetrievalMode.Hybrid)
        {
            return [request.Mode];
        }

        // Hybrid always runs all four modes. StructuralDrill and CallTrace
        // will use original seeds if provided, or semantic-hit seeds injected
        // by the engine when no explicit seeds exist.
        return
        [
            RetrievalMode.SemanticSearch,
            RetrievalMode.StructuralDrill,
            RetrievalMode.CallTrace,
            RetrievalMode.ExpertiseRecall
        ];
    }
}