// -----------------------------------------------------------------------
// <copyright file="Phase85GitignoreAutoFixTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 85 regression — the orchestrator's plan-doc preflight no longer just
/// fails fast when the target repo's <c>.gitignore</c> blocks the doc path;
/// it now appends a narrow allowlist exception (<c>!/{baseDir}/</c> chain) as
/// a prep commit on the doc branch BEFORE any LLM cost is incurred.
///
/// <para>These exercise the recovery path end-to-end on a real temp repo:
/// (1) the static rule-builder covers each segment-depth case, (2) the
/// recovery routes a previously-blocked path to a clean commit, (3) the
/// recovery is idempotent on a follow-up call, (4) the unsafe-segment guard
/// rejects gitignore metacharacters, and (5) operator clarity holds when the
/// fix can't help (no worktree, etc.).</para>
/// </summary>
public class Phase85GitignoreAutoFixTests : IAsyncLifetime
{
    private readonly List<string> tempDirs = new();

    // OneBranch-style allowlist: ignore all root-level dirs, re-include /src/.
    // docs/stories is therefore initially blocked.
    private const string AllowlistGitignore =
        "###\nOneBranch Specific\n###\n\n# Everything outside src is ignored\n/*\n/*/\n!/src/\n";

    private const string Branch = "story/local-PHASE85-STORY/plan-architecture";
    private const string Slug = "plan-local-PHASE85-STORY-architecture";
    private const string PlanDocBaseDir = "docs/stories";
    private const string DocPath = "docs/stories/local-PHASE85-STORY/architecture.md";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in this.tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    // ── ComputeGitignoreNarrowExceptions — pure helper ─────────────────────

    [Fact]
    public void ComputeRules_SingleSegment_ReturnsUnIgnoreOnly()
    {
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions("docs");

        Assert.Equal(new[] { "!/docs/" }, rules);
    }

    [Fact]
    public void ComputeRules_TwoSegments_ReturnsParentChainPlusLeaf()
    {
        // /docs/ must be un-ignored, then re-ignore /docs/* to keep the un-ignore
        // footprint minimal (we don't want sibling /docs/architecture/ etc. to
        // become tracked), then un-ignore the stories subtree specifically.
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions("docs/stories");

        Assert.Equal(new[]
        {
            "!/docs/",
            "/docs/*",
            "!/docs/stories/",
        }, rules);
    }

    [Fact]
    public void ComputeRules_ThreeSegments_WalksAncestors()
    {
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions("docs/forge/stories");

        Assert.Equal(new[]
        {
            "!/docs/",
            "/docs/*",
            "!/docs/forge/",
            "/docs/forge/*",
            "!/docs/forge/stories/",
        }, rules);
    }

    [Fact]
    public void ComputeRules_LeadingAndTrailingSlashes_AreTrimmed()
    {
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions("/docs/stories/");

        Assert.Equal(new[]
        {
            "!/docs/",
            "/docs/*",
            "!/docs/stories/",
        }, rules);
    }

    [Fact]
    public void ComputeRules_BackslashSeparator_IsNormalized()
    {
        var rules = RealStoryGitOps.ComputeGitignoreNarrowExceptions(@"docs\stories");

        Assert.Equal(new[]
        {
            "!/docs/",
            "/docs/*",
            "!/docs/stories/",
        }, rules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("///")]
    public void ComputeRules_EmptyInput_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() =>
            RealStoryGitOps.ComputeGitignoreNarrowExceptions(input));
    }

    [Theory]
    [InlineData("docs/sto ries")]     // whitespace
    [InlineData("docs/sto*ries")]     // glob
    [InlineData("docs/!stories")]     // negation char
    [InlineData("docs/#stories")]     // comment char
    [InlineData("docs/sto[ries]")]    // character class
    [InlineData("docs/sto\nries")]    // newline injection
    [InlineData("docs/sto/../ries")]  // traversal slips past normalize (defense in depth)
    public void ComputeRules_UnsafeChars_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() =>
            RealStoryGitOps.ComputeGitignoreNarrowExceptions(input));
    }

    // ── EnsurePlanDocPathsCommittableAsync — recovery flow ──────────────────

    [Fact]
    public async Task EnsureCommittable_BlockedPath_AppliesAutoFixAndCommits()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-autofix", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        // Pre-condition: path is blocked.
        await Assert.ThrowsAsync<GitIgnoredPathException>(() =>
            git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, new[] { DocPath }));

        // Apply auto-fix.
        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, new[] { DocPath }, PlanDocBaseDir);

        // Post-condition: path is now committable on this branch.
        await git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, new[] { DocPath });

        // The branch has a new HEAD commit whose message identifies the auto-fix
        // and whose tree contains the rules we appended.
        var headMsg = await GitCapture(wtPath, "log", "-1", "--pretty=%s");
        Assert.Contains("Forge auto-fix", headMsg);
        Assert.Contains("docs/stories", headMsg);

        var gitignoreOnBranch = await File.ReadAllTextAsync(Path.Combine(wtPath, ".gitignore"));
        Assert.Contains("# Forge plan-doc allowlist (auto-fix)", gitignoreOnBranch);
        Assert.Contains("!/docs/", gitignoreOnBranch);
        Assert.Contains("/docs/*", gitignoreOnBranch);
        Assert.Contains("!/docs/stories/", gitignoreOnBranch);
    }

    [Fact]
    public async Task EnsureCommittable_AlreadyCommittable_IsNoOp()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-noop", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        var sha0 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        // /src/ is already on the allowlist — no auto-fix needed.
        var srcPath = "src/docs/stories/local-PHASE85-STORY/architecture.md";
        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, new[] { srcPath }, "src/docs/stories");

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha0, sha1);
    }

    [Fact]
    public async Task EnsureCommittable_SecondCall_IsIdempotent()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-idempotent", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, new[] { DocPath }, PlanDocBaseDir);

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        // Second call: preflight already passes (rules are on the branch),
        // so the auto-fix path is skipped entirely. No new commit.
        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, new[] { DocPath }, PlanDocBaseDir);

        var sha2 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha1, sha2);
    }

    [Fact]
    public async Task EnsureCommittable_UnsafeBaseDir_ThrowsBeforeTouchingGit()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-unsafe", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        // Glob char in baseDir would inject malformed gitignore patterns —
        // validator must reject up front, NOT silently corrupt .gitignore.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            git.EnsurePlanDocPathsCommittableAsync(
                repo, Branch, new[] { DocPath }, "docs/sto*ries"));
    }

    [Fact]
    public async Task EnsureCommittable_NoWorktree_ThrowsInvalidOperation()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-no-wt", Kind = "github", CloneRoot = cloneDir };

        // EnsureBranchAsync only, no EnsureWorktreeAsync — preflight has nothing
        // to evaluate against.
        await git.EnsureBranchAsync(repo, Branch, "main");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            git.EnsurePlanDocPathsCommittableAsync(
                repo, Branch, new[] { DocPath }, PlanDocBaseDir));
    }

    [Fact]
    public async Task EnsureCommittable_EmptyPathList_IsNoOp()
    {
        if (!GitAvailable()) return;

        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-empty", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        var wtPath = await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        var sha0 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();

        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, Array.Empty<string>(), PlanDocBaseDir);

        var sha1 = (await GitCapture(wtPath, "rev-parse", "HEAD")).Trim();
        Assert.Equal(sha0, sha1);
    }

    [Fact]
    public async Task EnsureCommittable_FullPipeline_PlanDocCommitsCleanlyAfterAutoFix()
    {
        if (!GitAvailable()) return;

        // Demonstrates the full integration: orchestrator-style preflight
        // applies the .gitignore fix, then the architect's CommitFilesAsync
        // (with its own per-commit preflight) goes through without throwing.
        var cloneDir = await this.CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-pipeline", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        await git.EnsurePlanDocPathsCommittableAsync(
            repo, Branch, new[] { DocPath }, PlanDocBaseDir);

        // This is exactly what RunPlanDocInnerAsync does after the preflight.
        await git.CommitFilesAsync(repo, Branch, Slug,
            new Dictionary<string, string> { [DocPath] = "# architecture\n" },
            "plan-architecture: artifact");

        Assert.True(await git.FileExistsAsync(repo, Branch, DocPath),
            "plan doc should land on the branch after auto-fix + CommitFilesAsync");
    }

    // ── fixture + git helpers ───────────────────────────────────────────────

    private async Task<string> CreateRepoAsync(string gitignore)
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), $"forge-phase85-remote-{Guid.NewGuid():N}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"forge-phase85-seed-{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"forge-phase85-clone-{Guid.NewGuid():N}");
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
        // seed a tracked file inside the /src/ allowlist so the repo isn't
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
