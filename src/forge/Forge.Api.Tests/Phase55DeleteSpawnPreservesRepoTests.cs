// -----------------------------------------------------------------------
// <copyright file="Phase55DeleteSpawnPreservesRepoTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 55 — pin the "DeleteSpawn must never wipe the cloneRoot" guarantee.
/// Operator hit this on 2026-05-13 deleting spawn
/// <c>code-intelligence:AGENT-MEMORY</c> against a repo whose main worktree
/// was on <c>main</c>: DeleteSpawn step 6a.1 called
/// <see cref="IStoryGitOps.RemoveWorktreeAsync"/> with the base branch
/// (<c>"main"</c>). <c>RealStoryGitOps.TryFindExistingWorktreeAsync</c>
/// scans <c>git worktree list --porcelain</c>, finds the main worktree on
/// <c>main</c>, returns the cloneRoot itself. <c>RemoveWorktreeAsync</c>
/// then ran <c>Directory.Delete(cloneRoot, recursive: true)</c> — wiping
/// the entire repo including <c>.git</c>. User report:
/// <c>"plan failed: git fetch origin main failed (exit 128) in
/// 'E:\forge\workflow'. stderr: fatal: not a git repository"</c>.
///
/// <para>The fix has two layers:</para>
/// <list type="number">
///   <item><see cref="RealStoryGitOps"/>'s <c>RemoveWorktreeAsync</c> and
///         <c>EnsureWorktreeAsync</c> refuse to <c>Directory.Delete</c> any
///         worktree path that resolves to the cloneRoot. Detection is
///         two-pronged: normalized path equality against
///         <c>repo.CloneRoot</c> plus a symlink-resilient structural check
///         (linked worktrees' <c>.git</c> is a pointer FILE, main worktree's
///         <c>.git</c> is a DIRECTORY).</item>
///   <item><c>StoriesEndpoints.DeleteSpawn</c> step 6a.1 (the obsolete
///         <c>RemoveWorktreeAsync(repo, spawn.BaseBranch, ...)</c> call)
///         was removed — Phase 49 long since stopped creating a
///         <c>spawn-{base}</c> worktree from EnsureBranchAsync, so the
///         step was both stale AND actively destructive.</item>
/// </list>
///
/// <para>Why not also restrict <c>TryFindExistingWorktreeAsync</c>?
/// <see cref="IStoryGitOps.MergeAsync"/> and
/// <c>RebaseBaseBranchAsync</c> rely on the helper returning the main
/// worktree as the merge target — they use it for <c>git reset --hard
/// origin/{base}</c>, which is an intentional Forge sync semantic (Forge
/// owns the CloneRoot). Restricting the helper's contract would break
/// the every-post-PR-merge sync flow. The guard belongs at the
/// destructive <c>Directory.Delete</c> callsites, not in the listing
/// helper.</para>
///
/// <para>These tests exercise the real <see cref="RealStoryGitOps"/> against
/// a real temp git repo so the guarantee stays intact regardless of future
/// refactors.</para>
/// </summary>
public class Phase55DeleteSpawnPreservesRepoTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-ph55-{Guid.NewGuid():N}");
    private RealStoryGitOps git = null!;
    private RepoRow repo = null!;
    private string clonePath = string.Empty;
    private string originPath = string.Empty;

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        // Bare "origin" + a local clone whose main worktree stays ON `main`
        // (the production state Phase49RebaseOnClaimTests deliberately
        // avoided with a `parking` branch checkout — that's how the bug
        // hid for so long).
        Directory.CreateDirectory(this.repoRoot);
        this.originPath = Path.Combine(this.repoRoot, "origin.git");
        this.clonePath = Path.Combine(this.repoRoot, "clone");

        await RunAsync(this.repoRoot, "git", "init", "--bare", "-b", "main", this.originPath);
        await RunAsync(this.repoRoot, "git", "clone", this.originPath, this.clonePath);
        await ConfigureGitIdentityAsync(this.clonePath);

        // Initial commit so origin/main has content the assertions can
        // verify still exists after the destructive call attempts.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# canary file\n");
        await RunAsync(this.clonePath, "git", "add", ".");
        await RunAsync(this.clonePath, "git", "commit", "-m", "Initial");
        await RunAsync(this.clonePath, "git", "push", "-u", "origin", "main");

        // CRITICAL — DO NOT switch onto a parking branch. The production
        // scenario the bug fires under has the main worktree on `main`.
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
    /// Headline regression: <see cref="IStoryGitOps.RemoveWorktreeAsync"/>
    /// against the base branch (when the main worktree is checked out on
    /// it) MUST NOT delete the cloneRoot. Pre-fix this wiped
    /// <c>E:\forge\workflow</c> for the user.
    /// </summary>
    [Fact]
    public async Task RemoveWorktreeAsync_BaseBranchOnMainWorktree_DoesNotDeleteCloneRoot()
    {
        if (!GitAvailable()) return;

        // Sanity: the clone's main worktree is on `main` with .git intact.
        Assert.True(Directory.Exists(this.clonePath));
        Assert.True(Directory.Exists(Path.Combine(this.clonePath, ".git")));
        Assert.True(File.Exists(Path.Combine(this.clonePath, "README.md")));

        // Act: this is exactly the call DeleteSpawn step 6a.1 used to make.
        await this.git.RemoveWorktreeAsync(this.repo, "main", CancellationToken.None);

        // Assert: cloneRoot survived, .git survived, the canary file survived.
        Assert.True(Directory.Exists(this.clonePath),
            $"cloneRoot {this.clonePath} was wiped by RemoveWorktreeAsync — Phase 55 regression.");
        Assert.True(Directory.Exists(Path.Combine(this.clonePath, ".git")),
            ".git folder was deleted — repo is no longer a git repository.");
        Assert.True(File.Exists(Path.Combine(this.clonePath, "README.md")),
            "Working-tree file was wiped — cloneRoot contents were destroyed.");
    }

    /// <summary>
    /// Repeated calls are idempotent and equally safe — operators clicking
    /// the delete button twice (or DeleteSpawn re-running after a partial
    /// failure) must not progressively damage the repo.
    /// </summary>
    [Fact]
    public async Task RemoveWorktreeAsync_BaseBranch_RepeatedCalls_StaySafe()
    {
        if (!GitAvailable()) return;

        for (var i = 0; i < 3; i++)
        {
            await this.git.RemoveWorktreeAsync(this.repo, "main", CancellationToken.None);
        }

        Assert.True(Directory.Exists(Path.Combine(this.clonePath, ".git")));
        Assert.True(File.Exists(Path.Combine(this.clonePath, "README.md")));
    }

    /// <summary>
    /// A linked worktree on a non-base branch MUST still be removable — the
    /// callsite guards only refuse to delete the cloneRoot itself, never
    /// legitimately linked sibling worktrees.
    /// </summary>
    [Fact]
    public async Task RemoveWorktreeAsync_LinkedWorktreeOnSpawnBranch_StillCleansUp()
    {
        if (!GitAvailable()) return;

        var wsBranch = "ws/test/stage-1-1";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "main");
        var wt = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "main", worktreeSlug: "stage-1-1-test");

        Assert.True(Directory.Exists(wt), "Linked worktree was not created.");

        // Act: remove the linked worktree.
        await this.git.RemoveWorktreeAsync(this.repo, wsBranch, CancellationToken.None);

        // Assert: linked worktree gone, cloneRoot still intact.
        Assert.False(Directory.Exists(wt),
            $"Linked worktree {wt} should have been deleted but survived.");
        Assert.True(Directory.Exists(Path.Combine(this.clonePath, ".git")),
            "Removing a linked worktree must not affect the cloneRoot.");
        Assert.True(File.Exists(Path.Combine(this.clonePath, "README.md")));
    }

    /// <summary>
    /// Defense-in-depth: even if a future regression in
    /// <c>TryFindExistingWorktreeAsync</c> or operator state lets the
    /// cloneRoot through as the resolved worktree, the second layer of
    /// protection (the explicit cloneRoot equality guard in
    /// <c>RemoveWorktreeAsync</c>) prevents the wipe. Verified here by
    /// calling against <c>"main"</c> and checking <c>.git</c> remains a
    /// valid git directory.
    /// </summary>
    [Fact]
    public async Task RemoveWorktreeAsync_BaseBranch_GitFolderRemainsAGitDirectory()
    {
        if (!GitAvailable()) return;

        await this.git.RemoveWorktreeAsync(this.repo, "main", CancellationToken.None);

        // Verify the repo is still a working git checkout — the user's
        // production failure manifested as `git fetch` failing with
        // "fatal: not a git repository". This `git status` is the most
        // direct test that the .git metadata is intact.
        var (code, _, stderr) = await CaptureAsync(this.clonePath, "git", "status");
        Assert.True(code == 0,
            $"`git status` must succeed against cloneRoot after RemoveWorktreeAsync(main). "
            + $"stderr: {stderr}");
    }

    /// <summary>
    /// Rubber-duck blind-spot pin: <see cref="IStoryGitOps.MergeAsync"/>
    /// also reuses the main worktree as its merge target by calling the
    /// internal <c>TryFindExistingWorktreeAsync</c> helper and running
    /// <c>git reset --hard origin/{base}</c> in the result. That reset
    /// IS intentional under the Forge contract (Forge.Api owns its
    /// CloneRoot; operator-uncommitted state isn't expected). But the
    /// helper must NEVER let MergeAsync's <c>worktree remove --force</c>
    /// / <c>Directory.Delete</c> followups (Phase 51 Fix 6b cleanup at
    /// RealStoryGitOps.cs:321-336) wipe the cloneRoot. Verified here by
    /// running MergeAsync against the base branch on a clone whose main
    /// worktree is on the base branch, and asserting the repo survives:
    /// cloneRoot intact, `.git` intact, untracked files intact (reset
    /// --hard leaves them alone), `git status` works afterward.
    /// </summary>
    [Fact]
    public async Task MergeAsync_AgainstBaseBranch_DoesNotWipeCloneRoot()
    {
        if (!GitAvailable()) return;

        // Untracked canary: `reset --hard` does NOT remove untracked files,
        // so this should survive intentional Forge merge behavior. A
        // Directory.Delete on the cloneRoot would wipe it — that's the
        // catastrophic regression we're guarding against.
        var untrackedCanary = Path.Combine(this.clonePath, "OPERATOR-SCRATCH.txt");
        await File.WriteAllTextAsync(untrackedCanary, "do not touch\n");

        // Act: this is the same call shape the orchestrator makes after a
        // workstream PR merges into origin/main. Source==Base==main so the
        // merge is a sync (origin/main is already where local main wants
        // to be). The Phase 55 fix must not turn this into a repo-wipe.
        await this.git.MergeAsync(this.repo, "main", "main", CancellationToken.None);

        // Assert: cloneRoot, .git, and the untracked canary all survive.
        Assert.True(Directory.Exists(this.clonePath),
            $"cloneRoot {this.clonePath} was wiped by MergeAsync.");
        Assert.True(Directory.Exists(Path.Combine(this.clonePath, ".git")),
            ".git folder was deleted by MergeAsync — repo is no longer a git repository.");
        Assert.True(File.Exists(untrackedCanary),
            "MergeAsync deleted the operator's untracked file (Directory.Delete pattern).");

        // Assert: the cloneRoot is still a working git repo.
        var (code, _, stderr) = await CaptureAsync(this.clonePath, "git", "status");
        Assert.True(code == 0,
            $"`git status` must succeed against cloneRoot after MergeAsync(main, main). "
            + $"stderr: {stderr}");
    }

    // ── Helpers (mirrored from Phase49RebaseOnClaimTests) ─────────────────

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
            p.WaitForExit(3_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
