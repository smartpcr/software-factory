// -----------------------------------------------------------------------
// <copyright file="IExpertiseStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Store for agent-authored expertise entries (separate from the hypergraph).
/// </summary>
public interface IExpertiseStore
{
    /// <summary>
    /// Gets expertise entries that apply to the specified node.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> GetForNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches expertise entries by embedding similarity.
    /// </summary>
    Task<IReadOnlyList<ExpertiseEntry>> SearchByEmbeddingAsync(
        float[] queryEmbedding,
        int limit = 10,
        CancellationToken cancellationToken = default);
}