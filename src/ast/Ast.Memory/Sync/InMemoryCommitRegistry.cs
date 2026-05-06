// -----------------------------------------------------------------------
// <copyright file="InMemoryCommitRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Collections.Concurrent;
using Ast.Memory.Interfaces;

/// <summary>
/// In-memory implementation of <see cref="ICommitRegistry"/> for single-process
/// usage and unit/integration testing. Thread-safe via ConcurrentDictionary.
/// Production deployments back this with a durable commits table (§3.2.3).
/// </summary>
public sealed class InMemoryCommitRegistry : ICommitRegistry
{
    private readonly ConcurrentDictionary<string, int> shaToIndex = new(StringComparer.OrdinalIgnoreCase);
    private int nextIndex;

    /// <summary>
    /// Initializes a new instance with the given starting index.
    /// The first call to <see cref="RegisterCommitAsync"/> returns <paramref name="startIndex"/>.
    /// </summary>
    public InMemoryCommitRegistry(int startIndex = 1)
    {
        // Interlocked.Increment returns startIndex on the first call
        // because it pre-increments from (startIndex - 1).
        this.nextIndex = startIndex - 1;
    }

    /// <inheritdoc />
    public Task<int> RegisterCommitAsync(string commitSha, string? message = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(commitSha);

        var index = this.shaToIndex.GetOrAdd(commitSha, _ => Interlocked.Increment(ref this.nextIndex));
        return Task.FromResult(index);
    }

    /// <summary>
    /// Returns all registered commits (for testing/diagnostics).
    /// </summary>
    public IReadOnlyDictionary<string, int> GetAll() => this.shaToIndex;
}