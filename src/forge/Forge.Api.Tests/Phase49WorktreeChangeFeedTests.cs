// -----------------------------------------------------------------------
// <copyright file="Phase49WorktreeChangeFeedTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 49 follow-up — pin the live <c>task.worktree-tick</c> feed
/// against a real temp git repo. Observed 2026-05-11 on
/// failover-cluster:XRAFT stage 2.3: the Worktree-tab snap reported
/// <c>+0 ~0 -0</c> with a branchDiff array of 47 unrelated files (all
/// of <c>feature/xraft</c>'s contents) because <c>SampleAsync</c>
/// hard-coded <c>git diff main..HEAD</c> regardless of the workstream's
/// real base branch.
///
/// <para>The fix:</para>
/// <list type="bullet">
///   <item><description>Sample takes a <c>baseBranch</c> explicit arg
///   (resolved from <c>WorkItemBaseBranchResolver</c> at the call
///   sites).</description></item>
///   <item><description>Diff anchors at the fork point
///   (<c>git merge-base origin/{baseBranch} HEAD</c>) so the
///   branchDiff scopes to the workstream's contribution.</description></item>
///   <item><description>Folds tracked-but-uncommitted + untracked
///   entries into the totals so the snap pills match
///   <see cref="WorktreeCommitsScanner.BranchTotals"/> and the PR file
///   list.</description></item>
///   <item><description>Ahead/behind via
///   <c>git rev-list --left-right --count origin/{baseBranch}...HEAD</c>
///   so the snap agrees with /commits.</description></item>
///   <item><description>Renames (<c>R100</c>) and copies
///   (<c>C100</c>) are bucketed as modifications on the new path
///   rather than dumped into a single-char status confusion.</description></item>
/// </list>
/// </summary>
public class Phase49WorktreeChangeFeedTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-feed-{Guid.NewGuid():N}");
    private string originPath = string.Empty;
    private string clonePath = string.Empty;
    private string workPath = string.Empty;

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        Directory.CreateDirectory(this.repoRoot);
        this.originPath = Path.Combine(this.repoRoot, "origin.git");
        this.clonePath = Path.Combine(this.repoRoot, "clone");
        this.workPath = this.clonePath;

        await Run(this.repoRoot, "git", "init", "--bare", "-b", "main", this.originPath);
        await Run(this.repoRoot, "git", "clone", this.originPath, this.clonePath);
        await Run(this.clonePath, "git", "config", "user.email", "test@forge.local");
        await Run(this.clonePath, "git", "config", "user.name", "Forge Test");

        // Seed origin/main + an upstream `feature/xraft` branch that the
        // workstream forks from. Mirrors the failover-cluster:XRAFT
        // setup where the spawn base is `feature/xraft`, NOT `main`.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# repo\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "Initial");
        await Run(this.clonePath, "git", "push", "-u", "origin", "main");

        await Run(this.clonePath, "git", "checkout", "-b", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "feature.txt"), "feature seed\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "feature: seed");
        await Run(this.clonePath, "git", "push", "-u", "origin", "feature/xraft");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoRoot, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Headline — when the workstream's base branch is
    /// <c>feature/xraft</c> (not <c>main</c>), the branchDiff must
    /// only reflect commits made on the ws branch, not files inherited
    /// from <c>feature/xraft</c> itself. Before the fix this returned
    /// every file on <c>feature/xraft</c> (because of the literal
    /// <c>main..HEAD</c> anchor).
    /// </summary>
    [Fact]
    public async Task SampleAsync_UsesProvidedBaseBranch_NotMain()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/snapshot-store", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "snapshot.rs"), "snapshot module\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: snapshot module");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var snap = await feed.SampleAsync(this.workPath, "feature/xraft", CancellationToken.None);

        Assert.NotNull(snap);
        // ONLY the workstream's contribution should surface in
        // branchDiff — the feature.txt seed file is part of the base,
        // not the workstream.
        Assert.Contains("snapshot.rs", snap!.BranchDiffAdded ?? Array.Empty<string>());
        Assert.DoesNotContain("feature.txt", snap.BranchDiffAdded ?? Array.Empty<string>());
        Assert.DoesNotContain("README.md", snap.BranchDiffAdded ?? Array.Empty<string>());
    }

    /// <summary>
    /// Counterfactual — using "main" as base branch would (correctly,
    /// for that anchor) include both <c>feature.txt</c> and
    /// <c>snapshot.rs</c>. Locks in that the bug WAS what we said:
    /// hard-coded "main" anchor pollutes the totals.
    /// </summary>
    [Fact]
    public async Task SampleAsync_WithMainBase_IncludesUpstreamFeatureCommits()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/snapshot-store", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "snapshot.rs"), "snapshot module\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: snapshot module");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var snap = await feed.SampleAsync(this.workPath, "main", CancellationToken.None);

        Assert.NotNull(snap);
        // Diffing against `main` includes the feature.txt seed file
        // because it landed AFTER main's tip. This is the bug the
        // production code exhibited until baseBranch threading.
        Assert.Contains("feature.txt", snap!.BranchDiffAdded ?? Array.Empty<string>());
        Assert.Contains("snapshot.rs", snap.BranchDiffAdded ?? Array.Empty<string>());
    }

    /// <summary>
    /// Ahead/behind must come from
    /// <c>rev-list --left-right --count origin/{baseBranch}...HEAD</c>
    /// (matches scanner). Before the fix the feed read these from
    /// porcelain v2 status which is keyed on the branch's CONFIGURED
    /// upstream, not the workstream's real base.
    /// </summary>
    [Fact]
    public async Task SampleAsync_AheadBehind_AgainstProvidedBase()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/ab", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "ws-1.txt"), "1\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: 1");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "ws-2.txt"), "2\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: 2");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var snap = await feed.SampleAsync(this.workPath, "feature/xraft", CancellationToken.None);

        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Ahead);
        Assert.Equal(0, snap.Behind);
    }

    /// <summary>
    /// Folding semantics — tracked-but-uncommitted edits AND untracked
    /// files both fold into branchDiff so the Worktree-tab pills (which
    /// use these arrays) match what a reviewer sees in the PR file list
    /// once the agent pushes.
    /// </summary>
    [Fact]
    public async Task SampleAsync_BranchDiff_FoldsUncommittedAndUntracked()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/fold", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "committed.txt"), "in commit\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: committed");

        // One uncommitted edit + one untracked add.
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "README.md"), "# repo (edit)\n");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "scratch.txt"), "not committed\n");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var snap = await feed.SampleAsync(this.workPath, "feature/xraft", CancellationToken.None);

        Assert.NotNull(snap);
        Assert.Contains("committed.txt", snap!.BranchDiffAdded ?? Array.Empty<string>());
        Assert.Contains("scratch.txt", snap.BranchDiffAdded ?? Array.Empty<string>());
        Assert.Contains("README.md", snap.BranchDiffModified ?? Array.Empty<string>());
    }

    /// <summary>
    /// Renames — <c>git diff --name-status</c> emits
    /// <c>R100\told\tnew</c>. The pre-fix line-0 parser dumped this
    /// into branchDiffModified with path = "100\told\tnew" which
    /// broke the UI's display + dedup. The fix parses on tab and
    /// surfaces the new path.
    /// </summary>
    [Fact]
    public async Task SampleAsync_HandlesRenameStatus()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "old-name.txt"), "rename me\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "feature: pre-rename seed");
        await Run(this.clonePath, "git", "push", "origin", "feature/xraft");

        await Run(this.clonePath, "git", "checkout", "-b", "ws/rename", "feature/xraft");
        // Rename via git mv so the diff machinery surfaces R100.
        await Run(this.clonePath, "git", "mv", "old-name.txt", "new-name.txt");
        await Run(this.clonePath, "git", "commit", "-m", "ws: rename");

        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var snap = await feed.SampleAsync(this.workPath, "feature/xraft", CancellationToken.None);

        Assert.NotNull(snap);
        // The new path should surface in Modified; the old path
        // should NOT show up in any bucket of branchDiff (the rename
        // collapses into a single move on the new path).
        Assert.Contains("new-name.txt", snap!.BranchDiffModified ?? Array.Empty<string>());
        Assert.DoesNotContain("old-name.txt", snap.BranchDiffAdded ?? Array.Empty<string>());
        Assert.DoesNotContain("old-name.txt", snap.BranchDiffDeleted ?? Array.Empty<string>());
    }

    /// <summary>
    /// Two subscribers sharing the same worktree path but with
    /// different <c>baseBranch</c> values must each see their own
    /// merge-base scoping — the snapshot cache keys by both axes.
    /// Exotic in production but cheap to defend against, and the
    /// alternative (path-only key) silently masks one of the
    /// subscriber's intended anchor.
    /// </summary>
    [Fact]
    public async Task SampleAsync_DistinctBaseBranches_ProduceDistinctSnapshots()
    {
        if (!GitAvailable()) return;

        await Run(this.clonePath, "git", "checkout", "-b", "ws/distinct", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(this.clonePath, "marker.txt"), "marker\n");
        await Run(this.clonePath, "git", "add", ".");
        await Run(this.clonePath, "git", "commit", "-m", "ws: marker");
        await Run(this.clonePath, "git", "fetch", "origin", "--quiet");

        var feed = new WorktreeChangeFeed(NewChannel(), NullLogger<WorktreeChangeFeed>.Instance);
        var againstFeature = await feed.SampleAsync(this.workPath, "feature/xraft", CancellationToken.None);
        var againstMain = await feed.SampleAsync(this.workPath, "main", CancellationToken.None);

        Assert.NotNull(againstFeature);
        Assert.NotNull(againstMain);
        // feature/xraft baseline: only `marker.txt` is the workstream's
        // contribution.
        Assert.Single(againstFeature!.BranchDiffAdded ?? Array.Empty<string>());
        // main baseline: feature.txt (the upstream seed) + marker.txt.
        Assert.True((againstMain!.BranchDiffAdded ?? Array.Empty<string>()).Count >= 2);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static TaskChannel NewChannel()
    {
        // The feed only calls EmitAsync via the channel; tests don't
        // verify emit content here (separate Phase16 tests do that), so
        // an in-memory no-op channel is sufficient. Construct via a
        // throw-away service provider so the channel's dependencies
        // (hub context, db) resolve to null-friendly stubs.
        return new TaskChannel(
            new NullHubContext(),
            new NullScopeFactory(),
            NullLogger<TaskChannel>.Instance);
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
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task Run(string cwd, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
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
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}\n{stdout}");
    }
}

/// <summary>
/// Minimal <see cref="Microsoft.AspNetCore.SignalR.IHubContext{Forge.Api.Realtime.ForgeHub}"/>
/// stub so <see cref="TaskChannel"/> can be constructed without spinning
/// up SignalR. Methods are intentionally no-ops; the feed tests don't
/// observe what's emitted.
/// </summary>
internal sealed class NullHubContext : Microsoft.AspNetCore.SignalR.IHubContext<Forge.Api.Realtime.ForgeHub>
{
    public Microsoft.AspNetCore.SignalR.IHubClients Clients { get; } = new NullClients();
    public Microsoft.AspNetCore.SignalR.IGroupManager Groups { get; } = new NullGroups();

    private sealed class NullClients : Microsoft.AspNetCore.SignalR.IHubClients
    {
        public Microsoft.AspNetCore.SignalR.IClientProxy All { get; } = new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => new NullProxy();
        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => new NullProxy();
    }

    private sealed class NullGroups : Microsoft.AspNetCore.SignalR.IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullProxy : Microsoft.AspNetCore.SignalR.IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Minimal <see cref="IServiceScopeFactory"/> for <see cref="TaskChannel"/>.
/// Returns a scope whose service provider yields null for any service it
/// doesn't recognise; the channel only resolves the DB context which the
/// feed tests don't write to, so this is enough to keep construction green.
/// </summary>
internal sealed class NullScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
{
    public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new NullScope();

    private sealed class NullScope : Microsoft.Extensions.DependencyInjection.IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new NullProvider();
        public void Dispose() { }
    }

    private sealed class NullProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
