// -----------------------------------------------------------------------
// <copyright file="IFileSyncStateStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Interfaces;

/// <summary>
/// Tracks last-synced ContentHash per file to avoid redundant re-extraction
/// on unchanged content. Used by IncrementalSync for ContentHash skip.
/// </summary>
public interface IFileSyncStateStore
{
    /// <summary>
    /// Gets the last-synced content hash for a file path.
    /// </summary>
    /// <returns>The SHA-256 content hash, or null if never synced.</returns>
    Task<string?> GetContentHashAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Updates the last-synced content hash for a file path.
    /// </summary>
    Task SetContentHashAsync(string relativePath, string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Removes tracking for a file (e.g., when deleted).
    /// </summary>
    Task RemoveAsync(string relativePath, CancellationToken ct = default);
}
