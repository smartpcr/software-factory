// -----------------------------------------------------------------------
// <copyright file="Phase81PlanDocGitignoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 81 / 83 regression — committing story plan docs into a repo whose
/// <c>.gitignore</c> is an allowlist (OneBranch / SDL: <c>/*</c> + <c>/*/</c>
/// + <c>!/src/</c>).
///
/// <para>Background (2026-05-24): the <c>update</c> and <c>common-infra</c>
/// msazure repos halted the plan phase with "Refusing to commit … excluded by
/// .gitignore" because the default plan-doc location <c>docs/stories/{story}/</c>
/// is outside the allowlisted <c>/src/</c> tree. We learned the hard way that a
/// <c>.gitignore</c> re-include (<c>!docs/stories/**</c>) does NOT reliably work
/// under a <c>/*</c> allowlist: git refuses to re-include a file whose parent
/// directory is excluded, and the negation evaluation differs in a linked
/// worktree. The robust fix is <see cref="RepoRow.PlanDocBaseDir"/> =
/// <c>src/docs/stories</c> — placing docs UNDER the already-allowlisted
/// <c>/src/</c> tree, which needs no negation at all.</para>
///
/// <para>These drive a real temp git repo through
/// <see cref="RealStoryGitOps.CommitFilesAsync"/> (which runs the Phase 81
/// <c>git check-ignore</c> preflight) and assert: (1) the guard correctly
/// REFUSES a doc under <c>docs/</c> (git-ignored, would silently no-op), and
/// (2) the same doc UNDER <c>src/</c> commits cleanly. Skips when <c>git</c> is
/// unavailable.</para>
/// </summary>
public class Phase81PlanDocGitignoreTests : IAsyncLifetime
{
    private readonly List<string> tempDirs = new();

    // OneBranch-style allowlist: ignore the whole root, re-include only /src/.
    // docs/stories therefore starts ignored; src/** is committable.
    private const string AllowlistGitignore =
        "###\nOneBranch Specific\n###\n\n# Everything outside src is ignored\n/*\n/*/\n!/src/\n";

    private const string Branch = "story/local-TEST-STORY/plan-architecture";
    private const string Slug = "plan-local-TEST-STORY-architecture";
    private const string DocUnderDocs = "docs/stories/local-TEST-STORY/architecture.md";
    private const string DocUnderSrc = "src/docs/stories/local-TEST-STORY/architecture.md";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in this.tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AllowlistRepo_PlanDocUnderDocs_CommitIsRefused()
    {
        if (!GitAvailable()) return;

        // docs/stories is outside the /src/ allowlist → git-ignored. The
        // Phase 81 preflight must refuse rather than silently no-op the commit
        // (which would open an empty PR and strand the plan gate forever).
        var cloneDir = await CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-docs", Kind = "github", CloneRoot = cloneDir };
        await git.EnsureBranchAsync(repo, Branch, "main");

        await Assert.ThrowsAsync<GitIgnoredPathException>(() =>
            git.CommitFilesAsync(repo, Branch, Slug,
                new Dictionary<string, string> { [DocUnderDocs] = "# arch\n" },
                "plan: architecture"));
    }

    [Fact]
    public async Task AllowlistRepo_PlanDocUnderSrc_CommitSucceeds()
    {
        if (!GitAvailable()) return;

        // The Phase 83 fix: PlanDocBaseDir = src/docs/stories puts the doc
        // UNDER the allowlisted /src/ tree, so it commits with no .gitignore
        // negation gymnastics. Same allowlist repo, just a /src/-rooted path.
        var cloneDir = await CreateRepoAsync(AllowlistGitignore);
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-src", Kind = "github", CloneRoot = cloneDir };
        await git.EnsureBranchAsync(repo, Branch, "main");

        var sha = await git.CommitFilesAsync(repo, Branch, Slug,
            new Dictionary<string, string> { [DocUnderSrc] = "# arch\n" },
            "plan: architecture");

        Assert.False(string.IsNullOrWhiteSpace(sha), "Commit should have produced a HEAD sha.");
        Assert.True(await git.FileExistsAsync(repo, Branch, DocUnderSrc),
            "architecture.md under src/ should exist on the branch after a successful commit.");
    }

    // ── fixture + git helpers ────────────────────────────────────────────

    /// <summary>
    /// Create a bare remote + working clone seeded with one commit on
    /// <c>main</c> carrying <paramref name="gitignore"/> (force-added, since
    /// the allowlist would otherwise drop .gitignore itself) plus a <c>src/</c>
    /// placeholder. Returns the clone path for the repo's <c>CloneRoot</c>.
    /// </summary>
    private async Task<string> CreateRepoAsync(string gitignore)
    {
        var id = Guid.NewGuid().ToString("N");
        var remoteDir = Path.Combine(Path.GetTempPath(), $"forge-gi-remote-{id}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"forge-gi-seed-{id}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"forge-gi-clone-{id}");
        this.tempDirs.Add(remoteDir);
        this.tempDirs.Add(cloneDir);

        Directory.CreateDirectory(remoteDir);
        await Git(remoteDir, "init", "--bare", "-b", "main");

        Directory.CreateDirectory(seedDir);
        await Git(seedDir, "init", "-b", "main");
        await Git(seedDir, "config", "user.email", "test@forge.local");
        await Git(seedDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(seedDir, ".gitignore"), gitignore.Replace("\r\n", "\n"));
        Directory.CreateDirectory(Path.Combine(seedDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(seedDir, "src", ".keep"), "\n");
        // Force-add: the allowlist `/*` rule would otherwise make `git add`
        // skip .gitignore itself, leaving the worktree with NO .gitignore.
        await Git(seedDir, "add", "-f", ".gitignore", "src/.keep");
        await Git(seedDir, "commit", "-m", "seed");
        await Git(seedDir, "remote", "add", "origin", remoteDir);
        await Git(seedDir, "push", "-u", "origin", "main");
        try { Directory.Delete(seedDir, recursive: true); } catch { /* best-effort */ }

        await Git(Path.GetTempPath(), "clone", remoteDir, cloneDir);
        await Git(cloneDir, "config", "user.email", "test@forge.local");
        await Git(cloneDir, "config", "user.name", "Forge Test");
        return cloneDir;
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
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in '{cwd}': {await stderrTask}");
        }
    }
}
