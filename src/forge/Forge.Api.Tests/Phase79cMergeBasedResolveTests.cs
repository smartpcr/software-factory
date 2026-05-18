// -----------------------------------------------------------------------
// <copyright file="Phase79cMergeBasedResolveTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 79c — pins the new merge-based conflict resolver on
/// <see cref="RealStoryGitOps"/>. The previous resolver rebased + force-pushed
/// the workstream branch, which routinely orphaned late-arriving reviewer
/// comments after the branch tip was rewritten. The new path runs
/// <c>git merge origin/{base}</c> into the workstream branch in its
/// worktree, lets the agent resolve any conflicts, finalises the merge
/// commit, and plain-pushes — preserving the branch's history.
/// </summary>
public class Phase79cMergeBasedResolveTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-ph79c-{Guid.NewGuid():N}");
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
    /// Headline — base has a conflicting change on the same file the ws
    /// branch edited. Detect step reports Conflicted with the file content
    /// (markers preserved); after the caller hands back a clean resolution
    /// the finalize step plain-pushes a merge commit. Origin's tip must
    /// move to the new merge commit, and the prior ws-branch commit
    /// must still be reachable in the history (no force-push rewrite).
    /// </summary>
    [Fact]
    public async Task MergeAndContinue_OnConflict_AgentResolution_ProducesMergeCommit_PlainPushed()
    {
        if (!GitAvailable()) return;

        const string wsBranch = "ws/code-intelligence-AGENT-MEMORY/snapshot-verb";
        const string slug = "stage-7.4-snapshot-verb";

        // main advances (sibling PR landed).
        await RunAsync(this.clonePath, "git", "checkout", "main");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"main-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "main: v1");
        await RunAsync(this.clonePath, "git", "push", "origin", "main");

        // ws diverged from main~1 and edited the same file.
        await RunAsync(this.clonePath, "git", "checkout", "-b", wsBranch, "origin/main~1");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"ws-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "ws: snapshot verb");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", wsBranch);
        await RunAsync(this.clonePath, "git", "checkout", "main");

        // Capture the ws tip BEFORE the merge resolve runs so we can prove
        // it is still reachable from the new tip (i.e. no force-push).
        var (_, wsTipBefore, _) = await CaptureAsync(this.clonePath, "git", "rev-parse", $"origin/{wsBranch}");
        wsTipBefore = wsTipBefore.Trim();
        Assert.False(string.IsNullOrEmpty(wsTipBefore));

        // Attach the canonical worktree on the ws branch so the resolver
        // finds it via TryFindExistingWorktreeAsync.
        var wtRoot = Path.Combine(this.clonePath, ".worktree");
        Directory.CreateDirectory(wtRoot);
        var wtPath = Path.Combine(wtRoot, slug);
        await RunAsync(this.clonePath, "git", "worktree", "add", wtPath, wsBranch);

        // 1) Detect step — expect Conflicted on handler.go.
        var detect = await this.git.MergeBaseIntoBranchAndDetectConflictsAsync(
            this.repo, wsBranch, "main", CancellationToken.None);
        Assert.Equal(MergeState.Conflicted, detect.State);
        Assert.Single(detect.ConflictedFiles);
        Assert.Equal("handler.go", detect.ConflictedFiles[0].Path);
        Assert.Contains("<<<<<<<", detect.ConflictedFiles[0].Content);

        // 2) Caller resolves: keep both behaviours symbolically by writing
        //    a clean version with no markers.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["handler.go"] = "package mgmtapi\n\nfunc Handle() string { return \"ws-v1+main-v1\" }\n",
        };

        // 3) Finalize step — commits the merge and plain-pushes.
        var finalize = await this.git.ContinueMergeAndPushAsync(
            this.repo, wsBranch, resolved, CancellationToken.None);
        Assert.Equal(MergeState.Clean, finalize.State);

        // 4) Origin tip moved.
        var (_, wsTipAfter, _) = await CaptureAsync(this.clonePath, "git", "rev-parse", $"origin/{wsBranch}");
        wsTipAfter = wsTipAfter.Trim();
        Assert.NotEqual(wsTipBefore, wsTipAfter);

        // 5) The prior ws tip is still reachable from the new tip — proves
        //    the push was NOT a force-push (history preserved). Use git
        //    merge-base --is-ancestor to assert.
        var (ancestorExit, _, _) = await CaptureAsync(
            this.clonePath, "git", "merge-base", "--is-ancestor", wsTipBefore, wsTipAfter);
        Assert.Equal(0, ancestorExit);

        // 6) The new tip is a merge commit (two parents).
        var (_, parents, _) = await CaptureAsync(
            this.clonePath, "git", "rev-list", "--parents", "-n", "1", wsTipAfter);
        var parentCount = parents.Trim().Split(' ').Length - 1; // first token is the commit itself
        Assert.Equal(2, parentCount);

        // 7) handler.go on origin now reflects the resolver's content.
        await RunAsync(this.clonePath, "git", "fetch", "origin", wsBranch);
        var (_, fileOut, _) = await CaptureAsync(
            this.clonePath, "git", "show", $"origin/{wsBranch}:handler.go");
        Assert.Contains("ws-v1+main-v1", fileOut);
    }

    /// <summary>
    /// When base has advanced but on a different file (no conflict), the
    /// detect step returns Clean. The finalize step with an empty resolved
    /// dict still has work to do: push the merge commit produced inside
    /// the detect call. Origin's tip moves to a new merge commit.
    /// </summary>
    [Fact]
    public async Task MergeAndContinue_OnNoConflict_PushesMergeCommit_NoForce()
    {
        if (!GitAvailable()) return;

        const string wsBranch = "ws/forge-ph79c-clean";
        const string slug = "stage-clean";

        // main advances on a DIFFERENT file than ws will touch.
        await RunAsync(this.clonePath, "git", "checkout", "main");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "README.md"),
            "# project\n\nv1\n");
        await RunAsync(this.clonePath, "git", "add", "README.md");
        await RunAsync(this.clonePath, "git", "commit", "-m", "main: add README");
        await RunAsync(this.clonePath, "git", "push", "origin", "main");

        // ws branched from main~1 and edited handler.go.
        await RunAsync(this.clonePath, "git", "checkout", "-b", wsBranch, "origin/main~1");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"ws-clean\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "ws: handler");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", wsBranch);
        await RunAsync(this.clonePath, "git", "checkout", "main");

        var (_, tipBefore, _) = await CaptureAsync(this.clonePath, "git", "rev-parse", $"origin/{wsBranch}");
        tipBefore = tipBefore.Trim();

        var wtRoot = Path.Combine(this.clonePath, ".worktree");
        Directory.CreateDirectory(wtRoot);
        var wtPath = Path.Combine(wtRoot, slug);
        await RunAsync(this.clonePath, "git", "worktree", "add", wtPath, wsBranch);

        var detect = await this.git.MergeBaseIntoBranchAndDetectConflictsAsync(
            this.repo, wsBranch, "main", CancellationToken.None);
        Assert.Equal(MergeState.Clean, detect.State);
        Assert.Empty(detect.ConflictedFiles);

        // Empty dict — caller asks ContinueMergeAndPushAsync to just push.
        var finalize = await this.git.ContinueMergeAndPushAsync(
            this.repo, wsBranch, new Dictionary<string, string>(StringComparer.Ordinal), CancellationToken.None);
        Assert.Equal(MergeState.Clean, finalize.State);

        var (_, tipAfter, _) = await CaptureAsync(this.clonePath, "git", "rev-parse", $"origin/{wsBranch}");
        tipAfter = tipAfter.Trim();
        Assert.NotEqual(tipBefore, tipAfter);

        // Prior tip reachable → plain (non-force) push.
        var (ancestorExit, _, _) = await CaptureAsync(
            this.clonePath, "git", "merge-base", "--is-ancestor", tipBefore, tipAfter);
        Assert.Equal(0, ancestorExit);
    }

    /// <summary>
    /// If the agent's resolution still contains conflict markers, the
    /// finalize step refuses to commit the merge and returns Conflicted
    /// with the still-broken files so the orchestrator can give up that
    /// attempt without leaving the worktree in a permanently merging
    /// state on the wrong content.
    /// </summary>
    [Fact]
    public async Task ContinueMerge_WithUnresolvedMarkers_ReturnsConflicted()
    {
        if (!GitAvailable()) return;

        const string wsBranch = "ws/forge-ph79c-bad-resolve";
        const string slug = "stage-bad-resolve";

        await RunAsync(this.clonePath, "git", "checkout", "main");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"main-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "main: v1");
        await RunAsync(this.clonePath, "git", "push", "origin", "main");

        await RunAsync(this.clonePath, "git", "checkout", "-b", wsBranch, "origin/main~1");
        await File.WriteAllTextAsync(
            Path.Combine(this.clonePath, "handler.go"),
            "package mgmtapi\n\nfunc Handle() string { return \"ws-v1\" }\n");
        await RunAsync(this.clonePath, "git", "commit", "-am", "ws");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", wsBranch);
        await RunAsync(this.clonePath, "git", "checkout", "main");

        var wtRoot = Path.Combine(this.clonePath, ".worktree");
        Directory.CreateDirectory(wtRoot);
        var wtPath = Path.Combine(wtRoot, slug);
        await RunAsync(this.clonePath, "git", "worktree", "add", wtPath, wsBranch);

        var detect = await this.git.MergeBaseIntoBranchAndDetectConflictsAsync(
            this.repo, wsBranch, "main", CancellationToken.None);
        Assert.Equal(MergeState.Conflicted, detect.State);

        // Agent's "resolution" still has markers.
        var bad = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["handler.go"] =
                "package mgmtapi\n\n"
                + "<<<<<<< HEAD\nfunc Handle() string { return \"ws-v1\" }\n"
                + "=======\nfunc Handle() string { return \"main-v1\" }\n"
                + ">>>>>>> origin/main\n",
        };

        var finalize = await this.git.ContinueMergeAndPushAsync(
            this.repo, wsBranch, bad, CancellationToken.None);
        Assert.Equal(MergeState.Conflicted, finalize.State);
        Assert.Single(finalize.ConflictedFiles);
    }

    // ── Helpers (parallel to Phase79b test file) ────────────────────────

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
