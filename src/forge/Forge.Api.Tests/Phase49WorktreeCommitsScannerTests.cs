// -----------------------------------------------------------------------
// <copyright file="Phase49WorktreeCommitsScannerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 49 follow-up — pin the Worktree-tab counters against a real
/// temp git repo. Observed 2026-05-10 on failover-cluster:XRAFT stage 1.2:
/// the Worktree tab read <c>+0 ~0 -45</c> on a workstream that had only
/// added files; the 45 "deletions" were files PR #2 (sibling stage 1.1)
/// had merged into <c>origin/feature/xraft</c> AFTER stage 1.2's worktree
/// was created. Diffing against <c>origin/feature/xraft</c> directly
/// counted those as missing-from-worktree — false positives.
///
/// The fix anchors the diff at <c>git merge-base origin/{baseBranch} HEAD</c>
/// (the fork point) so the totals only reflect what THIS workstream
/// contributed, regardless of how the base has advanced.
/// </summary>
public class Phase49WorktreeCommitsScannerTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-scanner-{Guid.NewGuid():N}");
    private string originPath = string.Empty;
    private string clonePath = string.Empty;
    private string workPath = string.Empty;

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        // Bare "origin" + a local clone that lives at clonePath. The
        // "worktree" we'll scan is just the clone itself in a detached
        // working-state — no need to involve git's `worktree add` plumbing
        // since the scanner doesn't care about the worktree-vs-clone
        // distinction (it just runs git commands in the directory it was
        // pointed at).
        Directory.CreateDirectory(this.repoRoot);
        this.originPath = Path.Combine(this.repoRoot, "origin.git");
        this.clonePath = Path.Combine(this.repoRoot, "clone");
        this.workPath = this.clonePath;

        await Run(this.repoRoot, "git", "init", "--bare", "-b", "main", this.originPath);
        await Run(this.repoRoot, "git", "clone", this.originPath, this.clonePath);
        await Run(this.clonePath, "git", "config", "user.email", "test@forge.local");
        await Run(this.clonePath, "git", "config", "user.name", "Forge Test");

        // Initial commit on main so origin/main has content.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# repo\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "Initial");
        await Run(this.clonePath, "git", "push", "-u", "origin", "main");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoRoot, recursive: true); }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Headline regression — workstream branch adds 2 files, the base
    /// advances independently with 5 unrelated files, and the scanner
    /// must report ONLY the workstream's 2 added files (not "5 deletions"
    /// for the upstream files that aren't in the worktree).
    /// </summary>
    [Fact]
    public async Task BranchTotals_AnchorsAtMergeBase_NotAtCurrentOriginTip()
    {
        if (!GitAvailable()) return;

        // ws branched off main at the initial commit.
        await Run(this.clonePath, "git", "checkout", "-b", "ws/test");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "ws-add-1.txt"), "ws contribution one\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "ws-add-2.txt"), "ws contribution two\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: add two files");

        // Base advances in a sibling clone (5 unrelated files), then push.
        await SimulateBaseAdvanceAsync("main", new[] { "up-1.txt", "up-2.txt", "up-3.txt", "up-4.txt", "up-5.txt" });

        // Refresh the workstream clone's view of origin so merge-base
        // computation sees the advanced tip.
        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        Assert.Equal(2, scan.BranchTotals.Added.Count);
        Assert.Contains("ws-add-1.txt", scan.BranchTotals.Added);
        Assert.Contains("ws-add-2.txt", scan.BranchTotals.Added);
        Assert.Empty(scan.BranchTotals.Modified);
        Assert.Empty(scan.BranchTotals.Deleted);

        // Ahead/behind still reflect the literal remote tip — operator
        // sees how far this branch has fallen behind upstream even
        // though the totals show only their own contribution.
        Assert.Equal(1, scan.Ahead);
        Assert.True(scan.Behind >= 1, $"expected behind >= 1, got {scan.Behind}");
    }

    /// <summary>
    /// Pin the counters across all three diff statuses. ws commits one
    /// each of Add / Modify / Delete; scanner totals must equal exactly
    /// 1 / 1 / 1.
    /// </summary>
    [Fact]
    public async Task BranchTotals_CountsAddModifyDelete_Distinctly()
    {
        if (!GitAvailable()) return;

        // Seed two files on main so we have something to modify and delete.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "to-modify.txt"), "v1\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "to-delete.txt"), "doomed\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "seed two files");
        await Run(this.clonePath, "git", "push", "origin", "main");

        await Run(this.clonePath, "git", "checkout", "-b", "ws/amd");

        // One commit doing all three operations.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "new-add.txt"), "fresh\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "to-modify.txt"), "v2\n");
        File.Delete(Path.Combine(this.clonePath, "to-delete.txt"));
        await Run(this.clonePath, "git", "add", "-A");
        await Run(this.clonePath, "git", "commit", "-m", "ws: A/M/D");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");
        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        Assert.Single(scan.BranchTotals.Added);
        Assert.Contains("new-add.txt", scan.BranchTotals.Added);
        Assert.Single(scan.BranchTotals.Modified);
        Assert.Contains("to-modify.txt", scan.BranchTotals.Modified);
        Assert.Single(scan.BranchTotals.Deleted);
        Assert.Contains("to-delete.txt", scan.BranchTotals.Deleted);
    }

    /// <summary>
    /// Uncommitted dirty files surface in <see cref="WorktreeCommitsScanner.UncommittedChanges"/>
    /// AND fold into <see cref="WorktreeCommitsScanner.BranchTotals"/> so
    /// the Worktree-tab pills show the full picture (committed + dirty)
    /// in one number per status.
    /// </summary>
    [Fact]
    public async Task BranchTotals_FoldUncommittedChangesIn()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/dirty");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "committed.txt"), "in a commit\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: committed file");

        // Now an UNCOMMITTED add + an UNCOMMITTED modify of README.md.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "dirty-add.txt"), "not committed yet\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# repo (edited)\n");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");
        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        Assert.Contains("dirty-add.txt", scan.Uncommitted.Added);
        Assert.Contains("README.md", scan.Uncommitted.Modified);

        // Totals include BOTH the committed file AND the uncommitted ones.
        Assert.Contains("committed.txt", scan.BranchTotals.Added);
        Assert.Contains("dirty-add.txt", scan.BranchTotals.Added);
        Assert.Contains("README.md", scan.BranchTotals.Modified);
    }

    /// <summary>
    /// Per-commit grouping — file changes attribute to the commit that
    /// introduced them. Two commits each touching different files;
    /// scanner must produce two <see cref="WorktreeCommitsScanner.CommitEntry"/>
    /// rows, each with its own Added array.
    /// </summary>
    [Fact]
    public async Task CommitsList_GroupsFileChangesByCommit()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/multi-commit");

        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "c1-a.txt"), "1a\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "c1-b.txt"), "1b\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "commit one");

        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "c2-x.txt"), "2x\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "commit two");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");
        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        Assert.Equal(2, scan.Commits.Count);

        // git log returns newest first; "commit two" is at index 0.
        var c2 = scan.Commits[0];
        Assert.Equal("commit two", c2.Message);
        Assert.Single(c2.Added);
        Assert.Contains("c2-x.txt", c2.Added);
        Assert.Empty(c2.Modified);
        Assert.Empty(c2.Deleted);

        var c1 = scan.Commits[1];
        Assert.Equal("commit one", c1.Message);
        Assert.Equal(2, c1.Added.Count);
        Assert.Contains("c1-a.txt", c1.Added);
        Assert.Contains("c1-b.txt", c1.Added);
        Assert.Empty(c1.Modified);
        Assert.Empty(c1.Deleted);

        // BranchTotals dedupes per-path — each file should appear once.
        Assert.Equal(3, scan.BranchTotals.Added.Count);
    }

    /// <summary>
    /// File added in commit 1, modified in commit 2. Per-commit view
    /// shows the file in commit-1's Added AND in commit-2's Modified
    /// (history is honest); the cumulative <see cref="WorktreeCommitsScanner.BranchTotals"/>
    /// flattens this to a single Added entry — what a reviewer would
    /// see as "this PR added one new file".
    /// </summary>
    [Fact]
    public async Task BranchTotals_DeduplicateAddedThenModifiedAsSingleAdd()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/add-then-modify");

        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "evolving.txt"), "v1\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "add evolving");

        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "evolving.txt"), "v2\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "modify evolving");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");
        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        // Per-commit view honours history.
        Assert.Equal(2, scan.Commits.Count);
        Assert.Contains("evolving.txt", scan.Commits[1].Added);
        Assert.Contains("evolving.txt", scan.Commits[0].Modified);

        // Cumulative branch totals show the net effect — one Add.
        Assert.Single(scan.BranchTotals.Added);
        Assert.Contains("evolving.txt", scan.BranchTotals.Added);
        Assert.Empty(scan.BranchTotals.Modified);
        Assert.Empty(scan.BranchTotals.Deleted);
    }

    /// <summary>
    /// Renames surface in both <c>Other</c> (with OldPath populated) and
    /// the <c>Modified</c> bucket (so the summary count includes them).
    /// </summary>
    [Fact]
    public async Task CommitsList_HandlesRenames()
    {
        if (!GitAvailable()) return;

        // Seed a file on main that we can rename on the workstream branch.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "old-name.txt"), "stable content\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "seed for rename");
        await Run(this.clonePath, "git", "push", "origin", "main");

        await Run(this.clonePath, "git", "checkout", "-b", "ws/rename");
        await Run(this.clonePath, "git", "mv", "old-name.txt", "new-name.txt");
        await Run(this.clonePath, "git", "commit", "-m", "rename file");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");
        var scan = await WorktreeCommitsScanner.ScanAsync(this.workPath, "main", CancellationToken.None);

        Assert.Single(scan.Commits);
        var commit = scan.Commits[0];
        Assert.Contains(commit.Other, o => o.Status.StartsWith("R", StringComparison.Ordinal) && o.Path == "new-name.txt" && o.OldPath == "old-name.txt");
        Assert.Contains("new-name.txt", commit.Modified);
    }

    /// <summary>
    /// Empty worktree path returns an all-empty <see cref="WorktreeCommitsScanner.ScanResult"/>
    /// — no exception, no git invocation, just the zero-shape so the
    /// endpoint can serialize it.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ReturnsEmpty_WhenWorktreePathDoesNotExist()
    {
        if (!GitAvailable()) return;
        var bogus = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        var scan = await WorktreeCommitsScanner.ScanAsync(bogus, "main", CancellationToken.None);
        Assert.Equal(0, scan.Ahead);
        Assert.Equal(0, scan.Behind);
        Assert.Null(scan.HeadSha);
        Assert.Empty(scan.Commits);
        Assert.Empty(scan.BranchTotals.Added);
        Assert.Empty(scan.BranchTotals.Modified);
        Assert.Empty(scan.BranchTotals.Deleted);
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Push <paramref name="files"/> onto <paramref name="baseBranch"/>
    /// from a separate sibling clone so the workstream's clone has no
    /// awareness until it next fetches. Mirrors what happens in
    /// production when a sibling PR merges upstream.
    /// </summary>
    private async Task SimulateBaseAdvanceAsync(string baseBranch, string[] files)
    {
        var sibling = Path.Combine(this.repoRoot, $"sibling-{Guid.NewGuid():N}");
        await Run(this.repoRoot, "git", "clone", this.originPath, sibling);
        await Run(sibling, "git", "config", "user.email", "test@forge.local");
        await Run(sibling, "git", "config", "user.name", "Forge Test");
        await Run(sibling, "git", "checkout", baseBranch);
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(sibling, file), $"upstream contribution from sibling: {file}\n");
        }
        await Run(sibling, "git", "add", ".");
        await Run(sibling, "git", "commit", "-m", $"sibling: add {files.Length} files");
        await Run(sibling, "git", "push", "origin", baseBranch);
    }

    private static async Task Run(string cwd, string program, params string[] args)
    {
        var (code, _, stderr) = await Capture(cwd, program, args);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"{program} {string.Join(' ', args)} failed (exit {code}) in {cwd}: {stderr}");
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Capture(string cwd, string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
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
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
