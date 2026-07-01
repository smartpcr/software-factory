// -----------------------------------------------------------------------
// <copyright file="WorktreeReaperTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the <see cref="WorktreeReaper"/> SAFE predicate + orphan + grace
/// behaviour against a real temp git repo and a real sqlite
/// <see cref="ProductDbContext"/>. Background: 2026-05-30 code-intelligence
/// accumulated ~90 leftover <c>.worktree/stage-*</c> dirs against 2 active
/// spawns because the only event-driven cleanup
/// (<c>StorySpawnOrchestrator</c> merge transition) misses workstreams that
/// reach <c>complete</c> by any other path. The reaper is the periodic GC
/// that prevents reaccumulation.
/// </summary>
public class WorktreeReaperTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-wtreaper-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task RunForProduct_ReapsCompleteAndOrphan_KeepsActive()
    {
        if (!GitAvailable()) return;

        // Three per-WS worktrees under .worktree/:
        //   stage-complete → workstream Status=complete  → REAP
        //   stage-active   → workstream Status=active     → KEEP
        //   stage-orphan   → no workstream row at all      → REAP (orphan)
        var brComplete = "ws/test/stage-complete";
        var brActive = "ws/test/stage-active";
        var brOrphan = "ws/test/stage-orphan";
        var pathComplete = await this.AddWorktreeAsync("stage-complete", brComplete);
        var pathActive = await this.AddWorktreeAsync("stage-active", brActive);
        var pathOrphan = await this.AddWorktreeAsync("stage-orphan", brOrphan);

        var (conn, _, db) = await NewProductDbAsync();
        await using (conn)
        await using (db)
        {
            db.Repos.Add(this.repo);
            db.Workstreams.Add(NewWs("ws-complete", brComplete, "complete"));
            db.Workstreams.Add(NewWs("ws-active", brActive, "active"));
            await db.SaveChangesAsync();

            var reaper = new WorktreeReaper(
                scopeFactory: null!,
                git: this.git,
                config: new ConfigurationBuilder().Build(),
                logger: NullLogger<WorktreeReaper>.Instance);

            // grace = 0 so freshly-created worktrees aren't grace-skipped.
            var removed = await reaper.RunForProductAsync(db, "test", TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(2, removed);
        }

        Assert.False(Directory.Exists(pathComplete), "complete worktree should be reaped.");
        Assert.False(Directory.Exists(pathOrphan), "orphan worktree should be reaped.");
        Assert.True(Directory.Exists(pathActive), "active worktree must be KEPT.");
    }

    [Fact]
    public async Task RunForProduct_GraceProtectsFreshlyModifiedWorktree()
    {
        if (!GitAvailable()) return;

        var brComplete = "ws/test/stage-fresh";
        var pathComplete = await this.AddWorktreeAsync("stage-fresh", brComplete);

        var (conn, _, db) = await NewProductDbAsync();
        await using (conn)
        await using (db)
        {
            db.Repos.Add(this.repo);
            db.Workstreams.Add(NewWs("ws-fresh", brComplete, "complete"));
            await db.SaveChangesAsync();

            var reaper = new WorktreeReaper(
                scopeFactory: null!,
                git: this.git,
                config: new ConfigurationBuilder().Build(),
                logger: NullLogger<WorktreeReaper>.Instance);

            // A 1-hour grace window protects the just-created worktree.
            var removed = await reaper.RunForProductAsync(
                db, "test", TimeSpan.FromHours(1), CancellationToken.None);

            Assert.Equal(0, removed);
        }

        Assert.True(Directory.Exists(pathComplete),
            "grace period must protect a worktree modified within the window.");
    }

    [Fact]
    public async Task RunForProduct_SweepsUnregisteredOrphanDir_OnDisk()
    {
        if (!GitAvailable()) return;

        // Reproduces the 2026-06-30 stage-1.2 leak: `git worktree remove` dropped
        // the registration but the follow-up delete failed on a locked/hardened
        // out\ / .forge\ subtree, leaving an UNREGISTERED `.worktree/stage-*` dir
        // (no `.git` pointer, absent from `git worktree list`). Loops (1)/(2)
        // can't see it (both iterate git-registered worktrees) and
        // `git worktree prune` can't either. Pass (3) must collect it.
        var brActive = "ws/test/stage-active";
        var pathActive = await this.AddWorktreeAsync("stage-active", brActive);

        // Unregistered orphan leaf with the classic leftover content, NO `.git`.
        var wtDir = Path.Combine(this.clonePath, ".worktree");
        var orphan = Path.Combine(wtDir, "stage-detached");
        Directory.CreateDirectory(Path.Combine(orphan, ".forge", "memory"));
        Directory.CreateDirectory(Path.Combine(orphan, "out"));
        await File.WriteAllTextAsync(Path.Combine(orphan, "out", "marker.txt"), "build output\n");

        // A non-per-workstream unregistered leaf (plan-*) must be LEFT ALONE —
        // plan/qa/merge worktrees are owned by other lifecycle hooks.
        var planLeaf = Path.Combine(wtDir, "plan-architecture");
        Directory.CreateDirectory(planLeaf);

        var (conn, _, db) = await NewProductDbAsync();
        await using (conn)
        await using (db)
        {
            db.Repos.Add(this.repo);
            db.Workstreams.Add(NewWs("ws-active", brActive, "active"));
            await db.SaveChangesAsync();

            var reaper = new WorktreeReaper(
                scopeFactory: null!,
                git: this.git,
                config: new ConfigurationBuilder().Build(),
                logger: NullLogger<WorktreeReaper>.Instance);

            // grace = 0 so the just-created orphan isn't grace-skipped.
            var removed = await reaper.RunForProductAsync(db, "test", TimeSpan.Zero, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        Assert.False(Directory.Exists(orphan), "unregistered stage-* orphan dir should be swept.");
        Assert.True(Directory.Exists(planLeaf), "unregistered plan-* dir must be LEFT for other hooks.");
        Assert.True(Directory.Exists(pathActive), "registered active worktree must be KEPT.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<string> AddWorktreeAsync(string leaf, string branch)
    {
        var wtPath = Path.Combine(this.clonePath, ".worktree", leaf);
        await RunAsync(this.clonePath, "git", "worktree", "add", "-b", branch, wtPath, "HEAD");
        return wtPath;
    }

    private static WorkstreamRow NewWs(string id, string branch, string status) => new()
    {
        Id = id,
        RepoId = "repo-1",
        Name = id,
        Branch = branch,
        Base = "main",
        Status = status,
    };

    private static async Task<(SqliteConnection Conn, ControlDbContext Control, ProductDbContext Db)> NewProductDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options;
        var db = new ProductDbContext(opts);
        var schema = db.Database.GenerateCreateScript();
        foreach (var stmt in schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(trimmed);
        }
        return (conn, control, db);
    }

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
