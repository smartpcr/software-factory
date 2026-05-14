// -----------------------------------------------------------------------
// <copyright file="Phase55ResumeWorktreeSlugTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Sequential-mode plan-doc Restart/Resume MUST reuse the canonical
/// <c>plan-{key}</c> worktree slug the orchestrator owns. The legacy
/// <c>spawn-{long-branch-slug}</c> fallback path is removed entirely, so
/// every caller of <see cref="IStoryGitOps.EnsureWorktreeAsync"/> must
/// supply a canonical slug. These tests pin the invariant against the
/// real <see cref="RealStoryGitOps"/>.
/// </summary>
public class Phase55ResumeWorktreeSlugTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-ph55slug-{Guid.NewGuid():N}");
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

        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# canary\n");
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
    /// Headline regression: simulating the production sequence — orchestrator
    /// creates the canonical worktree, the architect writes a per-iter
    /// canary into it, then a Restart fires <see cref="IStoryGitOps.EnsureWorktreeAsync"/>
    /// AGAIN with the SAME canonical slug. The path must be reused so the
    /// canary survives. The directory must live under
    /// <c>.worktree/plan-{key}</c> with no <c>spawn-</c> prefix.
    /// </summary>
    [Fact]
    public async Task EnsureWorktreeAsync_RepeatedCanonicalSlug_PreservesArchitectsInflightWorkspace()
    {
        if (!GitAvailable()) return;

        const string docKey = "architecture";
        var docBranch = PlanDocStateHelpers.CanonicalDocBranch("WI-test", docKey);
        var canonicalSlug = PlanDocStateHelpers.CanonicalDocWorktreeSlug(docKey);

        // (1) First drive — orchestrator's RunPlanDocAsync shape.
        await this.git.EnsureBranchFreshAsync(this.repo, docBranch, "main");
        var firstWtPath = await this.git.EnsureWorktreeAsync(
            this.repo, docBranch, "main", worktreeSlug: canonicalSlug);

        // Path is the canonical short slug with NO spawn- prefix.
        Assert.EndsWith(Path.Combine(".worktree", "plan-architecture"), firstWtPath);
        Assert.DoesNotContain($"spawn-", firstWtPath);

        // Architect writes a per-iter canary file into the worktree (mirrors
        // CommitPathInWorktreeAsync behavior — file lives inside the cwd).
        var canary = Path.Combine(firstWtPath, "architect-canary.md");
        await File.WriteAllTextAsync(canary, "iter-1 content");

        // (2) Operator clicks Restart → ResumeSingleArtifactAsync runs with
        // the SAME canonical slug.
        var secondWtPath = await this.git.EnsureWorktreeAsync(
            this.repo, docBranch, "main", worktreeSlug: canonicalSlug);

        // Same path; canary survived.
        Assert.Equal(firstWtPath, secondWtPath);
        Assert.True(Directory.Exists(secondWtPath));
        Assert.True(File.Exists(canary),
            $"Architect's in-flight canary file at {canary} was wiped by EnsureWorktreeAsync.");
    }

    /// <summary>
    /// Callers must supply <c>worktreeSlug</c>. The legacy
    /// <c>spawn-{branch-slug}</c> fallback was the root cause of the
    /// 2026-05-13 cwd-vanish incident and is removed entirely. Verify the
    /// contract: a missing slug throws immediately rather than silently
    /// materialising a non-canonical directory.
    /// </summary>
    [Fact]
    public async Task EnsureWorktreeAsync_WithoutSlug_Throws()
    {
        if (!GitAvailable()) return;

        const string docKey = "architecture";
        var docBranch = PlanDocStateHelpers.CanonicalDocBranch("WI-test-mismatch", docKey);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await this.git.EnsureWorktreeAsync(this.repo, docBranch, "main");
        });
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
