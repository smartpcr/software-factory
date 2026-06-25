// -----------------------------------------------------------------------
// <copyright file="SharedRepoGitGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Process-wide serialization gate + transient-lock retry policy for git
/// commands that touch a CLONE-SHARED file (chiefly <c>.git/packed-refs</c>
/// and <c>.git/HEAD</c>). Windows uses mandatory file locks; when two git
/// processes concurrently read/write <c>packed-refs</c> the loser sees
/// <c>ERROR_SHARING_VIOLATION</c> which git surfaces as:
/// <code>
///   fatal: couldn't read .../.git/packed-refs: Permission denied
///   error: ... did not send all necessary objects
/// </code>
/// (the second line is a downstream symptom — the want/have negotiation
/// is built from a truncated ref read, so the remote rejects the partial
/// request).
///
/// <para>This was the root cause of two near-simultaneous orchestrator
/// halts on 2026-05-28: <c>spawn-code-intelligence-AST-PARSER-FOR-ADDIT</c>
/// at 15:25:22 UTC and <c>spawn-code-intelligence-CLEAN-CODE</c> at
/// 16:09:36 UTC. Both spawns ran <c>git fetch origin main</c> against the
/// same shared <c>E:\forge\code-intelligence\.git</c> directory; they
/// raced on <c>packed-refs</c> and each halted its spawn with the banner
/// "ORCHESTRATOR HALTED — implement failed: git fetch origin main failed
/// (exit 1) ... Permission denied ... did not send all necessary objects".
/// The related <c>index.lock: File exists</c> stall on the 8.2 workstream
/// at 10:46:51 UTC is the same race family at the per-worktree index.</para>
///
/// <para><b>Lock granularity.</b> One semaphore per CLONE-ROOT (NOT per
/// worktree, NOT per branch). Worktrees of the same clone share the same
/// physical <c>.git</c> commondir, so a fetch from worktree A blocks a
/// fetch from worktree B of the same clone. Branch-level locks (Phase 42
/// <c>branchLocks</c>) live alongside this gate and serve a different
/// purpose (serialising staging/commit on a shared worktree); they do
/// NOT prevent the packed-refs race.</para>
///
/// <para><b>Retry policy.</b> Transient lock failures (the three
/// signatures in <see cref="IsTransientLockError"/>) self-heal in tens
/// of milliseconds because git releases the lock file as soon as its
/// subprocess exits. We retry up to <see cref="MaxRetryAttempts"/> with
/// exponential backoff capped at <see cref="MaxBackoffMs"/>. Total
/// worst-case wait before re-failing is ~1.5 s — long enough to clear
/// a leftover lock from an interleaved subprocess, short enough that
/// genuine non-transient failures don't get delayed in production. The
/// gate is intentionally optimistic: it serialises the FIRST attempt,
/// then retries IGNORING the serialiser so a stuck holder can't pin
/// downstream callers (the retry will hit the same gate on the next
/// attempt; that's fine).</para>
///
/// <para><b>What this does NOT do.</b> This gate does NOT retry network
/// errors (<c>Could not resolve host</c>, TLS, auth-denied), real merge
/// conflicts, or branch-not-found. Only the three Windows file-lock
/// signatures. Authentication denials (e.g. <c>Permission denied
/// (publickey)</c>) deliberately do NOT match because the path-bearing
/// substring <c>packed-refs</c> is absent.</para>
/// </summary>
internal static class SharedRepoGitGate
{
    /// <summary>Number of attempts (including the first) before giving up.</summary>
    internal const int MaxRetryAttempts = 5;

    /// <summary>Initial backoff in milliseconds; doubles each attempt up to <see cref="MaxBackoffMs"/>.</summary>
    internal const int InitialBackoffMs = 50;

    /// <summary>Cap on the per-attempt backoff so the total wait stays bounded.</summary>
    internal const int MaxBackoffMs = 800;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a stable per-clone-root key for serialization. Strips the
    /// Forge worktree suffix <c>\.worktree\{slug}</c> if present so every
    /// worktree of the same clone collapses to one key. Falls back to the
    /// normalized full path if the input is the clone root itself or any
    /// other directory.
    /// </summary>
    public static string ResolveCloneRootKey(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(cwd); }
        catch { full = cwd; }
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Convention: worktrees live at "{cloneRoot}\.worktree\{slug}\..."
        // (see RealStoryGitOps class docstring). Trim that suffix so every
        // worktree of the same clone shares the gate.
        var marker = $"{Path.DirectorySeparatorChar}.worktree{Path.DirectorySeparatorChar}";
        var idx = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx > 0) full = full.Substring(0, idx);

        return full.ToLowerInvariant();
    }

    /// <summary>
    /// Acquire the per-clone-root semaphore. Caller MUST <see cref="SemaphoreSlim.Release()"/>
    /// in a finally block. Returns the semaphore so the caller's <c>using</c> /
    /// <c>try/finally</c> is explicit at the call site.
    /// </summary>
    public static async Task<SemaphoreSlim> AcquireAsync(string cloneRootKey, CancellationToken ct)
    {
        var sem = RepoLocks.GetOrAdd(cloneRootKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return sem;
    }

    /// <summary>
    /// True if (exitCode, stderr) match a transient Windows file-lock
    /// race that is safe to retry. See class docstring for the exact
    /// signatures. Non-zero exit is required so a successful command
    /// whose stderr happens to mention these tokens does not loop.
    /// </summary>
    public static bool IsTransientLockError(int exitCode, string? stderr)
    {
        if (exitCode == 0) return false;
        if (string.IsNullOrEmpty(stderr)) return false;
        // 1. packed-refs read race during concurrent fetch / pack-refs
        if (stderr.Contains("packed-refs", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 2. Downstream symptom of #1: want/have negotiation built from a
        //    truncated ref list; GitHub / ADO reject the partial request.
        if (stderr.Contains("did not send all necessary objects", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 3. Per-worktree index.lock race (e.g. P79c conflict-resolve
        //    agent's last git subprocess hadn't fully released the lock
        //    before ContinueMergeAndPushAsync ran `git add`).
        if (stderr.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("File exists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 4. Generic "another git process is running" — git's own banner
        //    when it detects a foreign lock file. Same root cause family.
        if (stderr.Contains("Another git process seems to be running", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Exponential backoff: 50, 100, 200, 400, 800 (capped). Attempt is
    /// zero-based — the delay BEFORE attempt N. Attempt 0 returns 0.
    /// </summary>
    public static int RetryDelayMs(int attempt)
    {
        if (attempt <= 0) return 0;
        var ms = InitialBackoffMs;
        for (var i = 1; i < attempt; i++)
        {
            ms *= 2;
            if (ms >= MaxBackoffMs) return MaxBackoffMs;
        }
        return ms;
    }

    /// <summary>
    /// Extra retries granted to a network git op (fetch/push/clone/ls-remote)
    /// whose per-call timeout is THROWN as <see cref="TimeoutException"/> rather
    /// than returned. Without a retry that exception escapes the lock-race loop
    /// and HALTS the whole orchestrator on a one-off broker / DNS stall
    /// (observed 2026-06-24: a single `git fetch origin &lt;branch&gt;` stalled
    /// 180s in C:\forge\asz-common and halted the workstream, yet a manual
    /// retry reconnected in ~15s). Kept tiny because each attempt can cost up
    /// to FORGE_GIT_NETWORK_TIMEOUT_SECONDS.
    /// </summary>
    internal const int NetworkTimeoutRetryAttempts = 2;

    /// <summary>Backoff before re-attempting after a network timeout.</summary>
    internal const int NetworkTimeoutRetryDelayMs = 1500;

    /// <summary>
    /// Runs <paramref name="body"/>, retrying it up to
    /// <paramref name="maxExtraRetries"/> additional times when it throws a
    /// <see cref="TimeoutException"/>. A non-timeout exception, a cancelled
    /// token, or success short-circuits immediately. After the budget is spent
    /// the final <see cref="TimeoutException"/> is rethrown so the caller's
    /// existing halt path runs unchanged. Extracted as a pure helper so the
    /// retry policy is unit-testable without spawning a real git process.
    /// </summary>
    internal static async Task<T> RunWithNetworkTimeoutRetryAsync<T>(
        Func<Task<T>> body,
        int maxExtraRetries,
        int delayMs,
        CancellationToken ct,
        Action<TimeoutException, int>? onRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await body().ConfigureAwait(false);
            }
            catch (TimeoutException tex) when (attempt < maxExtraRetries && !ct.IsCancellationRequested)
            {
                onRetry?.Invoke(tex, attempt);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
