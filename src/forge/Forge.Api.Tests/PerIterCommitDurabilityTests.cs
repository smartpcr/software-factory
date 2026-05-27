// -----------------------------------------------------------------------
// <copyright file="PerIterCommitDurabilityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-05-27 systemic-fix regression — <see cref="IStoryGitOps.CommitAllAndPushAsync"/>.
///
/// <para>The bug it closes: every story-spawn impl iter routed through
/// <c>CrewStoryArtifactStrategy.GeneratorDelegate</c> left the agent's
/// worktree edits uncommitted. A Copilot SDK 24-min hard timeout (or any
/// premature generator termination) then lost the work entirely because
/// <see cref="IStoryGitOps.EnsureWorktreeAsync"/>'s <c>git reset --hard
/// origin/{branch}</c> wipes the uncommitted state at the start of iter
/// N+1. The CrewQueueWorker path got this fix on 2026-05-13 (XRAFT stage
/// 3.1 root cause, $96/iter × 2 iters with zero forward progress); the
/// story-spawn path was unprotected for 2 weeks longer, observed pre-fix
/// on local:UPDATE-HISTORY-IN-CL InMemoryIndex stage: 6 work items / 8
/// generator-timeout iters / ALL with empty <c>CommitSha</c> / $481+
/// cumulative cost.</para>
///
/// <para>These tests drive a real temp git repo (bare remote + clone, same
/// fixture pattern as <c>Phase81PlanDocGitignoreTests</c>) through
/// <see cref="RealStoryGitOps.CommitAllAndPushAsync"/> to assert the
/// behavior the strategy depends on:
/// (1) dirty worktree ⇒ commit created on origin/{branch};
/// (2) clean worktree ⇒ no-op, returns <c>false</c>;
/// (3) only <c>.forge/</c> scratch dirty ⇒ scrubbed, returns <c>false</c>;
/// (4) detached HEAD ⇒ best-effort skip, returns <c>false</c>;
/// (5) non-existent worktree path ⇒ returns <c>false</c>, no throw.</para>
/// </summary>
public class PerIterCommitDurabilityTests : IAsyncLifetime
{
    private readonly List<string> tempDirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in this.tempDirs)
        {
            try
            {
                if (Directory.Exists(d))
                {
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best-effort */ }
                    }
                    Directory.Delete(d, recursive: true);
                }
            }
            catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DirtyWorktree_StagesCommitsAndPushes()
    {
        if (!GitAvailable()) return;

        var (remoteDir, cloneDir) = await CreateBareRemoteAndCloneAsync();
        var git = new RealStoryGitOps();

        // Simulate the agent's iter work: add a tracked file edit + an
        // untracked file. Both must end up in the commit.
        await File.WriteAllTextAsync(Path.Combine(cloneDir, "src", "added.cs"), "// agent's new file\n");
        await File.WriteAllTextAsync(Path.Combine(cloneDir, "README.md"), "edited\n");

        var beforeSha = await GetSha(cloneDir, "HEAD");
        var beforeRemoteSha = await GetSha(remoteDir, "main");

        var committed = await git.CommitAllAndPushAsync(
            cloneDir, "forge[gen-agent] iter 1: wi-123", CancellationToken.None);

        Assert.True(committed, "Dirty worktree should produce a commit + push.");

        var afterSha = await GetSha(cloneDir, "HEAD");
        Assert.NotEqual(beforeSha, afterSha);

        // Push landed on the bare remote.
        var afterRemoteSha = await GetSha(remoteDir, "main");
        Assert.NotEqual(beforeRemoteSha, afterRemoteSha);
        Assert.Equal(afterSha, afterRemoteSha);

        // Commit message + identity match the forge-engineer pattern.
        var msg = await RunGit(cloneDir, "log", "-1", "--format=%s|%an|%ae");
        Assert.Contains("forge[gen-agent] iter 1: wi-123", msg);
        Assert.Contains("Forge", msg);
        Assert.Contains("forge-engineer@local", msg);

        // Both files are in the commit's tree.
        var lsTree = await RunGit(cloneDir, "ls-tree", "-r", "--name-only", "HEAD");
        Assert.Contains("src/added.cs", lsTree);
        Assert.Contains("README.md", lsTree);
    }

    [Fact]
    public async Task CleanWorktree_ReturnsFalse_NoNewCommit()
    {
        if (!GitAvailable()) return;

        var (remoteDir, cloneDir) = await CreateBareRemoteAndCloneAsync();
        var git = new RealStoryGitOps();

        var beforeSha = await GetSha(cloneDir, "HEAD");

        var committed = await git.CommitAllAndPushAsync(
            cloneDir, "forge: nothing to do", CancellationToken.None);

        Assert.False(committed, "Clean worktree should be a no-op.");
        var afterSha = await GetSha(cloneDir, "HEAD");
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task OnlyForgeScratchDirty_IsScrubbed_NoCommit()
    {
        if (!GitAvailable()) return;

        var (_, cloneDir) = await CreateBareRemoteAndCloneAsync();
        var git = new RealStoryGitOps();

        // Forge scratch under .forge/ — these are Phase 55 scratch files
        // that StageAllAndScrubAsync MUST remove from the index before
        // commit. With nothing else dirty, the staged set ends up empty
        // → no commit created.
        var forgeScratch = Path.Combine(cloneDir, ".forge", "scratch");
        Directory.CreateDirectory(forgeScratch);
        await File.WriteAllTextAsync(Path.Combine(forgeScratch, "notes.txt"), "agent scratch\n");

        var beforeSha = await GetSha(cloneDir, "HEAD");

        var committed = await git.CommitAllAndPushAsync(
            cloneDir, "forge: scratch only", CancellationToken.None);

        Assert.False(committed, "Only-.forge/-dirty worktree must not produce a commit.");
        var afterSha = await GetSha(cloneDir, "HEAD");
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task DetachedHead_ReturnsFalse_NoCommit_NoThrow()
    {
        if (!GitAvailable()) return;

        var (_, cloneDir) = await CreateBareRemoteAndCloneAsync();
        var git = new RealStoryGitOps();

        // Detach HEAD onto the current tip, then dirty the worktree. The
        // helper must refuse to push (no branch name to push to) but
        // must NOT throw — best-effort semantics.
        await RunGit(cloneDir, "checkout", "--detach", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(cloneDir, "edit.txt"), "dirty\n");

        var committed = await git.CommitAllAndPushAsync(
            cloneDir, "forge: detached", CancellationToken.None);

        Assert.False(committed, "Detached HEAD must skip per-iter push.");
    }

    [Fact]
    public async Task NonExistentPath_ReturnsFalse_NoThrow()
    {
        var git = new RealStoryGitOps();
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        var committed = await git.CommitAllAndPushAsync(missing, "forge: missing", CancellationToken.None);
        Assert.False(committed);
    }

    [Fact]
    public async Task InMemoryStoryGitOps_RecordsCalls()
    {
        // The InMemoryStoryGitOps fake records every CommitAllAndPushAsync
        // call so CrewStoryArtifactStrategy unit tests can verify that the
        // strategy invokes the helper exactly once per dirty iter without
        // a real git fixture. Default return value is false (no commit
        // semantics) — tests model "agent left dirty changes" by enqueueing
        // explicit true responses via CommitAllAndPushReturnQueue.
        var fake = new InMemoryStoryGitOps();
        fake.CommitAllAndPushReturnQueue.Enqueue(true);
        fake.CommitAllAndPushReturnQueue.Enqueue(false);

        var r1 = await fake.CommitAllAndPushAsync("/work/wt-1", "iter 1: wi-A", CancellationToken.None);
        var r2 = await fake.CommitAllAndPushAsync("/work/wt-1", "iter 2: wi-A", CancellationToken.None);
        var r3 = await fake.CommitAllAndPushAsync("/work/wt-2", "iter 1: wi-B", CancellationToken.None);

        Assert.True(r1);
        Assert.False(r2);
        Assert.False(r3, "Default (queue empty) must return false so tests don't accidentally double-count commits.");
        Assert.Equal(3, fake.CommitAllAndPushCalls.Count);
        Assert.Equal(("/work/wt-1", "iter 1: wi-A"), fake.CommitAllAndPushCalls[0]);
        Assert.Equal(("/work/wt-1", "iter 2: wi-A"), fake.CommitAllAndPushCalls[1]);
        Assert.Equal(("/work/wt-2", "iter 1: wi-B"), fake.CommitAllAndPushCalls[2]);
    }

    // ── fixture + git helpers ────────────────────────────────────────────

    /// <summary>
    /// Create a bare remote + a working clone with origin set + main checked
    /// out. Mirrors the <c>Phase81PlanDocGitignoreTests.CreateRepoAsync</c>
    /// fixture but simpler (no allowlist gitignore).
    /// </summary>
    private async Task<(string RemoteDir, string CloneDir)> CreateBareRemoteAndCloneAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var remoteDir = Path.Combine(Path.GetTempPath(), $"forge-piter-remote-{id}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"forge-piter-seed-{id}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"forge-piter-clone-{id}");
        this.tempDirs.Add(remoteDir);
        this.tempDirs.Add(cloneDir);

        Directory.CreateDirectory(remoteDir);
        await RunGit(remoteDir, "init", "--bare", "-b", "main");

        Directory.CreateDirectory(seedDir);
        await RunGit(seedDir, "init", "-b", "main");
        await RunGit(seedDir, "config", "user.email", "seed@forge.local");
        await RunGit(seedDir, "config", "user.name", "Forge Seed");
        Directory.CreateDirectory(Path.Combine(seedDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(seedDir, "src", ".keep"), "\n");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "README.md"), "seed\n");
        await RunGit(seedDir, "add", ".");
        await RunGit(seedDir, "commit", "-m", "seed");
        await RunGit(seedDir, "remote", "add", "origin", remoteDir);
        await RunGit(seedDir, "push", "-u", "origin", "main");
        try { Directory.Delete(seedDir, recursive: true); } catch { /* best-effort */ }

        await RunGit(Path.GetTempPath(), "clone", remoteDir, cloneDir);
        await RunGit(cloneDir, "config", "user.email", "clone@forge.local");
        await RunGit(cloneDir, "config", "user.name", "Forge Clone");
        return (remoteDir, cloneDir);
    }

    private static async Task<string> GetSha(string cwd, string rev)
    {
        return (await RunGit(cwd, "rev-parse", rev)).Trim();
    }

    private static async Task<string> RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in '{cwd}': {await stderrTask}");
        }
        return stdout;
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit(2_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
