// -----------------------------------------------------------------------
// <copyright file="SharedRepoGitGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Git;
using Xunit;

/// <summary>
/// Regression coverage for the SharedRepoGitGate retry policy + lock-key
/// resolver. Both reported orchestrator halts on 2026-05-28 (15:25:22 UTC
/// AST-PARSER-FOR-ADDIT and 16:09:36 UTC CLEAN-CODE) carried the exact
/// stderr lines asserted below in <see cref="IsTransientLockError_Matches_PackedRefsRace"/>
/// and <see cref="IsTransientLockError_Matches_DidNotSendAllNecessaryObjects"/>;
/// the 8.2 stall at 10:46:51 UTC carried the index.lock line asserted in
/// <see cref="IsTransientLockError_Matches_IndexLock"/>. The non-match
/// tests guard against the gate masking real failures (SSH auth denied,
/// missing branches, network errors).
/// </summary>
public class SharedRepoGitGateTests
{
    // ── ResolveCloneRootKey ───────────────────────────────────────────────

    [Fact]
    public void ResolveCloneRootKey_Strips_Worktree_Suffix()
    {
        var key = SharedRepoGitGate.ResolveCloneRootKey(
            @"E:\forge\code-intelligence\.worktree\stage-8.2-refactor-plan-and-task-generation");
        Assert.Equal(@"e:\forge\code-intelligence", key);
    }

    [Fact]
    public void ResolveCloneRootKey_Strips_Worktree_Suffix_With_Subpath()
    {
        var key = SharedRepoGitGate.ResolveCloneRootKey(
            @"E:\forge\code-intelligence\.worktree\stage-8.2\services\clean-code\docs");
        Assert.Equal(@"e:\forge\code-intelligence", key);
    }

    [Fact]
    public void ResolveCloneRootKey_Without_Worktree_Returns_Normalized_CloneRoot()
    {
        var key = SharedRepoGitGate.ResolveCloneRootKey(@"E:\forge\code-intelligence");
        Assert.Equal(@"e:\forge\code-intelligence", key);
    }

    [Fact]
    public void ResolveCloneRootKey_Worktrees_Of_Same_Clone_Collide()
    {
        var a = SharedRepoGitGate.ResolveCloneRootKey(
            @"E:\forge\code-intelligence\.worktree\stage-1.5-normalizehints");
        var b = SharedRepoGitGate.ResolveCloneRootKey(
            @"E:\forge\code-intelligence\.worktree\stage-8.2-refactor");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveCloneRootKey_Different_Clones_Do_Not_Collide()
    {
        var a = SharedRepoGitGate.ResolveCloneRootKey(@"E:\forge\code-intelligence");
        var b = SharedRepoGitGate.ResolveCloneRootKey(@"E:\forge\update");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ResolveCloneRootKey_Empty_Returns_Empty()
    {
        Assert.Equal(string.Empty, SharedRepoGitGate.ResolveCloneRootKey(string.Empty));
    }

    // ── IsTransientLockError ──────────────────────────────────────────────

    [Fact]
    public void IsTransientLockError_Zero_Exit_Never_Matches()
    {
        // Even if stderr happens to mention the tokens (e.g. log output
        // from a successful command), exit 0 must not loop.
        Assert.False(SharedRepoGitGate.IsTransientLockError(0,
            "warning: packed-refs Permission denied (cosmetic)"));
    }

    [Fact]
    public void IsTransientLockError_Matches_PackedRefsRace()
    {
        // Exact stderr line from spawn-code-intelligence-AST-PARSER-FOR-ADDIT
        // LastError @ 2026-05-28 15:25:22 UTC and CLEAN-CODE @ 16:09:36 UTC.
        const string Stderr =
            "fatal: couldn't read E:/forge/code-intelligence/.git/packed-refs: Permission denied";
        Assert.True(SharedRepoGitGate.IsTransientLockError(1, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Matches_DidNotSendAllNecessaryObjects()
    {
        // Downstream symptom of the packed-refs race. GitHub rejects the
        // truncated want/have list. Same root cause; retry succeeds.
        const string Stderr =
            "error: https://github.com/smartpcr/code-intelligence.git did not send all necessary objects";
        Assert.True(SharedRepoGitGate.IsTransientLockError(1, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Matches_IndexLock()
    {
        // Exact stderr from the 8.2 stall @ 2026-05-28 10:46:51 UTC when
        // ContinueMergeAndPushAsync ran `git add` 420 ms after the
        // P79c conflict-resolve agent's last subprocess exited.
        const string Stderr =
            "fatal: Unable to create 'E:/forge/code-intelligence/.git/worktrees/stage-8.2-refactor-plan-and-task-generation/index.lock': File exists.";
        Assert.True(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Matches_AnotherGitProcessRunning()
    {
        const string Stderr =
            "Another git process seems to be running in this repository, e.g.\nan editor opened by 'git commit'.";
        Assert.True(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Does_Not_Match_Ssh_Auth_Denied()
    {
        // "Permission denied" alone (no packed-refs token) is auth, not
        // a file-lock race. Must NOT retry — credentials won't change.
        const string Stderr = "Permission denied (publickey).\nfatal: Could not read from remote repository.";
        Assert.False(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Does_Not_Match_Branch_Not_Found()
    {
        const string Stderr = "fatal: couldn't find remote ref refs/heads/ws/missing-branch";
        Assert.False(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Does_Not_Match_Network_Error()
    {
        const string Stderr = "fatal: unable to access 'https://github.com/x.git/': Could not resolve host: github.com";
        Assert.False(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Empty_Stderr_Does_Not_Match()
    {
        Assert.False(SharedRepoGitGate.IsTransientLockError(1, string.Empty));
        Assert.False(SharedRepoGitGate.IsTransientLockError(1, null));
    }

    [Fact]
    public void IsTransientLockError_Matches_GitconfigSharingViolation()
    {
        // GCM rewrites the per-clone global .gitconfig (azrepos hints) atomically;
        // a concurrent reader hits ERROR_SHARING_VIOLATION → exit 128.
        const string Stderr =
            "warning: unable to access 'C:/Users/x/AppData/Local/Forge/git-home/update-2b0cd04364e6/.gitconfig': Permission denied";
        Assert.True(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    [Fact]
    public void IsTransientLockError_Matches_ConfigReadFatal()
    {
        const string Stderr = "fatal: unknown error occurred while reading the configuration files";
        Assert.True(SharedRepoGitGate.IsTransientLockError(128, Stderr));
    }

    // ── ShouldRetryNetworkOp (empty-stderr network abort) ─────────────────

    [Fact]
    public void ShouldRetryNetworkOp_Empty_Stderr_NetworkOp_Retries()
    {
        // The 2026-07-01 halt fingerprint: `git fetch origin main` exit 128 with
        // empty stdout+stderr. IsTransientLockError can't match it (empty
        // stderr), but a network op is safe to re-attempt.
        Assert.True(SharedRepoGitGate.ShouldRetryNetworkOp(128, string.Empty, isNetworkOp: true));
        Assert.True(SharedRepoGitGate.ShouldRetryNetworkOp(128, null, isNetworkOp: true));
    }

    [Fact]
    public void ShouldRetryNetworkOp_Empty_Stderr_NonNetworkOp_Does_Not_Retry()
    {
        // A local op (status/commit/checkout) that fails with no stderr is not
        // a transient network abort — don't blindly loop it.
        Assert.False(SharedRepoGitGate.ShouldRetryNetworkOp(128, string.Empty, isNetworkOp: false));
    }

    [Fact]
    public void ShouldRetryNetworkOp_Zero_Exit_Never_Retries()
    {
        Assert.False(SharedRepoGitGate.ShouldRetryNetworkOp(0, string.Empty, isNetworkOp: true));
    }

    [Fact]
    public void ShouldRetryNetworkOp_Genuine_Network_Error_Does_Not_Retry()
    {
        // A real network/auth failure writes a diagnostic to stderr, so it is
        // NOT the empty-stderr fingerprint and must not retry (creds/DNS won't
        // change within the backoff budget).
        const string Stderr = "fatal: unable to access 'https://github.com/x.git/': Could not resolve host: github.com";
        Assert.False(SharedRepoGitGate.ShouldRetryNetworkOp(128, Stderr, isNetworkOp: true));
    }

    [Fact]
    public void ShouldRetryNetworkOp_Delegates_To_LockError_For_NonEmpty_Stderr()
    {
        // Lock-race signatures still retry regardless of the network-op flag.
        const string PackedRefs =
            "fatal: couldn't read E:/forge/update/.git/packed-refs: Permission denied";
        Assert.True(SharedRepoGitGate.ShouldRetryNetworkOp(1, PackedRefs, isNetworkOp: false));
    }

    // ── RetryDelayMs (exponential backoff) ─────────────────────────────────

    [Fact]
    public void RetryDelayMs_Attempt0_Is_Zero()
    {
        Assert.Equal(0, SharedRepoGitGate.RetryDelayMs(0));
    }

    [Fact]
    public void RetryDelayMs_Doubles_Up_To_Cap()
    {
        // Sequence: attempt 1=50, 2=100, 3=200, 4=400, 5=800 (cap).
        Assert.Equal(50, SharedRepoGitGate.RetryDelayMs(1));
        Assert.Equal(100, SharedRepoGitGate.RetryDelayMs(2));
        Assert.Equal(200, SharedRepoGitGate.RetryDelayMs(3));
        Assert.Equal(400, SharedRepoGitGate.RetryDelayMs(4));
        Assert.Equal(SharedRepoGitGate.MaxBackoffMs, SharedRepoGitGate.RetryDelayMs(5));
    }

    [Fact]
    public void RetryDelayMs_Beyond_MaxAttempts_Stays_Capped()
    {
        // Defensive: even if a caller passes a higher attempt number,
        // the delay must not overflow or exceed the cap.
        Assert.Equal(SharedRepoGitGate.MaxBackoffMs, SharedRepoGitGate.RetryDelayMs(20));
    }

    [Fact]
    public void RetryDelayMs_TotalBudget_Is_Bounded()
    {
        // Sum of all delays for MaxRetryAttempts must be < 2 seconds so
        // a hot caller doesn't slow down by more than ~2 s on a true
        // non-transient failure path.
        var total = 0;
        for (var i = 0; i < SharedRepoGitGate.MaxRetryAttempts; i++)
        {
            total += SharedRepoGitGate.RetryDelayMs(i);
        }
        Assert.True(total < 2_000, $"Total backoff was {total} ms; expected < 2000.");
    }

    // ── AcquireAsync (serialisation) ──────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_Same_Key_Serialises_Concurrent_Callers()
    {
        var key = SharedRepoGitGate.ResolveCloneRootKey($@"E:\test-{Guid.NewGuid():N}");
        var concurrentInside = 0;
        var maxConcurrent = 0;
        var sync = new object();

        async Task RunOne()
        {
            var sem = await SharedRepoGitGate.AcquireAsync(key, CancellationToken.None);
            try
            {
                lock (sync)
                {
                    concurrentInside++;
                    if (concurrentInside > maxConcurrent) maxConcurrent = concurrentInside;
                }
                await Task.Delay(25);
                lock (sync) { concurrentInside--; }
            }
            finally
            {
                sem.Release();
            }
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => RunOne()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task AcquireAsync_Different_Keys_Do_Not_Serialise()
    {
        var keyA = SharedRepoGitGate.ResolveCloneRootKey($@"E:\test-A-{Guid.NewGuid():N}");
        var keyB = SharedRepoGitGate.ResolveCloneRootKey($@"E:\test-B-{Guid.NewGuid():N}");

        var semA = await SharedRepoGitGate.AcquireAsync(keyA, CancellationToken.None);
        try
        {
            // Holding A must NOT block B. Use a tight timeout: if the
            // gate is wrongly process-global, this acquire will hang.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var semB = await SharedRepoGitGate.AcquireAsync(keyB, cts.Token);
            semB.Release();
        }
        finally
        {
            semA.Release();
        }
    }

    // ── RunWithNetworkTimeoutRetryAsync (network timeout self-heal) ────────

    [Fact]
    public async Task NetworkTimeoutRetry_Succeeds_On_First_Try_No_Retry()
    {
        var calls = 0;
        var retries = 0;
        var result = await SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync(
            () => { calls++; return Task.FromResult(42); },
            maxExtraRetries: 2,
            delayMs: 0,
            ct: CancellationToken.None,
            onRetry: (_, _) => retries++);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
        Assert.Equal(0, retries);
    }

    [Fact]
    public async Task NetworkTimeoutRetry_Recovers_After_Transient_Timeouts()
    {
        // Models the reported halt: the first attempt(s) trip the 180s
        // timeout (TimeoutException), a retry reconnects in seconds.
        var calls = 0;
        var retries = 0;
        var result = await SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync<string>(
            () =>
            {
                calls++;
                if (calls < 3) throw new TimeoutException("git fetch timed out after 180s");
                return Task.FromResult("ok");
            },
            maxExtraRetries: 2,
            delayMs: 0,
            ct: CancellationToken.None,
            onRetry: (_, _) => retries++);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);   // 1 original + 2 retries
        Assert.Equal(2, retries);
    }

    [Fact]
    public async Task NetworkTimeoutRetry_Rethrows_After_Budget_Exhausted()
    {
        // A sustained outage exhausts the budget and rethrows the final
        // TimeoutException so the caller's existing halt path runs.
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync<int>(
                () => { calls++; throw new TimeoutException("still stuck"); },
                maxExtraRetries: 2,
                delayMs: 0,
                ct: CancellationToken.None));

        Assert.Equal(3, calls);   // 1 original + 2 retries, then rethrow
    }

    [Fact]
    public async Task NetworkTimeoutRetry_Does_Not_Retry_NonTimeout_Exception()
    {
        // Lock-race / auth / branch-not-found failures are returned as
        // non-zero results, not thrown; a thrown non-timeout exception
        // (e.g. a real bug) must surface immediately without retry.
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync<int>(
                () => { calls++; throw new InvalidOperationException("boom"); },
                maxExtraRetries: 2,
                delayMs: 0,
                ct: CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NetworkTimeoutRetry_Stops_Retrying_When_Cancelled()
    {
        // A cancelled token must short-circuit the retry guard so a
        // shutting-down worker isn't pinned re-attempting a doomed op.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync<int>(
                () => { calls++; throw new TimeoutException("timed out"); },
                maxExtraRetries: 2,
                delayMs: 0,
                ct: cts.Token));

        Assert.Equal(1, calls);   // guard `!ct.IsCancellationRequested` blocks retry
    }
}
