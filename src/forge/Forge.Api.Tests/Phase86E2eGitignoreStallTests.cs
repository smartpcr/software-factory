// -----------------------------------------------------------------------
// <copyright file="Phase86E2eGitignoreStallTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 86 regression — the qa-e2e workstream commit path
/// (<see cref="StorySpawnOrchestrator.DriveOneQaE2eWorkstreamAsync"/>)
/// previously had no catch for
/// <see cref="GitIgnoredPathException"/> thrown by
/// <see cref="IStoryGitOps.CommitFilesAsync"/>. The producer ran the
/// LLM (costing $$$), the evaluator scored the in-memory file content
/// as pass, but the commit threw and the orchestrator's outer sweep
/// re-spawned the workstream on every tick — infinite retry loop. The
/// fix calls
/// <see cref="IStoryGitOps.EnsurePathsCommittableAsync"/> as a
/// best-effort preflight (auto-fixes OneBranch-style root-level
/// allowlists with a narrow <c>!/tests/</c> exception) and wraps the
/// commit + merge in a try/catch that flips the workstream to
/// <c>Status=stalled</c> on any leftover gitignore block instead of
/// bubbling — operators see an actionable stall reason instead of a
/// silent retry loop.
///
/// <para>The auto-fix recovery machinery itself (the
/// <see cref="RealStoryGitOps.ComputeGitignoreNarrowExceptions"/>
/// helper + the <c>EnsurePlanDocPathsCommittableAsync</c> guts that
/// the new alias delegates to) is already covered by
/// <c>Phase85GitignoreAutoFixTests</c>. These tests focus on the new
/// surface: the alias correctly delegates, the
/// <c>baseDir="tests"</c> case produces the expected single-segment
/// rule, and the full pipeline (preflight + commit) lands a real
/// qa-e2e file (<c>tests/Features/*.feature</c>) on a OneBranch
/// fixture without throwing.</para>
/// </summary>
public class Phase86E2eGitignoreStallTests : IAsyncLifetime
{
    private readonly List<string> tempDirs = new();

    // OneBranch-style allowlist: ignore all root-level dirs, re-include /src/.
    // tests/ is therefore initially blocked — exactly the case that hit
    // workstream ws-local-update-history-in-cl-...-skeleton-e2e in production.
    private const string AllowlistGitignore =
        "###\nOneBranch Specific\n###\n\n# Everything outside src is ignored\n/*\n/*/\n!/src/\n";

    private const string Branch = "story/local-PHASE86-STORY/qa-e2e";
    private const string Slug = "qa-e2e-local-PHASE86-STORY";
    private const string E2eBaseDir = "tests";

    private const string FeaturePath =
        "tests/Features/local-PHASE86-STORY/sample.feature";

    private const string StepsPath =
        "tests/Steps/local-PHASE86-STORY/SampleSteps.cs";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in this.tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    // ── 1. ComputeGitignoreNarrowExceptions("tests") — single-segment rule ──

    [Fact]
    public void ComputeRules_TestsBaseDir_ReturnsSingleUnIgnore()
    {
        // The qa-e2e default baseDir is "tests" — a single segment, so the
        // helper should emit ONLY `!/tests/` (no parent-chain re-ignore is
        // needed; nothing above it is ignored to begin with).
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions(E2eBaseDir);

        Assert.Equal(new[] { "!/tests/" }, rules);
    }

    // ── 2. EnsurePathsCommittableAsync alias correctly auto-fixes "tests/" ──

    [Fact]
    public async Task EnsurePathsCommittableAsync_BlockedTestsPath_AppliesAutoFix()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-e2e-autofix", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        // Pre-condition: the qa-e2e feature path is gitignored on this branch.
        await Assert.ThrowsAsync<GitIgnoredPathException>(() =>
            git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, new[] { FeaturePath }));

        // Apply the auto-fix via the Phase 86 alias (the entry point the
        // orchestrator calls). The alias must thread the call through to the
        // same internals as EnsurePlanDocPathsCommittableAsync.
        await git.EnsurePathsCommittableAsync(
            repo, Branch, new[] { FeaturePath, StepsPath }, E2eBaseDir);

        // Post-condition: the paths are committable; the .gitignore prep
        // commit landed on this branch and contains the expected rule.
        await git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, new[] { FeaturePath, StepsPath });

        var headMsg = await GitCapture(wtPath, "log", "-1", "--pretty=%s");
        Assert.Contains("Forge auto-fix", headMsg);
        Assert.Contains("tests", headMsg);

        var gitignoreOnBranch = await File.ReadAllTextAsync(Path.Combine(wtPath, ".gitignore"));
        Assert.Contains("!/tests/", gitignoreOnBranch);
    }

    // ── 3. Idempotent — calling twice doesn't double-commit ───────────────

    [Fact]
    public async Task EnsurePathsCommittableAsync_SecondCall_IsIdempotent()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-e2e-idempotent", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        await git.EnsurePathsCommittableAsync(
            repo, Branch, new[] { FeaturePath }, E2eBaseDir);

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        // Second call: preflight already passes (the `!/tests/` rule is on
        // the branch), so the auto-fix path is skipped. No new commit.
        await git.EnsurePathsCommittableAsync(
            repo, Branch, new[] { FeaturePath }, E2eBaseDir);

        var sha2 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha1, sha2);
    }

    // ── 4. Full pipeline — preflight + CommitFilesAsync lands a real feature

    [Fact]
    public async Task EnsurePathsCommittableAsync_FullPipeline_FeatureFileLands()
    {
        if (!GitAvailable()) return;

        // Demonstrates the orchestrator's new commit sequence end-to-end:
        // (1) EnsurePathsCommittableAsync auto-fixes the .gitignore on the
        // branch, (2) the subsequent CommitFilesAsync (which has its own
        // per-commit fail-fast guard from Phase 81) goes through without
        // throwing GitIgnoredPathException — proving the fix works for the
        // qa-e2e file shapes (tests/Features/*.feature + tests/Steps/*.cs).
        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-e2e-pipeline", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        var files = new Dictionary<string, string>
        {
            [FeaturePath] = "Feature: sample\n  Scenario: x\n    Given y\n",
            [StepsPath] = "namespace X;\npublic class SampleSteps { }\n",
        };

        await git.EnsurePathsCommittableAsync(repo, Branch, files.Keys, E2eBaseDir);
        await git.CommitFilesAsync(repo, Branch, Slug, files, "qa-e2e: feature + steps");

        Assert.True(await git.FileExistsAsync(repo, Branch, FeaturePath),
            "qa-e2e .feature should land after auto-fix + CommitFilesAsync");
        Assert.True(await git.FileExistsAsync(repo, Branch, StepsPath),
            "qa-e2e Steps.cs should land after auto-fix + CommitFilesAsync");
    }

    // ── 5. Empty path list is a no-op for the alias ───────────────────────

    [Fact]
    public async Task EnsurePathsCommittableAsync_EmptyPaths_IsNoOp()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-e2e-empty", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        var sha0 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        await git.EnsurePathsCommittableAsync(
            repo, Branch, Array.Empty<string>(), E2eBaseDir);

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha0, sha1);
    }

    // ── 6. /src/ allowlisted path is a no-op (operator-configured baseDir) ─

    [Fact]
    public async Task EnsurePathsCommittableAsync_AllowlistedBaseDir_NoCommit()
    {
        if (!GitAvailable()) return;

        // Operators who configure their qa-e2e baseDir to `src/tests` (which
        // IS allowlisted by OneBranch's `!/src/`) shouldn't trigger any
        // auto-fix — the preflight finds the paths already committable and
        // returns without touching .gitignore.
        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-e2e-allowed", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        var sha0 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        await git.EnsurePathsCommittableAsync(
            repo, Branch,
            new[] { "src/tests/Features/local-PHASE86-STORY/sample.feature" },
            "src/tests");

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha0, sha1);
    }

    // ── fixture + git helpers (mirrors Phase85GitignoreAutoFixTests) ──────

    private async Task<string> CreateRepoAsync(string gitignore)
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), $"forge-phase86-remote-{Guid.NewGuid():N}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"forge-phase86-seed-{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"forge-phase86-clone-{Guid.NewGuid():N}");
        this.tempDirs.Add(remoteDir);
        this.tempDirs.Add(seedDir);
        this.tempDirs.Add(cloneDir);

        Directory.CreateDirectory(remoteDir);
        await Git(remoteDir, "init", "--bare", "-b", "main");

        Directory.CreateDirectory(seedDir);
        await Git(seedDir, "init", "-b", "main");
        await Git(seedDir, "config", "user.email", "test@forge.local");
        await Git(seedDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(seedDir, ".gitignore"), gitignore);
        // Seed a tracked file inside the /src/ allowlist so the repo isn't
        // empty after the OneBranch ignore applies.
        Directory.CreateDirectory(Path.Combine(seedDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(seedDir, "src", ".keep"), string.Empty);
        await Git(seedDir, "add", "-f", ".gitignore", "src/.keep");
        await Git(seedDir, "commit", "-m", "seed");
        await Git(seedDir, "remote", "add", "origin", remoteDir);
        await Git(seedDir, "push", "-u", "origin", "main");

        Directory.CreateDirectory(cloneDir);
        await Git(Path.GetTempPath(), "clone", remoteDir, cloneDir);
        await Git(cloneDir, "config", "user.email", "test@forge.local");
        await Git(cloneDir, "config", "user.name", "Forge Test");
        return cloneDir;
    }

    private static bool GitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task Git(string cwd, params string[] args)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed in '{cwd}' (exit {p.ExitCode}):\n{stdout}\n{stderr}");
        }
    }

    private static async Task<string> GitCapture(string cwd, params string[] args)
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
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }
}
