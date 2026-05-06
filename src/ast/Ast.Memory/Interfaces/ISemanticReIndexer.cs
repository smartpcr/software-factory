// -----------------------------------------------------------------------
// <copyright file="ISemanticReIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Core;

/// <summary>
/// Re-embeds changed nodes so the vector index and SIMILAR_TO edges stay
/// current. Invoked by <see cref="IIncrementalSync"/> after substantive
/// structural changes are detected (architecture.md §3.6 step 9).
/// </summary>
public interface ISemanticReIndexer
{
    /// <summary>
    /// Re-indexes the given nodes by regenerating their embeddings and
    /// updating the vector store and SIMILAR_TO edges.
    /// </summary>
    Task ReIndexAsync(
        IReadOnlyList<HypergraphNode> changedNodes,
        CancellationToken ct = default);
}