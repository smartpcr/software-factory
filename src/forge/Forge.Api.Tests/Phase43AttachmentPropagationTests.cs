// -----------------------------------------------------------------------
// <copyright file="Phase43AttachmentPropagationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 43 follow-up — story attachments must reach every downstream producer
/// (engineer workstream, QA features, step-defs, review-comments, review-fix),
/// not just the plan-doc producers. Production observed (2026-05-12) the
/// architect agent looping on an architecture doc complaining "The acs.md
/// attachment was not available" because <c>ProduceWorkstreamChangesAsync</c>
/// and the QA/review producers never called <c>CopyAttachmentsToWorktreeAsync</c>
/// or interpolated the attachment prompt section. These tests pin the
/// helpers' correctness so the propagation can't silently regress.
/// </summary>
public class Phase43AttachmentPropagationTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-att-{Guid.NewGuid():N}");
    private readonly string dbDir;
    private readonly string attachmentDir;
    private readonly string? previousForgeDbPath;

    public Phase43AttachmentPropagationTests()
    {
        Directory.CreateDirectory(this.testRoot);
        this.dbDir = Path.Combine(this.testRoot, "forge-state");
        this.attachmentDir = Path.Combine(this.dbDir, "attachments");
        Directory.CreateDirectory(this.attachmentDir);

        // CopyAttachmentsToWorktreeAsync reads FORGE_DB_PATH to find the
        // attachment storage root. Override for the duration of the test so
        // we don't depend on the developer's E:\forge layout.
        this.previousForgeDbPath = Environment.GetEnvironmentVariable("FORGE_DB_PATH");
        Environment.SetEnvironmentVariable("FORGE_DB_PATH", Path.Combine(this.dbDir, "forge.db"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FORGE_DB_PATH", this.previousForgeDbPath);
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── BuildAttachmentPromptSection ─────────────────────────────────────────

    [Fact]
    public void BuildAttachmentPromptSection_NullOrEmpty_Returns_Empty()
    {
        Assert.Equal(string.Empty, CrewStoryArtifactStrategy.BuildAttachmentPromptSection(null));
        Assert.Equal(string.Empty, CrewStoryArtifactStrategy.BuildAttachmentPromptSection(string.Empty));
        Assert.Equal(string.Empty, CrewStoryArtifactStrategy.BuildAttachmentPromptSection("[]"));
    }

    [Fact]
    public void BuildAttachmentPromptSection_Malformed_Returns_Empty()
    {
        Assert.Equal(string.Empty, CrewStoryArtifactStrategy.BuildAttachmentPromptSection("{ not json"));
    }

    [Fact]
    public void BuildAttachmentPromptSection_RealEntries_ContainsHeaderAndPaths()
    {
        var json = BuildAttachmentsJson(("a", "acs.md", "any/path/acs.md", 1234));
        var prompt = CrewStoryArtifactStrategy.BuildAttachmentPromptSection(json);

        Assert.Contains("Story attachments -- IMPORTANT", prompt);
        Assert.Contains("`.forge-attachments/acs.md`", prompt);
        Assert.Contains("1,234 bytes", prompt);
    }

    // ── CopyAttachmentsToWorktreeAsync ───────────────────────────────────────

    [Fact]
    public async Task CopyAttachmentsToWorktreeAsync_NullOrEmptyJson_NoOp()
    {
        var dir = Path.Combine(this.testRoot, "wt-noop");
        Directory.CreateDirectory(dir);

        var resultNull = await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            null, dir, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.None);
        var resultEmpty = await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            "[]", dir, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.None);

        Assert.Empty(resultNull);
        Assert.Empty(resultEmpty);
        Assert.False(Directory.Exists(Path.Combine(dir, ".forge-attachments")),
            "Helper should not pre-create the attachments dir when there's nothing to write.");
    }

    [Fact]
    public async Task CopyAttachmentsToWorktreeAsync_RealFile_CopiedAndPathReturned()
    {
        // Seed an attachment file on disk under the forge state root.
        var relStorage = $"attachments/upload-1/{"acs.md"}";
        var absSource = Path.Combine(this.dbDir, relStorage.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absSource)!);
        await File.WriteAllTextAsync(absSource, "# ACS\nReference spec contents.\n");

        var dir = Path.Combine(this.testRoot, "wt-copy");
        Directory.CreateDirectory(dir);

        var json = BuildAttachmentsJson(("att-1", "acs.md", relStorage, 30));
        var result = await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            json, dir, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.None);

        Assert.Single(result);
        Assert.Equal(".forge-attachments/acs.md", result[0]);

        var copied = Path.Combine(dir, ".forge-attachments", "acs.md");
        Assert.True(File.Exists(copied), $"Expected attachment copied to '{copied}'.");
        Assert.Contains("Reference spec", await File.ReadAllTextAsync(copied));
    }

    [Fact]
    public async Task CopyAttachmentsToWorktreeAsync_MissingSourceFile_SkippedNotThrown()
    {
        var dir = Path.Combine(this.testRoot, "wt-missing");
        Directory.CreateDirectory(dir);

        var json = BuildAttachmentsJson(("att-x", "missing.md", "attachments/upload-x/missing.md", 0));
        var result = await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            json, dir, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CopyAttachmentsToWorktreeAsync_OverwritesStaleAttachmentsDir()
    {
        var relStorage = "attachments/upload-2/new.md";
        var absSource = Path.Combine(this.dbDir, relStorage.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absSource)!);
        await File.WriteAllTextAsync(absSource, "new contents\n");

        var dir = Path.Combine(this.testRoot, "wt-stale");
        Directory.CreateDirectory(dir);
        var attDir = Path.Combine(dir, ".forge-attachments");
        Directory.CreateDirectory(attDir);
        // Pre-seed a stale attachment from a previous tick.
        await File.WriteAllTextAsync(Path.Combine(attDir, "stale.md"), "previous upload\n");

        var json = BuildAttachmentsJson(("att-2", "new.md", relStorage, 13));
        await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            json, dir, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.None);

        Assert.False(File.Exists(Path.Combine(attDir, "stale.md")),
            "Stale attachments from a previous tick should be wiped by the next copy.");
        Assert.True(File.Exists(Path.Combine(attDir, "new.md")));
    }

    // ── TryAddGitInfoExcludeAsync ────────────────────────────────────────────

    [Fact]
    public async Task TryAddGitInfoExcludeAsync_OnRealGitRepo_AppendsPattern_Idempotent()
    {
        if (!GitAvailable()) return;

        var repo = Path.Combine(this.testRoot, "repo");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-b", "main");

        await CrewStoryArtifactStrategy.TryAddGitInfoExcludeAsync(repo, ".forge-attachments/", CancellationToken.None);
        await CrewStoryArtifactStrategy.TryAddGitInfoExcludeAsync(repo, ".forge-attachments/", CancellationToken.None);

        var excludePath = Path.Combine(repo, ".git", "info", "exclude");
        Assert.True(File.Exists(excludePath), $"Expected info/exclude at '{excludePath}'.");

        var contents = await File.ReadAllTextAsync(excludePath);
        var hits = contents.Split('\n').Count(l => l.Trim() == ".forge-attachments/");
        Assert.Equal(1, hits);

        // A bare .gitignore must NOT be created in the worktree. CommitFilesAsync
        // (which uses `git add -A`) would otherwise commit it to the PR diff.
        Assert.False(File.Exists(Path.Combine(repo, ".gitignore")),
            "Helper must use .git/info/exclude, never the tracked .gitignore.");
    }

    [Fact]
    public async Task TryAddGitInfoExcludeAsync_OnWorktreeSubCheckout_WritesIntoWorktreeGitDir()
    {
        if (!GitAvailable()) return;

        // Seed a main checkout with one commit, then `git worktree add` a sub-checkout
        // so `git rev-parse --git-dir` from the sub-checkout returns
        // {main}/.git/worktrees/{slug}/, not {sub}/.git.
        var main = Path.Combine(this.testRoot, "main");
        Directory.CreateDirectory(main);
        await Git(main, "init", "-b", "main");
        await Git(main, "config", "user.email", "test@forge.local");
        await Git(main, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(main, "README.md"), "seed\n");
        await Git(main, "add", "README.md");
        await Git(main, "commit", "-m", "seed");

        var wt = Path.Combine(this.testRoot, "wt-feature");
        await Git(main, "worktree", "add", "-b", "feature", wt);

        await CrewStoryArtifactStrategy.TryAddGitInfoExcludeAsync(wt, ".forge-attachments/", CancellationToken.None);

        // Verify the exclude is per-worktree (resolved via `git rev-parse --git-dir`
        // from the sub-checkout — points at {main}/.git/worktrees/{slug}/).
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wt-feature");
        Assert.True(Directory.Exists(worktreeGitDir),
            $"Expected git to have created the worktree git dir at '{worktreeGitDir}'.");
        var excludePath = Path.Combine(worktreeGitDir, "info", "exclude");
        Assert.True(File.Exists(excludePath), $"Expected per-worktree info/exclude at '{excludePath}'.");
        Assert.Contains(".forge-attachments/", await File.ReadAllTextAsync(excludePath));

        // And the main checkout's info/exclude was NOT polluted (the
        // per-worktree exclude is the only place we want the line).
        var mainExclude = Path.Combine(main, ".git", "info", "exclude");
        if (File.Exists(mainExclude))
        {
            Assert.DoesNotContain(".forge-attachments/", await File.ReadAllTextAsync(mainExclude));
        }
    }

    [Fact]
    public async Task CopyAttachmentsToWorktreeAsync_WithGitInfoExclude_DoesNotTouchGitignore()
    {
        if (!GitAvailable()) return;

        var repo = Path.Combine(this.testRoot, "repo-with-gitignore");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-b", "main");

        // Pre-existing tracked .gitignore — must not be mutated.
        var gitignore = Path.Combine(repo, ".gitignore");
        const string original = "bin/\nobj/\n";
        await File.WriteAllTextAsync(gitignore, original);

        var relStorage = "attachments/upload-3/spec.md";
        var absSource = Path.Combine(this.dbDir, relStorage.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absSource)!);
        await File.WriteAllTextAsync(absSource, "spec\n");

        var json = BuildAttachmentsJson(("att-3", "spec.md", relStorage, 5));
        await CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync(
            json, repo, CancellationToken.None, CrewStoryArtifactStrategy.AttachmentExcludeMode.GitInfoExclude);

        // .gitignore must be byte-identical to its pre-call contents.
        Assert.Equal(original, await File.ReadAllTextAsync(gitignore));

        // The exclude lives in info/exclude.
        var infoExclude = Path.Combine(repo, ".git", "info", "exclude");
        Assert.True(File.Exists(infoExclude));
        Assert.Contains(".forge-attachments/", await File.ReadAllTextAsync(infoExclude));
    }

    // ── ReadAllFiles filter ──────────────────────────────────────────────────

    [Fact]
    public async Task ReadAllFiles_Skips_ForgeAttachmentsDir()
    {
        // Scratch dir matching the QA / step-def / review producer layout —
        // attachments copied alongside real deliverables.
        var scratch = Path.Combine(this.testRoot, "scratch");
        Directory.CreateDirectory(scratch);

        await File.WriteAllTextAsync(Path.Combine(scratch, "feature.feature"), "Feature: thing\n");

        var attDir = Path.Combine(scratch, ".forge-attachments");
        Directory.CreateDirectory(attDir);
        await File.WriteAllTextAsync(Path.Combine(attDir, "spec.md"), "ref doc\n");
        await File.WriteAllTextAsync(Path.Combine(attDir, "diagram.md"), "ref doc 2\n");

        var dict = CrewStoryArtifactStrategy.ReadAllFiles(scratch);

        Assert.True(dict.ContainsKey("feature.feature"),
            "Real deliverable should be harvested.");
        Assert.DoesNotContain(".forge-attachments/spec.md", dict.Keys);
        Assert.DoesNotContain(".forge-attachments/diagram.md", dict.Keys);
        Assert.True(dict.Keys.All(k => !k.StartsWith(".forge-attachments/", StringComparison.Ordinal)),
            "ReadAllFiles must exclude every entry under .forge-attachments/.");
    }

    // ── LoadFreshAttachmentsJsonAsync ────────────────────────────────────────

    [Fact]
    public async Task LoadFreshAttachmentsJsonAsync_ReturnsLatestColumnValue()
    {
        // Boot the WAF for its scope-factory / EF wiring.
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        const string spawnId = "spawn-WI-43A";
        const string storyId = "WI-43A";

        // Seed spawn with no attachments yet.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                AttachmentsJson = null,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var scopeFactory = f.Services.GetRequiredService<IServiceScopeFactory>();
        // Only scopeFactory + (optional) logger are touched by the helper.
        // Other ctor params are private fields the method never reads.
        var strategy = new CrewStoryArtifactStrategy(
            scopeFactory, null!, null!, null!, null!, null!, null!, null!);

        var before = await strategy.LoadFreshAttachmentsJsonAsync(spawnId, CancellationToken.None);
        Assert.Null(before);

        // Simulate the operator uploading after the orchestrator's outer
        // tick already snapshotted the spawn.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await db.StorySpawns.FirstAsync(s => s.Id == spawnId);
            row.AttachmentsJson = BuildAttachmentsJson(("late", "late.md", "attachments/up-late/late.md", 7));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var after = await strategy.LoadFreshAttachmentsJsonAsync(spawnId, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(after));
        Assert.Contains("late.md", after);
    }

    [Fact]
    public async Task LoadFreshAttachmentsJsonAsync_MissingSpawn_ReturnsNull()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        var scopeFactory = f.Services.GetRequiredService<IServiceScopeFactory>();
        var strategy = new CrewStoryArtifactStrategy(
            scopeFactory, null!, null!, null!, null!, null!, null!, null!);

        var result = await strategy.LoadFreshAttachmentsJsonAsync("spawn-does-not-exist", CancellationToken.None);

        Assert.Null(result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildAttachmentsJson(params (string Id, string FileName, string StoragePath, long SizeBytes)[] entries)
    {
        var list = entries.Select(e => new
        {
            id = e.Id,
            fileName = e.FileName,
            storagePath = e.StoragePath,
            mimeType = "text/markdown",
            sizeBytes = e.SizeBytes,
        });
        return JsonSerializer.Serialize(list);
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
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in '{cwd}': {await stderrTask}");
        }
    }
}
