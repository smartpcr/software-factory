// -----------------------------------------------------------------------
// <copyright file="IIncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Keeps the memory graph current without full re-extraction.
/// Per architecture.md §3.6, §5.5.
/// </summary>
public interface IIncrementalSync
{
    /// <summary>
    /// Computes git diff between two commits and updates the graph:
    /// upserts/retires nodes and edges, propagates stale markers,
    /// triggers expertise decay, and re-embeds changed nodes.
    /// </summary>
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);
}