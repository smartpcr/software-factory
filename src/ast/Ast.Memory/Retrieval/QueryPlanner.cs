// -----------------------------------------------------------------------
// <copyright file="QueryPlanner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

using System;
using System.Collections.Generic;
using Ast.Memory.Models;

/// <summary>
/// Selects one or more <see cref="RetrievalMode"/> values based on the shape
/// of a <see cref="RetrievalRequest"/>. This is the first stage of the retrieval
/// pipeline (architecture.md §3.3).
/// </summary>
public static class QueryPlanner
{
    /// <summary>
    /// Analyzes the request and returns the ordered list of retrieval modes to execute.
    /// </summary>
    /// <remarks>
    /// Mode selection rules (architecture.md §3.3 and §6.2):
    /// <list type="bullet">
    ///   <item>Explicit (non-Hybrid) mode → use only that mode (override).</item>
    ///   <item>Hybrid with free-text (QueryText present) → all four modes
    ///         (StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall).</item>
    ///   <item>Hybrid with seed nodes only (no QueryText) → StructuralDrill + CallTrace.</item>
    ///   <item>Hybrid with neither → empty list (no retrieval possible).</item>
    /// </list>
    /// Modes are returned in a canonical order for deterministic execution.
    /// </remarks>
    /// <param name="request">The retrieval request to plan for.</param>
    /// <returns>Ordered list of retrieval modes to execute.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="request"/> is null.</exception>
    public static IReadOnlyList<RetrievalMode> PlanQuery(RetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Explicit (non-Hybrid) mode overrides automatic selection.
        if (request.Mode != RetrievalMode.Hybrid)
        {
            return [request.Mode];
        }

        var hasText = request.HasQueryText;
        var hasSeeds = request.HasSeedNodes;

        // Free-text present (with or without seeds) → Hybrid = all four modes.
        if (hasText)
        {
            return
            [
                RetrievalMode.StructuralDrill,
                RetrievalMode.CallTrace,
                RetrievalMode.SemanticSearch,
                RetrievalMode.ExpertiseRecall
            ];
        }

        // Seed nodes only (no free-text) → StructuralDrill + CallTrace.
        if (hasSeeds)
        {
            return
            [
                RetrievalMode.StructuralDrill,
                RetrievalMode.CallTrace
            ];
        }

        // Neither text nor seeds — no retrieval possible.
        return [];
    }

    /// <summary>
    /// Validates that the request is well-formed for the selected or planned modes.
    /// Returns a list of validation errors (empty if valid).
    /// </summary>
    /// <param name="request">The retrieval request to validate.</param>
    /// <returns>List of validation error messages. Empty if the request is valid.</returns>
    public static IReadOnlyList<string> Validate(RetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (request.Mode != RetrievalMode.Hybrid)
        {
            switch (request.Mode)
            {
                case RetrievalMode.StructuralDrill:
                case RetrievalMode.CallTrace:
                    if (!request.HasSeedNodes)
                    {
                        errors.Add(
                            $"{request.Mode} requires at least one entry in SeedNodeIds.");
                    }

                    break;

                case RetrievalMode.SemanticSearch:
                    if (!request.HasQueryText)
                    {
                        errors.Add("SemanticSearch requires non-empty QueryText.");
                    }

                    break;

                case RetrievalMode.ExpertiseRecall:
                    if (!request.HasQueryText && !request.HasSeedNodes)
                    {
                        errors.Add(
                            "ExpertiseRecall requires at least one of QueryText or SeedNodeIds.");
                    }

                    break;
            }
        }
        else
        {
            if (!request.HasQueryText && !request.HasSeedNodes)
            {
                errors.Add(
                    "Hybrid mode requires at least one of QueryText or SeedNodeIds.");
            }
        }

        return errors;
    }
}