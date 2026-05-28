// -----------------------------------------------------------------------
// <copyright file="Phase87CheckIgnoreBatchingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 87 regression — when the qa-e2e producer's scratch workspace
/// gets polluted with the full repo (e.g. agent shells out to
/// <c>git clone</c> into the workspace dir), the orchestrator's
/// downstream <c>git check-ignore --</c> preflight previously bundled
/// every path into a single command line and blew the Windows
/// <c>CreateProcess</c> ~32 KB argv limit (
/// <see cref="System.ComponentModel.Win32Exception"/>
/// <c>"The filename or extension is too long."</c>). The exception
/// type didn't match the orchestrator's
/// <see cref="GitIgnoredPathException"/>-only catch, so it bubbled
/// to the outer sweep that re-drove the workstream every 30 minutes
/// without opening a PR — observed for
/// <c>code-intelligence:AST-PARSER 1.5-e2e</c> on 2026-05-28 (825
/// files, ~152 KB argv, 10+ hours stuck).
///
/// <para>The fix has two parts:</para>
/// <list type="number">
/// <item>
/// <see cref="RealStoryGitOps"/> chunks the path list into
/// <see cref="RealStoryGitOps.CheckIgnoreArgvByteBudget"/>-bounded
/// batches before invoking <c>git check-ignore</c> (and the verbose
/// re-run), and aggregates the ignored-path list across batches.
/// </item>
/// <item>
/// <see cref="CrewStoryArtifactStrategy.ProduceStageE2eAsync"/> caps
/// the producer's emitted-file count at
/// <see cref="CrewStoryArtifactStrategy.QaE2eProducerFileCountCap"/>
/// and throws an actionable
/// <see cref="System.InvalidOperationException"/> when exceeded — the
/// orchestrator's generic <c>catch (Exception)</c> at
/// <c>DriveOneQaE2eWorkstreamAsync</c> ~line 3575 then marks the
/// workstream <c>stalled</c> with the cap-violation message instead
/// of looping silently.
/// </item>
/// </list>
/// </summary>
public class Phase87CheckIgnoreBatchingTests : IAsyncLifetime
{
    private readonly List<string> tempDirs = new();

    private const string Branch = "story/local-PHASE87-STORY/check-ignore-batching";
    private const string Slug = "qa-e2e-local-PHASE87-STORY";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in this.tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    // ── 1. ChunkPathsByArgvBudget — pure unit tests ────────────────────────

    [Fact]
    public void ChunkPathsByArgvBudget_EmptyInput_ProducesNoChunks()
    {
        var chunks = RealStoryGitOps.ChunkPathsByArgvBudget(Array.Empty<string>(), 1024).ToList();
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkPathsByArgvBudget_SmallList_ProducesSingleChunk()
    {
        var paths = new[] { "a.cs", "b.cs", "c.cs" };
        var chunks = RealStoryGitOps.ChunkPathsByArgvBudget(paths, 1024).ToList();
        Assert.Single(chunks);
        Assert.Equal(paths, chunks[0]);
    }

    [Fact]
    public void ChunkPathsByArgvBudget_ExceedsBudget_SplitsIntoMultipleChunks()
    {
        // 100 paths × ~50 bytes each = ~5 KB total; with a 1 KB budget
        // we expect roughly 5 chunks.
        var paths = Enumerable.Range(0, 100)
            .Select(i => $"src/Modules/Module{i:D4}/SomeFile{i:D4}.cs")
            .ToArray();

        var chunks = RealStoryGitOps.ChunkPathsByArgvBudget(paths, 1024).ToList();

        Assert.True(chunks.Count >= 4, $"expected at least 4 chunks, got {chunks.Count}");

        // Each chunk's combined byte count (incl. inter-arg space) must
        // stay under budget unless the chunk is a singleton (oversized arg
        // exception).
        foreach (var chunk in chunks.Where(c => c.Count > 1))
        {
            var bytes = chunk.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p) + 1);
            Assert.True(bytes <= 1024, $"chunk over budget: {bytes} bytes for {chunk.Count} paths");
        }

        // Aggregate must equal input.
        var roundTrip = chunks.SelectMany(c => c).ToArray();
        Assert.Equal(paths, roundTrip);
    }

    [Fact]
    public void ChunkPathsByArgvBudget_SingleOversizedPath_LandsInOwnChunk()
    {
        var giant = new string('x', 4096);
        var paths = new[] { "small.cs", giant, "another.cs" };

        var chunks = RealStoryGitOps.ChunkPathsByArgvBudget(paths, 1024).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { "small.cs" }, chunks[0]);
        Assert.Equal(new[] { giant }, chunks[1]);
        Assert.Equal(new[] { "another.cs" }, chunks[2]);
    }

    [Fact]
    public void ChunkPathsByArgvBudget_PreservesInputOrder()
    {
        var paths = Enumerable.Range(0, 500).Select(i => $"path-{i}.txt").ToArray();
        var chunks = RealStoryGitOps.ChunkPathsByArgvBudget(paths, 512).ToList();

        var flattened = chunks.SelectMany(c => c).ToArray();
        Assert.Equal(paths, flattened);
    }

    // ── 2. AssertPlanDocPathsNotIgnoredAsync — many paths, no Win32 ───────

    [Fact]
    public async Task AssertPlanDocPathsNotIgnoredAsync_HundredsOfPaths_DoesNotThrowWin32()
    {
        if (!GitAvailable()) return;

        // Reproduces the production failure shape: hundreds of paths in
        // ONE call. Without chunking this builds a ~50 KB command line on
        // Windows and surfaces a System.ComponentModel.Win32Exception.
        // After the fix the call must complete with no exception (none
        // of the paths are gitignored on a clean repo).
        var cloneDir = await this.CreateMinimalRepoAsync();
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-phase87-many", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        // 600 paths × avg ~80 bytes = ~48 KB combined — well over the 32 KB
        // Windows CreateProcess limit if shipped as a single argv.
        var paths = Enumerable.Range(0, 600)
            .Select(i => $"src/Generated/Module{i:D4}/SubArea{i:D4}/Class{i:D4}.cs")
            .ToArray();

        // Should NOT throw Win32Exception, should NOT throw
        // GitIgnoredPathException (nothing is ignored on this clean repo).
        await git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, paths);
    }

    [Fact]
    public async Task AssertPlanDocPathsNotIgnoredAsync_HundredsOfMixedPaths_AggregatesIgnoredAcrossChunks()
    {
        if (!GitAvailable()) return;

        // Seed a .gitignore that ignores a small set of paths scattered
        // throughout the input list. After chunked check-ignore the
        // exception's path list must include ALL of them (proving
        // aggregation works across chunk boundaries).
        var cloneDir = await this.CreateRepoWithGitignoreAsync(
            ".gitignore-ignored/**\n");
        var git = new RealStoryGitOps();
        var repo = new RepoRow { Id = "repo-phase87-mixed", Kind = "github", CloneRoot = cloneDir };

        await git.EnsureBranchAsync(repo, Branch, "main");
        await git.EnsureWorktreeAsync(repo, Branch, "main", default, worktreeSlug: Slug);

        // Mix 400 OK paths with 12 ignored paths, evenly distributed so
        // the ignored entries fall into different chunks under the 8 KB
        // budget (~50 paths/chunk).
        var paths = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            paths.Add($"src/Generated/Module{i:D4}/SubArea{i:D4}/Class{i:D4}.cs");
            if (i % 33 == 0)
            {
                paths.Add($".gitignore-ignored/banned-{i:D4}.cs");
            }
        }

        var ex = await Assert.ThrowsAsync<GitIgnoredPathException>(
            () => git.AssertPlanDocPathsNotIgnoredAsync(repo, Branch, paths));

        // All ignored paths must be present in the exception (aggregation
        // across chunks).
        var expectedIgnored = paths.Where(p => p.StartsWith(".gitignore-ignored/")).ToArray();
        foreach (var ig in expectedIgnored)
        {
            Assert.Contains(ig, ex.IgnoredPaths);
        }
    }

    // ── 3. QaE2eProducerFileCountCap constant ─────────────────────────────

    [Fact]
    public void QaE2eProducerFileCountCap_IsTunedForLegitimateUseCase()
    {
        // Legitimate qa-e2e outputs are typically 2-10 files (one .feature
        // + step defs + base class + appsettings). 50 leaves 5× headroom
        // for unusual emissions while still flagging the 825-file pollution
        // case (an order of magnitude over the cap).
        Assert.True(CrewStoryArtifactStrategy.QaE2eProducerFileCountCap >= 20,
            "cap must not be tighter than legitimate emissions");
        Assert.True(CrewStoryArtifactStrategy.QaE2eProducerFileCountCap <= 200,
            "cap must catch the 825-file pollution incident");
    }

    // ── 4. CheckIgnoreArgvByteBudget constant ─────────────────────────────

    [Fact]
    public void CheckIgnoreArgvByteBudget_LeavesHeadroomUnderCreateProcessLimit()
    {
        // The Windows CreateProcess argv limit is 32767 chars. The budget
        // must leave room for the resolved `git.exe` path, the cwd, and a
        // potential `cmd /c` shim — keep the budget at most half the
        // kernel limit so we never trip it even after prefix overhead.
        Assert.True(RealStoryGitOps.CheckIgnoreArgvByteBudget >= 1024,
            "budget too tight — defeats the purpose of chunking");
        Assert.True(RealStoryGitOps.CheckIgnoreArgvByteBudget <= 16 * 1024,
            "budget too loose — leaves no headroom under CreateProcess limit");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<string> CreateMinimalRepoAsync()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        var seedDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        var cloneDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        this.tempDirs.Add(remoteDir);
        this.tempDirs.Add(seedDir);
        this.tempDirs.Add(cloneDir);

        Directory.CreateDirectory(remoteDir);
        await Git(remoteDir, "init", "--bare");

        Directory.CreateDirectory(seedDir);
        await Git(seedDir, "init", "-b", "main");
        await Git(seedDir, "config", "user.email", "test@forge.local");
        await Git(seedDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "README.md"), "# test\n");
        await Git(seedDir, "add", "README.md");
        await Git(seedDir, "commit", "-m", "seed");
        await Git(seedDir, "remote", "add", "origin", remoteDir);
        await Git(seedDir, "push", "-u", "origin", "main");

        Directory.CreateDirectory(cloneDir);
        await Git(Path.GetTempPath(), "clone", remoteDir, cloneDir);
        await Git(cloneDir, "config", "user.email", "test@forge.local");
        await Git(cloneDir, "config", "user.name", "Forge Test");
        return cloneDir;
    }

    private async Task<string> CreateRepoWithGitignoreAsync(string gitignoreBody)
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        var seedDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        var cloneDir = Path.Combine(Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
        this.tempDirs.Add(remoteDir);
        this.tempDirs.Add(seedDir);
        this.tempDirs.Add(cloneDir);

        Directory.CreateDirectory(remoteDir);
        await Git(remoteDir, "init", "--bare");

        Directory.CreateDirectory(seedDir);
        await Git(seedDir, "init", "-b", "main");
        await Git(seedDir, "config", "user.email", "test@forge.local");
        await Git(seedDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(seedDir, ".gitignore"), gitignoreBody);
        await Git(seedDir, "add", "-f", ".gitignore");
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
}
