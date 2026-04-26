// -----------------------------------------------------------------------
// <copyright file="GitWorktreeServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 3.7 — exercises the git wrapper against a per-test temp repository.
/// Tests skip themselves when the local <c>git</c> binary is unavailable so
/// the suite stays green on machines without git installed.
/// </summary>
public class GitWorktreeServiceTests : IAsyncLifetime
{
    private readonly string repoDir = Path.Combine(Path.GetTempPath(), $"forge-git-{Guid.NewGuid():N}");
    private readonly string worktreeRoot;

    public GitWorktreeServiceTests()
    {
        this.worktreeRoot = Path.Combine(this.repoDir, "wt");
    }

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;
        Directory.CreateDirectory(this.repoDir);
        Directory.CreateDirectory(this.worktreeRoot);
        await RunGitCheckedAsync(this.repoDir, "init", "-b", "main");
        await RunGitCheckedAsync(this.repoDir, "config", "user.email", "test@forge.local");
        await RunGitCheckedAsync(this.repoDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(this.repoDir, "README.md"), "seed\n");
        await RunGitCheckedAsync(this.repoDir, "add", "README.md");
        await RunGitCheckedAsync(this.repoDir, "commit", "-m", "seed");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // Guard pattern: each test exits early when git isn't installed so the
    // suite stays green on minimal CI agents. xUnit v2 doesn't have a
    // built-in skip primitive without an extra NuGet dep.

    [Fact]
    public async Task ListAsync_Returns_The_Initial_Worktree()
    {
        if (!GitAvailable()) return;
        var svc = NewService(this.repoDir, this.worktreeRoot);
        var entries = await svc.ListAsync();
        Assert.Single(entries);
        Assert.Contains(entries, e => e.Branch.EndsWith("main") || e.Branch == "main");
    }

    [Fact]
    public async Task CreateAsync_Outside_Root_Throws_Unauthorized()
    {
        if (!GitAvailable()) return;
        var svc = NewService(this.repoDir, this.worktreeRoot);
        var stranger = Path.Combine(Path.GetTempPath(), $"forge-stranger-{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(stranger, "ws/escape", "main"));
    }

    [Fact]
    public async Task CreateAsync_Inside_Root_Adds_A_Worktree()
    {
        if (!GitAvailable()) return;
        var svc = NewService(this.repoDir, this.worktreeRoot);
        var path = Path.Combine(this.worktreeRoot, "feature-a");
        await svc.CreateAsync(path, "ws/feature-a", "main");

        var entries = await svc.ListAsync();
        Assert.Contains(entries, e => e.Path.Replace('\\', '/').EndsWith("wt/feature-a"));
    }

    [Fact]
    public async Task AheadBehindAsync_Counts_Diverging_Commits()
    {
        if (!GitAvailable()) return;
        var svc = NewService(this.repoDir, this.worktreeRoot);
        var path = Path.Combine(this.worktreeRoot, "feature-b");
        await svc.CreateAsync(path, "ws/feature-b", "main");
        await File.WriteAllTextAsync(Path.Combine(path, "feature.txt"), "branch\n");
        await RunGitCheckedAsync(path, "add", "feature.txt");
        await RunGitCheckedAsync(path, "commit", "-m", "branch commit");

        var ab = await svc.AheadBehindAsync("main", "ws/feature-b");
        Assert.Equal(1, ab.Ahead);
        Assert.Equal(0, ab.Behind);
    }

    [Fact]
    public async Task ConflictPathsAsync_Empty_When_Clean()
    {
        if (!GitAvailable()) return;
        var svc = NewService(this.repoDir, this.worktreeRoot);
        var path = Path.Combine(this.worktreeRoot, "feature-c");
        await svc.CreateAsync(path, "ws/feature-c", "main");
        var conflicts = await svc.ConflictPathsAsync("main", "ws/feature-c");
        Assert.Empty(conflicts);
    }

    private static GitWorktreeService NewService(string repoPath, string root) =>
        new(
            Options.Create(new GitWorktreeOptions { RepoPath = repoPath, WorktreeRoot = root }),
            NullLogger<GitWorktreeService>.Instance);

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

    private static async Task RunGitCheckedAsync(string cwd, params string[] args)
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
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
        }
    }
}

