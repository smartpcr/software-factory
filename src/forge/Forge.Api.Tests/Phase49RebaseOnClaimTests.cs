// -----------------------------------------------------------------------
// <copyright file="Phase49RebaseOnClaimTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 49 follow-up — pin the "merge latest base into ws branch before
/// the agent runs" guarantee. Operator hit this on 2026-05-10 with
/// failover-cluster:XRAFT: stage 1.2's worktree was at the plan-PR commit
/// (3acba76), stage 1.1's PR had since merged into <c>feature/xraft</c>
/// adding the xraft-core scaffolding, and the engineer agent saw only
/// README + docs — not the sibling's commits it was supposed to extend.
/// The agent stalled at iter 1 with score 3 because there was nothing to
/// build on.
///
/// <para>The fix lives in
/// <see cref="CrewStoryArtifactStrategy.ProduceWorkstreamChangesAsync"/>:
/// right after <see cref="IStoryGitOps.EnsureWorktreeAsync"/> opens the
/// worktree it calls
/// <see cref="IStoryGitOps.MergeUpstreamIntoBranchAsync"/> to fast-forward
/// the ws branch onto the spawn's <see cref="StorySpawnRow.BaseBranch"/>.
/// These tests exercise the merge directly against a real temp git repo
/// (no LLM, no orchestrator) so the contract stays intact regardless of
/// future refactors of the call site.</para>
/// </summary>
public class Phase49RebaseOnClaimTests : IAsyncLifetime
{
    private readonly string repoRoot = Path.Combine(Path.GetTempPath(), $"forge-rebase-{Guid.NewGuid():N}");
    private RealStoryGitOps git = null!;
    private RepoRow repo = null!;

    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        // Set up a bare "origin" + a local clone so push/fetch behave like prod.
        Directory.CreateDirectory(this.repoRoot);
        this.originPath = Path.Combine(this.repoRoot, "origin.git");
        var clonePath = Path.Combine(this.repoRoot, "clone");

        await RunAsync(this.repoRoot, "git", "init", "--bare", "-b", "main", this.originPath);
        await RunAsync(this.repoRoot, "git", "clone", this.originPath, clonePath);
        await ConfigureGitIdentityAsync(clonePath);

        // Initial commit on main with a README so origin/main has content.
        await File.WriteAllTextAsync(Path.Combine(clonePath, "README.md"), "# repo\n");
        await RunAsync(clonePath, "git", "add", ".");
        await RunAsync(clonePath, "git", "commit", "-m", "Initial");
        await RunAsync(clonePath, "git", "push", "-u", "origin", "main");

        // Switch the clone OFF main onto a parking branch so subsequent
        // `git worktree add ... main` from EnsureWorktreeAsync doesn't
        // collide with the primary worktree's checkout.
        await RunAsync(clonePath, "git", "checkout", "-b", "parking");

        this.git = new RealStoryGitOps();
        this.repo = new RepoRow { Id = "repo-1", CloneRoot = clonePath };
    }

    private string originPath = string.Empty;

    public Task DisposeAsync()
    {
        try { Directory.Delete(this.repoRoot, recursive: true); }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Headline regression: ws branch was created at base@{T0}, base
    /// advanced (sibling stage merged), agent claims the ws → merge
    /// must fast-forward the ws branch onto the new base tip so the
    /// agent sees the sibling's commits before iter 1 starts.
    /// </summary>
    [Fact]
    public async Task MergeUpstreamIntoBranch_FastForwards_WsBranchWhenBaseAdvanced()
    {
        if (!GitAvailable()) return;

        // T0 — create ws branch off main; nothing on it yet (mirrors
        // the orchestrator's "new branch pushed before any iter ran").
        var wsBranch = "ws/test/stage-1-2";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "main");

        // T1 — sibling stage 1.1 lands a commit on `main`. Simulate via
        // a worktree on main + commit + push.
        await SimulateBaseAdvanceAsync("main", "stage-1.1-files", "xraft-core/src/lib.rs", "pub fn one() {}");

        // T2 — orchestrator claims ws/test/stage-1-2; opens its
        // worktree, then merges base in. After the merge the ws branch
        // should contain the sibling's file.
        var workspace = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "main", worktreeSlug: "stage-1-2-test");
        await this.git.MergeUpstreamIntoBranchAsync(this.repo, wsBranch, "main");

        Assert.True(File.Exists(Path.Combine(workspace, "xraft-core", "src", "lib.rs")),
            $"After merge, the sibling's file must exist in the ws worktree at {workspace}.");
        Assert.True(File.Exists(Path.Combine(workspace, "README.md")),
            "The original base file must still exist.");
    }

    /// <summary>
    /// Idempotent: when the ws branch is ALREADY current with base AND
    /// the caller passes <c>force: false</c> (the pre-impl merge path —
    /// no PR exists yet), the helper is a true no-op: HEAD SHA unchanged
    /// and no "nudge" empty commit pushed to origin. The post-pass merge
    /// path keeps <c>force: true</c> so an open PR's stale-mergeable
    /// cache still gets nudged.
    /// </summary>
    [Fact]
    public async Task MergeUpstreamIntoBranch_NoOp_WhenAlreadyUpToDate()
    {
        if (!GitAvailable()) return;

        var wsBranch = "ws/test/already-current";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "main");

        // Open the worktree once + merge twice with force=false; the
        // second merge should see the branch already at origin/main and
        // skip without pushing an empty commit.
        var workspace = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "main", worktreeSlug: "ws-already-current");
        await this.git.MergeUpstreamIntoBranchAsync(this.repo, wsBranch, "main", force: false);

        var (_, headBefore, _) = await CaptureAsync(workspace, "git", "rev-parse", "HEAD");

        await this.git.MergeUpstreamIntoBranchAsync(this.repo, wsBranch, "main", force: false);

        var (_, headAfter, _) = await CaptureAsync(workspace, "git", "rev-parse", "HEAD");
        Assert.Equal(headBefore.Trim(), headAfter.Trim());
    }

    /// <summary>
    /// When the spawn's base branch isn't <c>main</c> (e.g.
    /// <c>feature/xraft</c> on failover-cluster:XRAFT), the merge must
    /// target that branch — the legacy code hardcoded "main" and pulled
    /// in unrelated history. This test simulates the
    /// <c>feature/xraft</c> spawn shape: create a feature base off
    /// main, commit sibling work to feature, ws branched off feature
    /// must merge from feature (not main).
    /// </summary>
    [Fact]
    public async Task MergeUpstreamIntoBranch_HonorsCustomBaseBranch()
    {
        if (!GitAvailable()) return;

        // Create feature/xraft on origin via a sibling clone (so the
        // primary clone's parking branch doesn't conflict). Write a
        // plan.md commit so origin/feature/xraft has its own content
        // distinct from main.
        var sibling = Path.Combine(this.repoRoot, "sibling-feature-bootstrap");
        await RunAsync(this.repoRoot, "git", "clone", this.originPath, sibling);
        await ConfigureGitIdentityAsync(sibling);
        await RunAsync(sibling, "git", "checkout", "-b", "feature/xraft");
        await File.WriteAllTextAsync(Path.Combine(sibling, "plan.md"), "# plan\n");
        await RunAsync(sibling, "git", "add", ".");
        await RunAsync(sibling, "git", "commit", "-m", "Plan PR base");
        await RunAsync(sibling, "git", "push", "-u", "origin", "feature/xraft");

        // Make the primary clone aware of feature/xraft.
        await RunAsync(this.repo.CloneRoot!, "git", "fetch", "origin", "feature/xraft");

        // ws branched off feature/xraft.
        var wsBranch = "ws/test/feature-child";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "feature/xraft");

        // Sibling lands a commit on feature/xraft (not main).
        var libPath = Path.Combine(sibling, "xraft-core", "src", "lib.rs");
        Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
        await File.WriteAllTextAsync(libPath, "pub fn one() {}");
        await RunAsync(sibling, "git", "add", ".");
        await RunAsync(sibling, "git", "commit", "-m", "Sibling commit on feature/xraft");
        await RunAsync(sibling, "git", "push", "origin", "feature/xraft");
        await RunAsync(this.repo.CloneRoot!, "git", "fetch", "origin", "feature/xraft");

        // Open the ws worktree + merge from feature/xraft (NOT main).
        var workspace = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "feature/xraft", worktreeSlug: "ws-feature-child");
        await this.git.MergeUpstreamIntoBranchAsync(this.repo, wsBranch, "feature/xraft");

        Assert.True(File.Exists(Path.Combine(workspace, "xraft-core", "src", "lib.rs")),
            "After merge from feature/xraft, the sibling's file should exist.");
        Assert.True(File.Exists(Path.Combine(workspace, "plan.md")),
            "The feature/xraft base's plan.md should also exist (rules out merging from main).");
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 2026-06-03 — pin the pass-time base-refresh contract that
    /// <see cref="CrewStoryArtifactStrategy"/> now relies on (the
    /// <c>refreshBaseBeforePostPassGate</c> hook threaded into
    /// <c>RunGenEvalLoopAsync</c>'s <c>OnPassValidate</c>). Reproduces the
    /// code-intelligence:PR-INGESTER stage 5.3 stall: two sibling
    /// workstreams each declare the SAME symbol in DIFFERENT files (no
    /// textual merge conflict, but the Go package won't compile). The
    /// <c>force:false</c> merge MUST succeed cleanly and bring the sibling's
    /// file into the worktree so the post-pass build gate compiles the union
    /// and catches the duplicate-symbol break — instead of the stale-base
    /// per-iter gate greenlighting a diff that breaks the base after merge.
    /// </summary>
    [Fact]
    public async Task MergeUpstreamIntoBranch_ForceFalse_CleanMerge_BringsInSiblingFile_SoGateSeesCrossFileCollision()
    {
        if (!GitAvailable()) return;

        // T0 — ws branch off main.
        var wsBranch = "ws/test/stage-5-3";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "main");

        // T1 — the engineer's own committed+pushed work on the ws branch:
        // read_pr_records.go declares `const RoutePRRecords`. Mirrors the
        // per-iter CommitAllAndPushAsync that lands the agent's edits on
        // origin BEFORE the post-pass gate runs (so the merge's internal
        // reset --hard origin/{branch} loses nothing).
        var workspace = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "main", worktreeSlug: "stage-5-3-test");
        var readFile = Path.Combine(workspace, "mgmtapi", "read_pr_records.go");
        Directory.CreateDirectory(Path.GetDirectoryName(readFile)!);
        await File.WriteAllTextAsync(readFile,
            "package mgmtapi\n\nconst RoutePRRecords = \"/v1/pr_records\"\n");
        await RunAsync(workspace, "git", "add", ".");
        await RunAsync(workspace, "git", "commit", "-m", "ws: add read_pr_records route const");
        await RunAsync(workspace, "git", "push", "-u", "origin", wsBranch);

        // T2 — a SIBLING workstream squash-merges into main mid-drive,
        // adding feedback_pr_record.go that ALSO declares the same const.
        // Different file => NO textual merge conflict, but the package now
        // has a duplicate const => won't compile.
        await SimulateBaseAdvanceAsync("main", "sibling-feedback",
            "mgmtapi/feedback_pr_record.go",
            "package mgmtapi\n\nconst RoutePRRecords = \"/v1/pr_records\"\n");

        // T3 — pass-time base refresh (force:false).
        var merged = await this.git.MergeUpstreamIntoBranchAsync(
            this.repo, wsBranch, "main", force: false);

        Assert.True(merged,
            "force:false clean merge of a non-conflicting sibling file must succeed.");
        Assert.True(File.Exists(readFile),
            "The ws branch's own file must survive the merge.");
        Assert.True(File.Exists(Path.Combine(workspace, "mgmtapi", "feedback_pr_record.go")),
            "The sibling's file must be present after the merge so the post-pass "
            + "build gate sees BOTH declarations and rejects the pass.");
    }

    /// <summary>
    /// 2026-06-03 — the companion guard: when the base advance REALLY
    /// conflicts textually with the ws branch's edits (same file, same
    /// region), <c>force:false</c> must surface that as <c>merged=false</c>
    /// (merge aborted) WITHOUT silently auto-resolving via <c>-X ours</c> —
    /// which would discard the base's change and let a broken pass through.
    /// The ws branch's own content stays intact (no committed conflict
    /// markers, no base-side overwrite); the orchestrator's real
    /// provider-merge step handles genuine conflicts later.
    /// </summary>
    [Fact]
    public async Task MergeUpstreamIntoBranch_ForceFalse_ReturnsFalse_OnRealTextualConflict_WithoutXOurs()
    {
        if (!GitAvailable()) return;

        var wsBranch = "ws/test/conflict";
        await this.git.EnsureBranchAsync(this.repo, wsBranch, "main");

        var workspace = await this.git.EnsureWorktreeAsync(
            this.repo, wsBranch, "main", worktreeSlug: "ws-conflict");
        var shared = Path.Combine(workspace, "shared.go");
        await File.WriteAllTextAsync(shared, "package m\n\nconst V = \"ws-side\"\n");
        await RunAsync(workspace, "git", "add", ".");
        await RunAsync(workspace, "git", "commit", "-m", "ws edit shared");
        await RunAsync(workspace, "git", "push", "-u", "origin", wsBranch);

        // Base edits the SAME line differently => real textual conflict.
        await SimulateBaseAdvanceAsync("main", "base-conflict", "shared.go",
            "package m\n\nconst V = \"base-side\"\n");

        var merged = await this.git.MergeUpstreamIntoBranchAsync(
            this.repo, wsBranch, "main", force: false);

        Assert.False(merged,
            "force:false must surface a real textual conflict as merged=false (NO -X ours).");

        var content = await File.ReadAllTextAsync(shared);
        Assert.Contains("ws-side", content);
        Assert.DoesNotContain("<<<<<<<", content);
        Assert.DoesNotContain("base-side", content);
    }

    /// <summary>
    /// Advance a base branch by N commits. Uses a SEPARATE throwaway
    /// clone (sibling of the primary) so we don't fight git's
    /// "branch already checked out" guard against the main checkout
    /// or any worktree EnsureWorktreeAsync materialised.
    /// </summary>
    private async Task SimulateBaseAdvanceAsync(string baseBranch, string label, string repoRelPath, string content)
    {
        var siblingClone = Path.Combine(this.repoRoot, $"sibling-{label}");
        await RunAsync(this.repoRoot, "git", "clone", this.originPath, siblingClone);
        await ConfigureGitIdentityAsync(siblingClone);
        await RunAsync(siblingClone, "git", "checkout", baseBranch);

        var full = Path.Combine(siblingClone, repoRelPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await RunAsync(siblingClone, "git", "add", ".");
        await RunAsync(siblingClone, "git", "commit", "-m", $"Sibling: {label}");
        await RunAsync(siblingClone, "git", "push", "origin", baseBranch);

        // Make the primary clone aware so EnsureWorktreeAsync's fetch
        // for the ws branch's base picks up the new commit immediately.
        await RunAsync(this.repo.CloneRoot!, "git", "fetch", "origin", baseBranch);
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
