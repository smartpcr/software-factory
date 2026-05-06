// -----------------------------------------------------------------------
// <copyright file="ICommitRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

/// <summary>
/// Durable commit registration for the monotonic <c>commit_index</c>
/// used by <see cref="Models.VersionRange"/>.
/// Backed by the <c>commits</c> table (architecture.md §3.2.3, §4.3).
/// Separated from <see cref="IMemoryGraphStore"/> to keep the graph
/// interface limited to the canonical §5.1 contract.
/// </summary>
public interface ICommitRegistry
{
    /// <summary>
    /// Registers a commit SHA in the durable <c>commits</c> table and
    /// returns its monotonic <c>commit_index</c>.
    /// If the SHA is already registered, returns the existing index
    /// (idempotent).
    /// </summary>
    Task<int> RegisterCommitAsync(
        string commitSha,
        string? message = null,
        CancellationToken ct = default);
}