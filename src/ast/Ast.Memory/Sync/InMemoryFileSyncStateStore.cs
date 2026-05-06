// -----------------------------------------------------------------------
// <copyright file="InMemoryFileSyncStateStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Collections.Concurrent;
using Ast.Memory.Interfaces;

/// <summary>
/// In-memory implementation of <see cref="IFileSyncStateStore"/>.
/// Tracks last-synced ContentHash per file to avoid redundant re-extraction.
/// </summary>
public sealed class InMemoryFileSyncStateStore : IFileSyncStateStore
{
    private readonly ConcurrentDictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public Task<string?> GetContentHashAsync(string relativePath, CancellationToken ct = default)
    {
        this.hashes.TryGetValue(relativePath, out var hash);
        return Task.FromResult(hash);
    }

    /// <inheritdoc/>
    public Task SetContentHashAsync(string relativePath, string contentHash, CancellationToken ct = default)
    {
        this.hashes[relativePath] = contentHash;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string relativePath, CancellationToken ct = default)
    {
        this.hashes.TryRemove(relativePath, out _);
        return Task.CompletedTask;
    }
}