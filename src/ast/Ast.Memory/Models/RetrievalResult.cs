// -----------------------------------------------------------------------
// <copyright file="RetrievalResult.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Result of a memory retrieval operation.
/// </summary>
public class RetrievalResult
{
    /// <summary>
    /// Ranked nodes within the token budget.
    /// </summary>
    public IReadOnlyList<ScoredMemoryNode> Nodes { get; init; } = [];

    /// <summary>
    /// Assembled context as Markdown.
    /// </summary>
    public string ContextMarkdown { get; init; } = string.Empty;

    /// <summary>
    /// Actual tokens used in the context.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Retrieval modes that were executed.
    /// </summary>
    public IReadOnlyList<RetrievalMode> ModesExecuted { get; init; } = [];

    /// <summary>
    /// Per-mode diagnostics.
    /// </summary>
    public RetrievalDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// Creates an empty result for error/degradation scenarios.
    /// </summary>
    public static RetrievalResult Empty(string? error = null) => new()
    {
        Nodes = [],
        ContextMarkdown = string.Empty,
        TokensUsed = 0,
        ModesExecuted = [],
        Diagnostics = error != null ? new RetrievalDiagnostics { Error = error } : null
    };
}
