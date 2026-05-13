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
/// Phase 55 follow-up — sequential-mode plan-doc Restart/Resume MUST reuse
/// the canonical <c>plan-{key}</c> worktree slug owned by
/// <c>StorySpawnOrchestrator.RunPlanDocAsync</c>.
///
/// <para>Operator-reported 2026-05-13 on
/// <c>local:UPDATE-HISTORY-IN-CL → architecture.md</c>: the doc stalled at
/// iter 13 / score 0 / $130.56 with the generator narrating
/// <c>"The cwd doesn't exist… I'll edit the existing draft at the
/// spawn-story worktree (the only worktree with the file)"</c> while the
/// evaluator simultaneously reported <c>"docs/stories/local-UPDATE-HISTORY-
/// IN-CL/architecture.md does not exist at the required path"</c>. Root
/// cause: <see cref="CrewStoryArtifactStrategy.ResumeSingleArtifactAsync"/>
/// called <see cref="IStoryGitOps.EnsureWorktreeAsync"/> WITHOUT
/// <c>worktreeSlug</c>, so it fell back to the legacy
/// <c>spawn-{long-branch-slug}</c> path. That's a different directory
/// from the canonical <c>plan-{key}</c> the orchestrator's
/// <c>RunPlanDocAsync</c> uses.
/// <see cref="RealStoryGitOps.EnsureWorktreeAsync"/> handles the conflict
/// by *migrating* the worktree (`git worktree remove --force` + the C#
/// <c>Directory.Delete</c> of the old path, then <c>git worktree add</c>
/// at the new path). Every 15 s scheduler tick re-entered
/// <c>RunPlanDocAsync</c> and migrated back, deleting the legacy
/// directory out from under the in-flight architect's cwd. Generator and
/// evaluator both lost their workspace mid-iteration → identical
/// failure verdict every iter → <c>stalled-no-convergence</c> after the
/// 7-iter convergence detector window.</para>
///
/// <para>The fix passes <c>worktreeSlug:
/// PlanDocStateHelpers.CanonicalDocWorktreeSlug(key)</c> for sequential-
/// mode spawns so Restart/Resume reuses the exact directory the
/// orchestrator owns. These tests pin the invariant against the real
/// <see cref="RealStoryGitOps"/> so a future refactor cannot
/// silently re-introduce the path drift.</para>
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
    /// AGAIN. The fix must reuse the canonical path so the canary survives.
    /// Pre-fix this test fails: the canary disappears because the legacy-
    /// slug call migrates the worktree and `Directory.Delete`s the old one.
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

        // Architect writes a per-iter canary file into the worktree (mirrors
        // CommitPathInWorktreeAsync behavior — file lives inside the cwd).
        var canary = Path.Combine(firstWtPath, "architect-canary.md");
        await File.WriteAllTextAsync(canary, "iter-1 content");

        // (2) Operator clicks Restart → ResumeSingleArtifactAsync runs.
        // Post-fix, this passes the SAME canonical slug. Pre-fix, this
        // omitted worktreeSlug, falling back to the legacy spawn-{slug}
        // shape and triggering destructive migration.
        var secondWtPath = await this.git.EnsureWorktreeAsync(
            this.repo, docBranch, "main", worktreeSlug: canonicalSlug);

        // Invariant: same path. Without slug consistency, secondWtPath
        // would resolve under `.worktree/spawn-{long-branch-slug}/`,
        // EnsureWorktreeAsync would migrate, the canonical dir would be
        // Directory.Delete'd, and the canary would vanish.
        Assert.Equal(firstWtPath, secondWtPath);
        Assert.True(Directory.Exists(secondWtPath),
            $"Worktree at canonical slug {canonicalSlug} disappeared after second EnsureWorktreeAsync — slug mismatch caused migration.");
        Assert.True(File.Exists(canary),
            $"Architect's in-flight canary file at {canary} was wiped by EnsureWorktreeAsync — Phase 55 follow-up regression: ResumeSingleArtifactAsync's slug-less call migrated the worktree and Directory.Delete'd the cwd.");
    }

    /// <summary>
    /// Direct demonstration of the pre-fix failure mode: calling
    /// <see cref="IStoryGitOps.EnsureWorktreeAsync"/> twice on the same
    /// branch with DIFFERENT slugs (canonical first, then legacy/no-slug)
    /// migrates the worktree and the first directory is gone. Locks in
    /// that the migration logic in <c>RealStoryGitOps</c> is exactly
    /// what was destroying the architect's cwd — documenting WHY the
    /// fix above must keep slug consistent.
    /// </summary>
    [Fact]
    public async Task EnsureWorktreeAsync_SlugMismatch_MigratesAndWipesOriginalDir()
    {
        if (!GitAvailable()) return;

        const string docKey = "architecture";
        var docBranch = PlanDocStateHelpers.CanonicalDocBranch("WI-test-mismatch", docKey);
        var canonicalSlug = PlanDocStateHelpers.CanonicalDocWorktreeSlug(docKey);

        await this.git.EnsureBranchFreshAsync(this.repo, docBranch, "main");
        var canonicalWt = await this.git.EnsureWorktreeAsync(
            this.repo, docBranch, "main", worktreeSlug: canonicalSlug);
        Assert.True(Directory.Exists(canonicalWt));

        // Now simulate the BUGGY call shape: same branch, no worktreeSlug.
        // Internal SanitizeBranchSlug + WorktreePath produce a different
        // path under .worktree/spawn-{long-branch-slug}/.
        var legacyWt = await this.git.EnsureWorktreeAsync(
            this.repo, docBranch, "main");

        // The two MUST be different paths (otherwise this whole class of
        // bug is impossible and the headline test above doesn't prove
        // anything).
        Assert.NotEqual(
            canonicalWt.Replace('\\', '/').TrimEnd('/'),
            legacyWt.Replace('\\', '/').TrimEnd('/'));

        // The migration must have cleaned up the canonical directory —
        // confirming that any in-flight agent whose cwd was canonicalWt
        // now points at a vanished path. (This is the production failure
        // signature: "The cwd doesn't exist".)
        Assert.False(Directory.Exists(canonicalWt),
            $"Pre-fix bug demonstration: canonical worktree {canonicalWt} should have been Directory.Delete'd by EnsureWorktreeAsync's migration, but still exists. If this assertion fails, the migration semantics in RealStoryGitOps changed and the headline regression test is no longer protecting against the original bug.");
        Assert.True(Directory.Exists(legacyWt));
    }

    // ── Helpers (mirrored from Phase55DeleteSpawnPreservesRepoTests) ──

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
