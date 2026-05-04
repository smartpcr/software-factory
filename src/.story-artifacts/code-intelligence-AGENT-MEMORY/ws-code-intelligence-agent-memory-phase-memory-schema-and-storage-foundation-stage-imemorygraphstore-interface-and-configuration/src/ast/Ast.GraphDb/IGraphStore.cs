// -----------------------------------------------------------------------
// <copyright file="IGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

/// <summary>
/// Base contract for a persistent graph store.
/// Implementors manage connections and/or file handles, hence <see cref="IAsyncDisposable"/>.
/// </summary>
public interface IGraphStore : IAsyncDisposable
{
}
