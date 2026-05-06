// -----------------------------------------------------------------------
// <copyright file="IGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Ast.Core;

/// <summary>
/// Base contract for a persistent graph store.
/// Implementors manage connections and/or file handles, hence <see cref="IAsyncDisposable"/>.
/// </summary>
public interface IGraphStore : IAsyncDisposable
{
    /// <summary>
    /// Persists a hyperedge into the graph store.
    /// The edge carries its own metadata (including version, semantic relation, etc.).
    /// </summary>
    Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken);
}