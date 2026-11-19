// -----------------------------------------------------------------------
// <copyright file="CopilotSessionPool.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections.Concurrent;
using GitHub.Copilot;

/// <summary>
/// Phase 55 / Phase E (docs/forge/sloppy_guard.md) -- cache of live
/// <see cref="CopilotSession"/> instances keyed by
/// <c>(workItemId, agentId)</c>. Reused across multiple
/// <c>SendAsync</c> calls within one gen-eval loop so iter N+1
/// inherits iter N's in-session conversation history; without the
/// pool, <c>AgentRunnerDispatcher.RunCopilotSdk</c> creates and
/// disposes a fresh session on every <c>IAgentRunner.StartAsync</c>
/// call and the Copilot side has the same "fresh context each turn"
/// problem the Claude side already solves with <c>--resume</c> (CLI)
/// and message-history replay (SDK).
///
/// <para><b>Lifecycle.</b> Entries live until the caller invokes
/// <see cref="DisposeForWorkItemAsync"/> -- which
/// <c>CrewStoryArtifactStrategy.RunGenEvalLoopAsync</c> does in its
/// try/finally so a terminal verdict (pass / reject / sloppy /
/// stalled / cancel) reaps the session. Process shutdown disposes
/// any straggling entries via <see cref="DisposeAsync"/>.</para>
///
/// <para><b>Concurrency.</b> The Copilot SDK isn't documented for
/// concurrent <c>SendAsync</c> calls on a single session. Each
/// pooled entry carries a <see cref="SemaphoreSlim"/> the caller
/// uses to serialise its calls. The gen-eval loop is naturally
/// serial (generator then evaluator then next iter), so this lock
/// is defence in depth -- but it also blocks the rare case where
/// two orchestrator ticks race the same workstream.</para>
///
/// <para><b>Restart resilience -- not provided.</b> The live session
/// only exists in process memory. If Forge.Api restarts, the pool
/// loses everything; the next iter for an affected workstream
/// creates a fresh session and the Copilot-side conversation
/// history is gone. Mitigating that would require persisting the
/// session id and a server-side resume primitive the Copilot SDK
/// doesn't currently expose. Operator restart of Forge.Api remains
/// a rare event so the trade-off lands on "in-memory only" for
/// the v1 of this pool.</para>
/// </summary>
public sealed class CopilotSessionPool : IAsyncDisposable
{
    private readonly ILogger<CopilotSessionPool> log;
    private readonly ConcurrentDictionary<(string WorkItemId, string AgentId), Lazy<Task<PooledCopilotEntry>>> entries
        = new();

    public CopilotSessionPool(ILogger<CopilotSessionPool> log)
    {
        this.log = log;
    }

    /// <summary>
    /// Get-or-create the pooled entry for a <c>(workItemId, agentId)</c>
    /// tuple. The factory is invoked exactly once per key (on cache
    /// miss); subsequent callers receive the same
    /// <see cref="CopilotSession"/> and must serialise their
    /// <c>SendAsync</c> calls on
    /// <see cref="PooledCopilotEntry.SendLock"/>.
    ///
    /// <para>Phase 74 — every acquire bumps
    /// <see cref="PooledCopilotEntry.RefCount"/>. Callers MUST pair
    /// each acquire with exactly one <see cref="ReleaseAsync"/> call
    /// (typically in a <c>finally</c> block) so the entry's lifecycle
    /// follows reference counts. The entry is disposed and removed
    /// from the dictionary only when the count reaches zero. Without
    /// this, a concurrent <c>RunAsync</c> on the same workItem could
    /// dispose the shared session out from under the in-flight
    /// caller and surface as <c>generator-inactive</c> 10 min later.
    /// </para>
    /// </summary>
    /// <param name="factory">
    /// Builds the underlying <see cref="CopilotClient"/> +
    /// <see cref="CopilotSession"/> pair. The factory owns
    /// <c>SessionConfig</c> construction so the pool stays decoupled
    /// from <c>AgentContext</c> shape. Invoked at most once per
    /// cache miss, then cached.
    /// </param>
    public async Task<PooledCopilotEntry> AcquireAsync(
        string workItemId,
        string agentId,
        Func<CancellationToken, Task<(CopilotClient Client, CopilotSession Session)>> factory,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) throw new ArgumentException("workItemId required", nameof(workItemId));
        if (string.IsNullOrEmpty(agentId)) throw new ArgumentException("agentId required", nameof(agentId));

        var key = (workItemId, agentId);
        var lazy = this.entries.GetOrAdd(key, _ => new Lazy<Task<PooledCopilotEntry>>(async () =>
        {
            this.log.LogInformation(
                "CopilotSessionPool: creating session for ({WorkItem}, {Agent})",
                workItemId, agentId);
            var (client, session) = await factory(ct);
            return new PooledCopilotEntry(client, session, workItemId, agentId);
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var entry = await lazy.Value;
            // Phase 74 — every Acquire bumps the refcount. Pair with
            // ReleaseAsync. Throws if the entry has been finalised by
            // a concurrent ReleaseAsync racing to zero — caller must
            // retry, which will create a fresh entry via the
            // ConcurrentDictionary GetOrAdd path.
            entry.IncrementRefCount();
            return entry;
        }
        catch (ObjectDisposedException)
        {
            // Entry was disposed between our lazy.Value resolve and our
            // IncrementRefCount call. Drop the failed Lazy from the
            // dictionary so the next caller re-creates a fresh entry,
            // then retry once.
            this.entries.TryRemove(key, out _);
            throw;
        }
        catch
        {
            // Factory threw -- drop the failed Lazy so the next caller
            // retries with a fresh factory instead of caching the throw.
            this.entries.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>
    /// Phase 74 — decrement the refcount on the entry for
    /// <c>(workItemId, agentId)</c>. When the count reaches zero the
    /// entry is disposed and removed from the dictionary. Idempotent
    /// on missing entries (returns false). Safe to call from any
    /// <c>finally</c> that paired with <see cref="AcquireAsync"/>.
    /// </summary>
    public async Task<bool> ReleaseAsync(string workItemId, string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId) || string.IsNullOrEmpty(agentId)) return false;
        var key = (workItemId, agentId);
        if (!this.entries.TryGetValue(key, out var lazy)) return false;

        PooledCopilotEntry entry;
        try { entry = await lazy.Value; }
        catch { return false; }

        var shouldDispose = entry.DecrementRefCount();
        if (shouldDispose)
        {
            this.entries.TryRemove(key, out _);
            try { await entry.DisposeAsync(); }
            catch (Exception ex)
            {
                this.log.LogWarning(ex,
                    "CopilotSessionPool: dispose-after-release failed for ({WorkItem}, {Agent}); leaking entry.",
                    workItemId, agentId);
            }
        }
        return shouldDispose;
    }

    /// <summary>
    /// Phase 74 — soft signal that the caller has finished using the
    /// session pool for this work item. Decrements the refcount on
    /// every <c>(workItemId, *)</c> entry; entries whose count hits
    /// zero are disposed. <strong>NOT</strong> a force-dispose;
    /// in-flight concurrent callers keep the session alive until
    /// they call <see cref="ReleaseAsync"/>. Backward-compatible
    /// signature so existing call sites (single caller in
    /// <c>CrewStoryArtifactStrategy.RunGenEvalLoopAsync</c>'s
    /// finally) don't need source changes.
    ///
    /// <para>Use <see cref="DisposeForWorkItemForceAsync"/> for the
    /// force-disposal semantic (operator cancel / restart paths
    /// that explicitly want the session torn down).</para>
    /// </summary>
    public async Task<int> DisposeForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return 0;
        var matched = this.entries
            .Where(kv => string.Equals(kv.Key.WorkItemId, workItemId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        var released = 0;
        foreach (var key in matched)
        {
            if (await this.ReleaseAsync(key.WorkItemId, key.AgentId, ct).ConfigureAwait(false))
            {
                released++;
            }
        }
        return released;
    }

    /// <summary>
    /// Phase 74 — force-dispose every entry for a work item regardless
    /// of refcount. Used by operator-cancel / restart / shutdown paths
    /// that explicitly want the in-flight session torn down.
    /// </summary>
    public async Task<int> DisposeForWorkItemForceAsync(string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return 0;
        var matched = this.entries
            .Where(kv => string.Equals(kv.Key.WorkItemId, workItemId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        var disposed = 0;
        foreach (var key in matched)
        {
            if (this.entries.TryRemove(key, out var lazy))
            {
                try
                {
                    var entry = await lazy.Value;
                    await entry.DisposeAsync();
                    disposed++;
                }
                catch (Exception ex)
                {
                    this.log.LogWarning(ex,
                        "CopilotSessionPool: force-dispose failed for ({WorkItem}, {Agent}); leaking entry.",
                        key.WorkItemId, key.AgentId);
                }
            }
        }

        if (disposed > 0)
        {
            this.log.LogInformation(
                "CopilotSessionPool: force-disposed {Count} session(s) for {WorkItem}.",
                disposed, workItemId);
        }
        return disposed;
    }

    public async ValueTask DisposeAsync()
    {
        var snapshot = this.entries.ToArray();
        this.entries.Clear();
        foreach (var kv in snapshot)
        {
            try
            {
                var entry = await kv.Value.Value;
                await entry.DisposeAsync();
            }
            catch (Exception ex)
            {
                this.log.LogWarning(ex,
                    "CopilotSessionPool: dispose during shutdown failed for ({WorkItem}, {Agent}).",
                    kv.Key.WorkItemId, kv.Key.AgentId);
            }
        }
    }
}

/// <summary>
/// Phase 55 / Phase E -- one cached <see cref="CopilotSession"/>
/// plus the <see cref="CopilotClient"/> that owns it. The lock
/// must be held while a <c>SendAsync</c> is in flight.
///
/// <para>Phase 74 — <see cref="RefCount"/> is bumped by every
/// <see cref="CopilotSessionPool.AcquireAsync"/> call and dropped
/// by every <see cref="CopilotSessionPool.ReleaseAsync"/> call.
/// The entry is only disposed when the count reaches zero, so a
/// concurrent caller's reference keeps the underlying SDK session
/// alive even after the originating gen-eval loop's
/// <c>finally</c> has called <c>DisposeForWorkItemAsync</c>.</para>
/// </summary>
public sealed class PooledCopilotEntry : IAsyncDisposable
{
    private int refCount;
    private bool disposed;
    private readonly object refLock = new();

    public PooledCopilotEntry(CopilotClient client, CopilotSession session, string workItemId, string agentId)
    {
        this.Client = client;
        this.Session = session;
        this.WorkItemId = workItemId;
        this.AgentId = agentId;
        this.SendLock = new SemaphoreSlim(1, 1);
    }

    public CopilotClient Client { get; }

    public CopilotSession Session { get; }

    public string WorkItemId { get; }

    public string AgentId { get; }

    /// <summary>Serialises <see cref="CopilotSession.SendAsync"/> calls.</summary>
    public SemaphoreSlim SendLock { get; }

    /// <summary>
    /// Phase 74 — diagnostic read-only view of the current reference
    /// count. Production code never branches on this; tests use it
    /// to pin the refcount semantics.
    /// </summary>
    public int RefCount
    {
        get { lock (this.refLock) { return this.refCount; } }
    }

    /// <summary>Phase 74 — bump the refcount on Acquire. Throws if the entry is already disposed.</summary>
    internal void IncrementRefCount()
    {
        lock (this.refLock)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PooledCopilotEntry),
                    $"PooledCopilotEntry for ({this.WorkItemId}, {this.AgentId}) was disposed; caller must retry to allocate a fresh entry.");
            }
            this.refCount++;
        }
    }

    /// <summary>
    /// Phase 74 — decrement the refcount on Release. Returns true
    /// when the count has reached zero and the caller should
    /// dispose the entry + remove it from the pool dictionary.
    /// Idempotent under-zero (clamps to zero, returns false).
    /// </summary>
    internal bool DecrementRefCount()
    {
        lock (this.refLock)
        {
            if (this.disposed) return false;
            if (this.refCount <= 0) return false;
            this.refCount--;
            if (this.refCount == 0)
            {
                this.disposed = true;
                return true;
            }
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Phase 74 — mark disposed under the lock so a racing
        // IncrementRefCount on the same entry sees the disposal and
        // throws. Releases on a disposed entry are no-ops.
        lock (this.refLock)
        {
            if (this.disposed) return;
            this.disposed = true;
        }

        try { await this.Session.DisposeAsync(); }
        catch { /* best-effort */ }

        try { await this.Client.DisposeAsync(); }
        catch { /* best-effort */ }

        this.SendLock.Dispose();
    }
}
