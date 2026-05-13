// -----------------------------------------------------------------------
// <copyright file="CopilotSessionPool.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections.Concurrent;
using GitHub.Copilot.SDK;

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
            return await lazy.Value;
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
    /// Drop and dispose every pooled entry for a work item. Called
    /// from <c>RunGenEvalLoopAsync</c>'s try/finally on terminal
    /// verdict OR cancellation. Idempotent: re-disposing a work
    /// item with no entries is a no-op.
    /// </summary>
    public async Task<int> DisposeForWorkItemAsync(string workItemId, CancellationToken ct = default)
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
                        "CopilotSessionPool: dispose failed for ({WorkItem}, {Agent}); leaking entry.",
                        key.WorkItemId, key.AgentId);
                }
            }
        }

        if (disposed > 0)
        {
            this.log.LogInformation(
                "CopilotSessionPool: disposed {Count} session(s) for {WorkItem}.",
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
/// </summary>
public sealed class PooledCopilotEntry : IAsyncDisposable
{
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

    public async ValueTask DisposeAsync()
    {
        try { await this.Session.DisposeAsync(); }
        catch { /* best-effort */ }

        try { await this.Client.DisposeAsync(); }
        catch { /* best-effort */ }

        this.SendLock.Dispose();
    }
}
