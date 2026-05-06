// -----------------------------------------------------------------------
// <copyright file="RetrievalResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Result of a memory retrieval operation.
/// Contains scored nodes, assembled context, and execution diagnostics.
/// </summary>
/// <param name="Nodes">Scored memory nodes, sorted by descending composite score.</param>
/// <param name="ContextMarkdown">Deterministic Markdown output for the agent's prompt.</param>
/// <param name="TokensUsed">Number of tokens consumed by the assembled context.</param>
/// <param name="ModesExecuted">Retrieval modes that were actually executed.</param>
/// <param name="Diagnostics">Optional per-mode candidate counts and timing information.</param>
public record RetrievalResult(
    IReadOnlyList<ScoredMemoryNode> Nodes,
    string ContextMarkdown,
    int TokensUsed,
    IReadOnlyList<RetrievalMode> ModesExecuted,
    RetrievalDiagnostics? Diagnostics = null)
{
    /// <summary>
    /// Creates an empty result, typically returned when the graph store is unavailable.
    /// </summary>
    /// <param name="error">Optional diagnostic error message.</param>
    /// <returns>An empty <see cref="RetrievalResult"/>.</returns>
    public static RetrievalResult Empty(string? error = null)
    {
        return new RetrievalResult(
            Nodes: [],
            ContextMarkdown: string.Empty,
            TokensUsed: 0,
            ModesExecuted: [],
            Diagnostics: error != null
                ? new RetrievalDiagnostics(
                    ModeMetrics: [],
                    TotalDuration: System.TimeSpan.Zero,
                    Errors: [error])
                : null);
    }
}