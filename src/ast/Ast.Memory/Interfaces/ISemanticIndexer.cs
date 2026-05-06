// -----------------------------------------------------------------------
// <copyright file="ISemanticIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Core;

/// <summary>
/// Generates embeddings for nodes and creates SIMILAR_TO edges.
/// Per architecture.md §3.1.
/// </summary>
public interface ISemanticIndexer
{
    /// <summary>
    /// Re-embeds the given nodes and updates similarity edges.
    /// Returns the number of embeddings generated.
    /// </summary>
    Task<int> IndexNodesAsync(
        IReadOnlyList<HypergraphNode> nodes,
        CancellationToken ct = default);
}