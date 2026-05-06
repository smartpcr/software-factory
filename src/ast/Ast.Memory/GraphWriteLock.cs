// -----------------------------------------------------------------------
// <copyright file="GraphWriteLock.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.Memory.Interfaces;

/// <summary>
/// Process-wide write lock for graph store mutations.
/// Uses <see cref="SemaphoreSlim"/>(1,1) to serialize all writes —
/// ConsolidateAsync, RecordExpertiseAsync, ApplyDiffAsync,
/// DecayStaleExpertiseAsync — per architecture.md §3.7.
/// Registered as singleton in DI so all writers share the same instance.
/// </summary>
public sealed class GraphWriteLock : IGraphWriteLock, IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    /// <inheritdoc/>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await this.semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new LockRelease(this.semaphore);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.semaphore.Dispose();
    }

    private sealed class LockRelease : IDisposable
    {
        private SemaphoreSlim? semaphore;

        public LockRelease(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref this.semaphore, null)?.Release();
        }
    }
}