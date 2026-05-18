// -----------------------------------------------------------------------
// <copyright file="Phase79bWorktreeMidRebaseLookupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Regression for the 2026-05-18 incident on
/// <c>code-intelligence:AGENT-MEMORY</c> stages 7.2 and 7.4: a workstream
/// paused mid-rebase showed <c>detached</c> in
/// <c>git worktree list --porcelain</c>, so
/// <see cref="RealStoryGitOps.TryFindExistingWorktreeAsync"/> returned
/// null and downstream callers — most visibly
/// <see cref="IStoryGitOps.ContinueRebaseAndPushAsync"/> —
/// threw "Phase 79: no worktree is registered" even though the worktree
/// existed and still owned the branch via the per-worktree
/// <c>rebase-merge/head-name</c> state file. Phase 79b teaches the
/// lookup to recognise that case.
/// </summary>
public class Phase79bWorktreeMidRebaseLookupTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-ph79b-{Guid.NewGuid():N}");
    private RealStoryGitOps git = null!;
    private RepoRow repo = null!;
    private string clonePath = string.Empty;
    private string originPath = string.Empty;

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        Directory.CreateDirectory(this.repoRoot);
        this.originPath = Path.Combine(this.repoRoot, "origin.git");
        this.clonePath = Path.Combine(this.repoRoot, "clone");

        await RunAsync(this.repoRoot, "git", "init", "--bare", "-b", "main", this.originPath);
        await RunAsync(this.repoRoot, "git", "clone", this.originPath, this.clonePath);
        await ConfigureGitIdentityAsync(this.clonePath);

        // Seed the base branch with a file we will conflict on.
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"v0\" }\n");
        await RunAsync(this.clonePath, "git", "add", ".");
        await RunAsync(this.clonePath, "git", "commit", "-m", "Initial");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", "main");

        this.git = new RealStoryGitOps();
        this.repo = new RepoRow { Id = "repo-1", CloneRoot = this.clonePath };
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoRoot, recursive: true); }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Headline regression. Drive the production sequence end-to-end:
    /// <list type="number">
    ///   <item>main advances (analogous to a sibling workstream landing on the base).</item>
    ///   <item>A ws/ branch worktree is created at the canonical slug and
    ///         diverges on the same file as main.</item>
    ///   <item>A rebase of the ws/ branch onto origin/main is started in
    ///         that worktree; it pauses on a conflict, leaving the worktree
    ///         detached with <c>rebase-merge/head-name</c> populated.</item>
    ///   <item>The porcelain output now shows the worktree as
    ///         <c>detached</c> — the pre-fix code would return null here.</item>
    ///   <item>Phase 79b: the lookup MUST instead return the canonical
    ///         worktree path so <c>ContinueRebaseAndPushAsync</c> can
    ///         recover the in-flight rebase state.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task TryFindExistingWorktreeAsync_BranchPausedMidRebase_ReturnsCanonicalWorktreePath()
    {
        if (!GitAvailable()) return;

        const string wsBranch = "ws/code-intelligence-AGENT-MEMORY/phase-management-surface-stage-snapshot-verb";
        const string canonicalSlug = "stage-7.4-snapshot-verb";

        // (1) Base branch advances with a change to handler.go.
        await RunAsync(this.clonePath, "git", "checkout", "main");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"main-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "main: v1");
        await RunAsync(this.clonePath, "git", "push", "origin", "main");

        // (2) Create the ws/ branch as it would look right before the rebase:
        // diverged on the same file from the original commit.
        await RunAsync(this.clonePath, "git", "checkout", "-b", wsBranch, "origin/main~1");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"ws-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "ws: snapshot verb");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", wsBranch);
        // Move the main clone off the branch so we can attach a linked worktree on it.
        await RunAsync(this.clonePath, "git", "checkout", "main");

        // (3) Attach the canonical worktree on the ws branch.
        var wtRoot = Path.Combine(this.clonePath, ".worktree");
        Directory.CreateDirectory(wtRoot);
        var canonicalWtPath = Path.Combine(wtRoot, canonicalSlug);
        await RunAsync(this.clonePath, "git", "worktree", "add", canonicalWtPath, wsBranch);

        // (4) Start the rebase in the worktree; it must pause on conflict.
        var (rebaseExit, _, _) = await CaptureAsync(canonicalWtPath, "git", "rebase", "origin/main");
        Assert.NotEqual(0, rebaseExit); // conflict → non-zero, rebase paused
        Assert.True(
            File.Exists(Path.Combine(canonicalWtPath, ".git"))
                ? File.Exists(Path.Combine(this.clonePath, ".git", "worktrees", canonicalSlug, "rebase-merge", "head-name"))
                : false,
            "Expected rebase-merge/head-name to be present after a paused rebase.");

        // (5) Sanity-check the porcelain output: the canonical worktree
        // shows up as `detached` (this is the failure mode we're patching).
        var (_, porcelain, _) = await CaptureAsync(this.clonePath, "git", "worktree", "list", "--porcelain");
        Assert.Contains(canonicalSlug, porcelain.Replace('\\', '/'));
        Assert.Contains("detached", porcelain);
        // The branch attribution `branch refs/heads/{wsBranch}` is hidden
        // while the rebase is in flight — git replaces it with `detached`.
        Assert.DoesNotContain("branch refs/heads/" + wsBranch, porcelain);

        // (6) The Phase 79b lookup must still return the canonical worktree.
        var hit = await this.git.TryFindExistingWorktreeAsync(this.clonePath, wsBranch, CancellationToken.None);
        Assert.NotNull(hit);

        var normHit = hit!.Replace('\\', '/').TrimEnd('/');
        var normExpected = canonicalWtPath.Replace('\\', '/').TrimEnd('/');
        Assert.Equal(normExpected, normHit, ignoreCase: true);
    }

    /// <summary>
    /// Pin the fast-path: when porcelain DOES expose the branch (no
    /// rebase in progress), the helper returns immediately without
    /// touching the rebase-state probe. Belt-and-braces against an
    /// accidental regression that always fell through to the slower
    /// filesystem scan.
    /// </summary>
    [Fact]
    public async Task TryFindExistingWorktreeAsync_BranchCheckedOutNormally_ReturnsPathViaPorcelain()
    {
        if (!GitAvailable()) return;

        const string branch = "ws/forge-ph79b-clean";
        const string slug = "stage-clean";

        await RunAsync(this.clonePath, "git", "branch", branch);
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", branch);

        var wtRoot = Path.Combine(this.clonePath, ".worktree");
        Directory.CreateDirectory(wtRoot);
        var canonicalWtPath = Path.Combine(wtRoot, slug);
        await RunAsync(this.clonePath, "git", "worktree", "add", canonicalWtPath, branch);

        var hit = await this.git.TryFindExistingWorktreeAsync(this.clonePath, branch, CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal(
            canonicalWtPath.Replace('\\', '/').TrimEnd('/'),
            hit!.Replace('\\', '/').TrimEnd('/'),
            ignoreCase: true);
    }

    /// <summary>
    /// Unrelated mid-rebase state on a DIFFERENT branch must not produce
    /// a false-positive hit. The probe matches strictly on
    /// <c>refs/heads/{branch}</c>.
    /// </summary>
    [Fact]
    public async Task TryFindExistingWorktreeAsync_OtherBranchMidRebase_DoesNotMisattribute()
    {
        if (!GitAvailable()) return;

        const string realBranch = "ws/forge-ph79b-real";
        const string otherBranch = "ws/forge-ph79b-other";
        const string slug = "stage-other";

        // Create another branch and put IT into a mid-rebase state.
        await RunAsync(this.clonePath, "git", "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"main-v2\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "main: v2");
        await RunAsync(this.clonePath, "git", "push", "origin", "main");

        await RunAsync(this.clonePath, "git", "checkout", "-b", otherBranch, "origin/main~1");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"other-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "other: v1");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", otherBranch);
        await RunAsync(this.clonePath, "git", "checkout", "main");

        var otherWt = Path.Combine(this.clonePath, ".worktree", slug);
        await RunAsync(this.clonePath, "git", "worktree", "add", otherWt, otherBranch);
        var (rebaseExit, _, _) = await CaptureAsync(otherWt, "git", "rebase", "origin/main");
        Assert.NotEqual(0, rebaseExit);

        // Looking up an unrelated branch that has NO worktree of any kind
        // should still return null.
        var miss = await this.git.TryFindExistingWorktreeAsync(this.clonePath, realBranch, CancellationToken.None);
        Assert.Null(miss);

        // Looking up the branch that IS mid-rebase should return its worktree.
        var hit = await this.git.TryFindExistingWorktreeAsync(this.clonePath, otherBranch, CancellationToken.None);
        Assert.NotNull(hit);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task ConfigureGitIdentityAsync(string cwd)
    {
        await RunAsync(cwd, "git", "config", "user.email", "test@forge.local");
        await RunAsync(cwd, "git", "config", "user.name", "Forge Test");
    }

    private static async Task RunAsync(string cwd, string program, params string[] args)
    {
        var (code, _, stderr) = await CaptureAsync(cwd, program, args);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"{program} {string.Join(' ', args)} failed (exit {code}) in {cwd}: {stderr}");
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> CaptureAsync(string cwd, string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
