// -----------------------------------------------------------------------
// <copyright file="IIncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

using Ast.Memory.Models;

/// <summary>
/// Incremental sync: compute git diff between two commits and update graph.
/// Also supports FileSystemWatcher-based triggers for IDE file saves.
/// See architecture.md §3.6 and §5.5.
/// </summary>
public interface IIncrementalSync : IDisposable
{
    /// <summary>
    /// Compute git diff between two commits and update the graph.
    /// </summary>
    Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a FileSystemWatcher on the repository root.
    /// Watches for file saves (.cs, .ps1, .psm1, .psd1) with
    /// <see cref="MemoryOptions.IncrementalSyncDebounceMs"/> debounce.
    /// See architecture.md §3.6.
    /// </summary>
    void StartFileWatcher(string repoRoot);

    /// <summary>
    /// Stops the file system watcher if running.
    /// </summary>
    void StopFileWatcher();
}
