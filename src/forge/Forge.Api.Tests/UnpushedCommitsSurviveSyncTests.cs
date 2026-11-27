// -----------------------------------------------------------------------
// <copyright file="UnpushedCommitsSurviveSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// A drive that refreshes a workstream branch from its base must not discard
/// commits that were never pushed. On harness:DSH-M2 stage 5.9-e2e
/// (2026-11-27) the agent kept commit b0e7e45 local on the operator's answer
/// "keep it local; do not push yet"; the drive after a RETRY ran
/// <c>git reset --hard origin/{branch}</c> before merging main and the commit
/// vanished from the branch. Real git against a bare local remote.
/// </summary>
public sealed class UnpushedCommitsSurviveSyncTests : IAsyncLifetime
{
    private const string Branch = "ws/keep-test";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"forge-keep-{Guid.NewGuid():N}");
    private string remoteDir = string.Empty;
    private string cloneDir = string.Empty;
    private string otherDir = string.Empty;
    private string wtPath = string.Empty;
    private bool gitAvailable;

    public async Task InitializeAsync()
    {
        this.gitAvailable = GitAvailable();
        if (!this.gitAvailable) return;

        this.remoteDir = Path.Combine(this.root, "remote.git");
        this.cloneDir = Path.Combine(this.root, "clone");
        this.otherDir = Path.Combine(this.root, "other");
        Directory.CreateDirectory(this.remoteDir);
        await Git(this.remoteDir, "init", "--bare", "-b", "main");

        // "other" plays every remote writer: main's history and pushes from elsewhere.
        await Git(this.root, "clone", this.remoteDir, this.otherDir);
        await Configure(this.otherDir);
        await Git(this.otherDir, "checkout", "-b", "main");
        await CommitFile(this.otherDir, "README.md", "seed\n", "seed");
        await CommitFile(this.otherDir, "shared.txt", "base\n", "shared");
        await Git(this.otherDir, "push", "-u", "origin", "main");
        await Git(this.otherDir, "checkout", "-b", Branch);
        await CommitFile(this.otherDir, "ws.txt", "ws\n", "workstream start");
        await Git(this.otherDir, "push", "-u", "origin", Branch);
        await Git(this.otherDir, "checkout", "main");

        await Git(this.root, "clone", this.remoteDir, this.cloneDir);
        await Configure(this.cloneDir);
        this.wtPath = Path.Combine(this.cloneDir, ".worktree", "keep-test");
        await Git(this.cloneDir, "worktree", "add", "-b", Branch, this.wtPath, $"origin/{Branch}");
        await Configure(this.wtPath);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MergeUpstream_LocalAheadOfOrigin_KeepsTheUnpushedCommit()
    {
        if (!this.gitAvailable) return;
        var local = await CommitFile(this.wtPath, "local.txt", "local\n", "unpushed local work");
        await this.AdvanceMainAsync();

        var ok = await new RealStoryGitOps().MergeUpstreamIntoBranchAsync(this.Repo(), Branch, "main");

        Assert.True(ok);
        Assert.True(await IsAncestor(this.wtPath, local, "HEAD"), "the unpushed commit was dropped from the branch");
        Assert.True(await IsAncestor(this.wtPath, "origin/main", "HEAD"), "main was not merged in");
    }

    [Fact]
    public async Task MergeUpstream_DivergedButClean_KeepsBothSidesAndBacksUpTheLocalTip()
    {
        if (!this.gitAvailable) return;
        var local = await CommitFile(this.wtPath, "local.txt", "local\n", "unpushed local work");
        var remote = await this.PushToBranchFromElsewhereAsync("remote.txt", "remote\n");
        await this.AdvanceMainAsync();

        var ok = await new RealStoryGitOps().MergeUpstreamIntoBranchAsync(this.Repo(), Branch, "main");

        Assert.True(ok);
        Assert.True(await IsAncestor(this.wtPath, local, "HEAD"), "the unpushed commit was dropped from the branch");
        Assert.True(await IsAncestor(this.wtPath, remote, "HEAD"), "the remote branch's commit is missing");
        Assert.Contains(local, await this.BackupRefTargetsAsync());
    }

    [Fact]
    public async Task MergeUpstream_DivergedWithConflict_ResetsToOriginButKeepsABackupRef()
    {
        if (!this.gitAvailable) return;
        var local = await CommitFile(this.wtPath, "shared.txt", "local side\n", "unpushed conflicting edit");
        var remote = await this.PushToBranchFromElsewhereAsync("shared.txt", "remote side\n");
        await this.AdvanceMainAsync();

        await new RealStoryGitOps().MergeUpstreamIntoBranchAsync(this.Repo(), Branch, "main");

        Assert.True(await IsAncestor(this.wtPath, remote, "HEAD"));
        Assert.False(await IsAncestor(this.wtPath, local, "HEAD"));
        Assert.Contains(local, await this.BackupRefTargetsAsync());
    }

    [Fact]
    public async Task Sync_BehindOrigin_ResetsToOriginAndDiscardsTrackedEdits()
    {
        if (!this.gitAvailable) return;
        var remote = await this.PushToBranchFromElsewhereAsync("remote.txt", "remote\n");
        await Git(this.wtPath, "fetch", "origin", Branch);
        await File.WriteAllTextAsync(Path.Combine(this.wtPath, "ws.txt"), "uncommitted edit\n");

        var kept = await new RealStoryGitOps().SyncWithOriginKeepingLocalCommitsAsync(this.wtPath, Branch, default);

        Assert.Equal(0, kept);
        Assert.Equal(remote, await RevParse(this.wtPath, "HEAD"));
        Assert.Equal("ws\n", (await File.ReadAllTextAsync(Path.Combine(this.wtPath, "ws.txt"))).Replace("\r\n", "\n"));
        Assert.Empty(await this.BackupRefTargetsAsync());
    }

    private RepoRow Repo() => new() { Id = "repo-keep", Kind = "github", CloneRoot = this.cloneDir, DefaultBranch = "main" };

    private async Task AdvanceMainAsync()
    {
        await Git(this.otherDir, "checkout", "main");
        await CommitFile(this.otherDir, "main-next.txt", "next\n", "main moves on");
        await Git(this.otherDir, "push", "origin", "main");
    }

    private async Task<string> PushToBranchFromElsewhereAsync(string file, string content)
    {
        await Git(this.otherDir, "fetch", "origin");
        await Git(this.otherDir, "checkout", "-B", Branch, $"origin/{Branch}");
        var sha = await CommitFile(this.otherDir, file, content, "pushed from elsewhere");
        await Git(this.otherDir, "push", "origin", Branch);
        await Git(this.otherDir, "checkout", "main");
        return sha;
    }

    private async Task<List<string>> BackupRefTargetsAsync()
    {
        var (_, output, _) = await CaptureGit(this.cloneDir, "for-each-ref", "--format=%(objectname)", "refs/forge/backup/");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static async Task Configure(string dir)
    {
        await Git(dir, "config", "user.email", "test@forge.local");
        await Git(dir, "config", "user.name", "Forge Test");
        await Git(dir, "config", "core.autocrlf", "false");
    }

    private static async Task<string> CommitFile(string dir, string file, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await Git(dir, "add", file);
        await Git(dir, "commit", "-m", message);
        return await RevParse(dir, "HEAD");
    }

    private static async Task<string> RevParse(string dir, string rev) => (await CaptureGit(dir, "rev-parse", rev)).StdOut.Trim();

    private static async Task<bool> IsAncestor(string dir, string ancestor, string descendant)
        => (await CaptureGit(dir, "merge-base", "--is-ancestor", ancestor, descendant)).Code == 0;

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            p.WaitForExit(2_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task Git(string cwd, params string[] args)
    {
        var (code, _, stderr) = await CaptureGit(cwd, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in '{cwd}': {stderr}");
        }
    }

    private static async Task<(int Code, string StdOut, string StdErr)> CaptureGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}