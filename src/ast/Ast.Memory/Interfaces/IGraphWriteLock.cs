// -----------------------------------------------------------------------
// <copyright file="IGraphWriteLock.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

/// <summary>
/// Shared write-level lock for the memory graph store.
/// All mutations (ApplyDiffAsync, RecordExpertiseAsync, ConsolidateAsync,
/// DecayStaleExpertiseAsync) must serialize through this lock per
/// architecture.md §3.7.
/// Backed by <see cref="SemaphoreSlim"/>(1,1) for SQLite or
/// wrapping ACID transactions for Neo4j.
/// Registered as a process-wide singleton in DI.
/// </summary>
public interface IGraphWriteLock
{
    /// <summary>
    /// Acquires the write lock asynchronously.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/> that releases the lock when disposed.</returns>
    Task<IDisposable> AcquireAsync(CancellationToken ct = default);
}