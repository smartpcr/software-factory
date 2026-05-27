// -----------------------------------------------------------------------
// <copyright file="SnapshotWorktreeChangesTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// 2026-05-16 — regression tests for
/// <see cref="RealStoryGitOps.SnapshotWorktreeChangesAsync"/>. The bug it
/// fixes: when the workstream path commits ONCE at pass-time (via
/// <c>ReadCachedWorkstreamFilesAsync</c>) instead of per-iter,
/// <see cref="RealStoryGitOps.SnapshotBranchHeadAsync"/> reported 0,0,0
/// for every iter because HEAD never advanced during the gen-eval loop.
/// The new method diffs the live worktree (committed + uncommitted +
/// untracked) against <c>baseBranch</c> so iter chips show the cumulative
/// WIP delta, matching what the operator sees in the worktree pane.
///
/// <para>These tests build a real on-disk fixture git repo so the
/// command sequences are exercised end-to-end. Clean up the temp
/// directory in <c>IDisposable</c> to keep test runs hermetic.</para>
/// </summary>
public class SnapshotWorktreeChangesTests : IDisposable
{
    private readonly string repoDir;
    private readonly RealStoryGitOps git;

    public SnapshotWorktreeChangesTests()
    {
        this.repoDir = Path.Combine(Path.GetTempPath(), $"forge-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.repoDir);
        Run("init", "-b", "main");
        Run("config", "user.email", "test@local");
        Run("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(this.repoDir, "base.txt"), "base\n");
        Run("add", ".");
        Run("commit", "-m", "base");

        this.git = new RealStoryGitOps();
    }

    public void Dispose()
    {
        try
        {
            // Force-clear read-only attrs (git pack files set them on Windows).
            foreach (var f in Directory.EnumerateFiles(this.repoDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(this.repoDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task EmptyWorktree_AgainstBaseBranch_Reports_Zero_Counts()
    {
        // No edits, no commits since base → all counts zero.
        var snap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Added);
        Assert.Equal(0, snap.Modified);
        Assert.Equal(0, snap.Deleted);
    }

    [Fact]
    public async Task UncommittedTrackedModification_Counts_As_Modified()
    {
        // Modify a tracked file — should appear as M without committing.
        File.WriteAllText(Path.Combine(this.repoDir, "base.txt"), "base\nadded line\n");
        var snap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Added);
        Assert.Equal(1, snap.Modified);
        Assert.Equal(0, snap.Deleted);
    }

    [Fact]
    public async Task UntrackedFiles_Count_As_Added()
    {
        // Three new files, no `git add`.
        File.WriteAllText(Path.Combine(this.repoDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(this.repoDir, "b.txt"), "b");
        File.WriteAllText(Path.Combine(this.repoDir, "c.txt"), "c");
        var snap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Equal(3, snap!.Added);
        Assert.Equal(0, snap.Modified);
        Assert.Equal(0, snap.Deleted);
    }

    [Fact]
    public async Task UncommittedDelete_Counts_As_Deleted()
    {
        File.Delete(Path.Combine(this.repoDir, "base.txt"));
        var snap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Added);
        Assert.Equal(0, snap.Modified);
        Assert.Equal(1, snap.Deleted);
    }

    [Fact]
    public async Task ReplicatesBugScenario_PostLoopCommit_WorktreeStillCarriesChanges()
    {
        // Mirrors the operator-reported bug: agent edits N files but no
        // per-iter commits happen — every iter snapshot should still
        // report N files (not 0). After a final commit lands, the same
        // snapshot continues to report N (committed+vs-base instead of
        // uncommitted-vs-HEAD).
        // 1. Branch off main into a feature branch (simulates workstream branch).
        Run("checkout", "-b", "ws/test-feature");

        // 2. Iter 1-4: agent creates files but does NOT commit.
        File.WriteAllText(Path.Combine(this.repoDir, "f1.go"), "// f1");
        File.WriteAllText(Path.Combine(this.repoDir, "f2.go"), "// f2");
        File.WriteAllText(Path.Combine(this.repoDir, "f3.go"), "// f3");
        var iterSnap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.NotNull(iterSnap);
        Assert.Equal(3, iterSnap!.Added);

        // Add more files (iter 2-3 cumulative simulation).
        File.WriteAllText(Path.Combine(this.repoDir, "f4.go"), "// f4");
        File.WriteAllText(Path.Combine(this.repoDir, "f5.go"), "// f5");
        var iter3Snap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.Equal(5, iter3Snap!.Added);

        // 3. Iter 5 passes → orchestrator commits all 5 files at once.
        Run("add", ".");
        Run("commit", "-m", "impl: feature");

        // 4. Post-commit snapshot still shows 5 files vs main (now they're
        //    committed-on-feature-branch, but the diff against main still
        //    counts them).
        var passSnap = await this.git.SnapshotWorktreeChangesAsync(repo: null, this.repoDir, "main", CancellationToken.None);
        Assert.Equal(5, passSnap!.Added);
    }

    [Fact]
    public async Task NonExistentWorktreePath_Returns_Null()
    {
        var snap = await this.git.SnapshotWorktreeChangesAsync(
            repo: null, Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}"),
            "main", CancellationToken.None);
        Assert.Null(snap);
    }

    [Fact]
    public async Task EmptyBaseBranch_Returns_Null()
    {
        var snap = await this.git.SnapshotWorktreeChangesAsync(
            repo: null, this.repoDir, baseBranch: "", CancellationToken.None);
        Assert.Null(snap);
    }

    [Fact]
    public async Task MergedPrDiff_Uses_ProviderMergeRange_For_FastForwardMerge()
    {
        var baseSha = GitOut("rev-parse", "HEAD");
        Run("checkout", "-b", "ws/ff-pr");
        File.WriteAllText(Path.Combine(this.repoDir, "first.txt"), "first");
        Run("add", ".");
        Run("commit", "-m", "first change");
        File.WriteAllText(Path.Combine(this.repoDir, "second.txt"), "second");
        Run("add", ".");
        Run("commit", "-m", "second change");
        var headSha = GitOut("rev-parse", "HEAD");

        Run("checkout", "main");
        Run("merge", "--ff-only", "ws/ff-pr");

        var paths = await this.git.GetChangedFilesFromMergedPrAsync(
            repo: null,
            worktreePath: this.repoDir,
            mergeCommitSha: headSha,
            mergeBaseSha: baseSha,
            mergeHeadSha: headSha,
            ct: CancellationToken.None);

        Assert.Equal(new[] { "first.txt", "second.txt" }, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task MergedPrDiff_Falls_Back_To_FirstParent_For_MergeCommit()
    {
        Run("checkout", "-b", "ws/no-ff-pr");
        File.WriteAllText(Path.Combine(this.repoDir, "feature.txt"), "feature");
        Run("add", ".");
        Run("commit", "-m", "feature change");

        Run("checkout", "main");
        Run("merge", "--no-ff", "ws/no-ff-pr", "-m", "merge feature");
        var mergeSha = GitOut("rev-parse", "HEAD");

        var paths = await this.git.GetChangedFilesFromMergedPrAsync(
            repo: null,
            worktreePath: this.repoDir,
            mergeCommitSha: mergeSha,
            ct: CancellationToken.None);

        Assert.Equal(new[] { "feature.txt" }, paths);
    }

    private void Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = this.repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        }
    }

    private string GitOut(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = this.repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        }

        return p.StandardOutput.ReadToEnd().Trim();
    }
}
