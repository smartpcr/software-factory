// -----------------------------------------------------------------------
// <copyright file="RankingContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

/// <summary>
/// Input context for the Ranker containing pre-computed retrieval data.
/// </summary>
public sealed class RankingContext
{
    /// <summary>
    /// BFS distances from seed nodes (nodeId → distance).
    /// Used by the structural signal.
    /// </summary>
    public IReadOnlyDictionary<string, int> BfsDistances { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Query embedding vector for semantic similarity scoring.
    /// Null when no query text was provided or embedding is unavailable.
    /// </summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Current HEAD commit index for recency scoring.
    /// </summary>
    public int HeadCommitIndex { get; init; }
}