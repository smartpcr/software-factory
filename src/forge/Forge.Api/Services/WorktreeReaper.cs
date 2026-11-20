// -----------------------------------------------------------------------
// <copyright file="WorktreeReaper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Periodic reaper that removes stale per-workstream git worktrees from
/// each repo's <c>{cloneRoot}/.worktree/</c> directory. Without it, worktrees
/// accumulate indefinitely: the only event-driven cleanup
/// (<see cref="StorySpawnOrchestrator"/>'s merging/review → complete
/// transition) fires ONLY at the exact moment of that transition. Any
/// workstream that reaches <c>complete</c> by another path — operator Accept,
/// the Retry PR-merged shortcut, <see cref="PrSyncJob"/>'s external-merge
/// mapping, or simply being already-complete when the tick runs — skips that
/// cleanup, so its <c>.worktree/stage-*</c> directory survives forever.
/// (Observed 2026-05-30 on code-intelligence: ~90 leftover worktrees against
/// only 2 active spawns; 206 complete + 96 planning workstreams un-reaped.)
///
/// <para><b>Predicate (SAFE):</b> a worktree is reaped when its workstream is
/// in <c>{planning, cancelled, historic, complete}</c> — i.e., never claimed,
/// operator-killed, tombstoned, or already shipped to main. Worktrees for
/// <c>active / review / merging / blocked / stalled</c> are KEPT: they are
/// in-flight or in operator triage, and removing a <c>review</c> worktree
/// with active reviewer comments would make
/// <see cref="StorySpawnOrchestrator"/>'s
/// <c>TrySkipReviewFixOnStalePrAsync</c> auto-resolve those comments WITHOUT
/// applying the fix. The reaper also removes orphan per-workstream worktrees
/// (<c>stage-*</c> / <c>ws-*</c> leaves) whose branch matches no live
/// workstream row — these survive plan-doc reshapes that tombstone the old
/// row but leave its worktree on disk. Finally it runs an on-disk sweep for
/// <c>stage-*</c> / <c>ws-*</c> directories git no longer tracks at all
/// (unregistered, no <c>.git</c> pointer) — the residue of a
/// <see cref="StorySpawnOrchestrator"/> / reaper removal whose
/// <c>git worktree remove</c> dropped the registration but whose follow-up
/// delete failed on a locked <c>out\</c> / <c>.forge\</c> subtree; once
/// unregistered, the git-based loops and <c>git worktree prune</c> can never
/// see it again. Mirrors <c>POST /api/admin/worktrees/prune</c>.</para>
///
/// <para><b>Merged branches:</b> after the worktree pass it also closes the
/// local and origin branch of each <c>complete</c> workstream whose PR merged
/// (see <see cref="PruneMergedBranchesAsync"/>), so <c>ws/*</c> branches do not
/// outlive their merge whichever code path completed the workstream.</para>
///
/// <para><b>Grace period:</b> a worktree whose directory was modified within
/// <c>Forge:WorktreeReaper:GraceMinutes</c> is skipped, so a freshly-spawned
/// agent whose in-process liveness has not yet been reflected in its
/// workstream status is never torn down mid-flight.</para>
///
/// <para><b>Configuration</b> (defaults shown):</para>
/// <list type="bullet">
/// <item><c>Forge:WorktreeReaper:Enabled</c> — true. Set false to disable.</item>
/// <item><c>Forge:WorktreeReaper:TickInterval</c> — 00:15:00 (15 minutes).</item>
/// <item><c>Forge:WorktreeReaper:GraceMinutes</c> — 10.</item>
/// </list>
/// </summary>
public sealed class WorktreeReaper : BackgroundService
{
    private static readonly string[] PerWorkstreamLeafPrefixes = { "stage-", "ws-" };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IStoryGitOps git;
    private readonly IConfiguration config;
    private readonly ILogger<WorktreeReaper> logger;

    public WorktreeReaper(
        IServiceScopeFactory scopeFactory,
        IStoryGitOps git,
        IConfiguration config,
        ILogger<WorktreeReaper> logger)
    {
        this.scopeFactory = scopeFactory;
        this.git = git;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a beat before the first tick so app boot finishes (migrations,
        // seed, the boot-time worktree pruning in AdminEndpoints if invoked).
        var interval = this.GetTickInterval();
        try { await Task.Delay(interval, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (this.IsEnabled())
                {
                    await this.RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "WorktreeReaper tick errored — continuing");
            }

            try { await Task.Delay(this.GetTickInterval(), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick across every product. Public so a test or admin endpoint can
    /// drive it without waiting for the interval timer. Returns the number of
    /// worktrees removed.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var grace = TimeSpan.FromMinutes(this.GetGraceMinutes());

        using var scope = this.scopeFactory.CreateScope();
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var totalRemoved = 0;
        var lockObj = new object();

        await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
        {
            var removed = await this.RunForProductAsync(db, slug, grace, fct);
            if (removed > 0)
            {
                lock (lockObj) totalRemoved += removed;
            }
        }, ct);

        return totalRemoved;
    }

    /// <summary>
    /// Reap stale worktrees for a single product's DB context. Factored out so
    /// tests can drive one product directly.
    /// </summary>
    public async Task<int> RunForProductAsync(
        ProductDbContext db, string slug, TimeSpan grace, CancellationToken ct)
    {
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => !string.IsNullOrEmpty(w.RepoId))
            .Select(w => new WsRef(w.Id, w.Status, w.Branch, w.RepoId))
            .ToListAsync(ct);
        if (workstreams.Count == 0) return 0;

        var repos = await db.Repos.AsNoTracking()
            .Where(r => !string.IsNullOrEmpty(r.CloneRoot))
            .ToListAsync(ct);

        var removed = 0;
        foreach (var group in workstreams.GroupBy(w => w.RepoId!, StringComparer.Ordinal))
        {
            var repo = repos.FirstOrDefault(r => r.Id == group.Key);
            if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) continue;
            if (!Directory.Exists(repo.CloneRoot)) continue;

            // Enumerate registered worktrees once so we can (a) honour the
            // grace period by inspecting the on-disk directory mtime and
            // (b) detect orphans whose branch has no live workstream row.
            IReadOnlyList<(string Path, string Branch)> registered;
            try
            {
                registered = await EnumerateWorktreesAsync(repo.CloneRoot, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex,
                    "WorktreeReaper: failed to enumerate worktrees for repo {Repo} (product {Slug}); skipping.",
                    repo.Id, slug);
                continue;
            }

            var pathByBranch = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, branch) in registered)
            {
                if (!pathByBranch.ContainsKey(branch)) pathByBranch[branch] = path;
            }

            // (1) Status-based prune — workstreams whose worktree is stale.
            var stillCheckedOut = new HashSet<string>(pathByBranch.Keys, StringComparer.Ordinal);
            foreach (var ws in group.Where(w => ShouldPrune(w.Status ?? string.Empty)))
            {
                if (string.IsNullOrEmpty(ws.Branch)) continue;
                // Only act on a registered worktree; an unregistered branch has
                // nothing on disk for us to remove.
                if (!pathByBranch.TryGetValue(ws.Branch, out var wtPath)) continue;
                if (IsWithinGrace(wtPath, grace)) continue;

                if (await this.TryRemoveAsync(repo, ws.Branch, ws.Status, slug, ct))
                {
                    removed++;
                    stillCheckedOut.Remove(ws.Branch);
                }
            }

            // (1b) Merged-branch prune — close the branch (local + origin) of
            // every complete workstream whose PR merged. Several paths mark a
            // workstream complete (merge heal, PrSyncJob / AutoMergeWatcher via
            // WorkstreamMergeAdvancer, stalled-merged recovery, operator
            // Retry) and only some close the branch, so ws/* branches piled
            // up on origin after their PRs merged. This pass makes the
            // finished-workstream contract hold whichever path completed it.
            await this.PruneMergedBranchesAsync(db, repo, group.ToList(), stillCheckedOut, slug, ct);

            // (2) Orphan prune — registered per-workstream worktrees whose
            // branch matches no live workstream row at all.
            var liveBranches = new HashSet<string>(
                group.Select(w => w.Branch).Where(b => !string.IsNullOrEmpty(b))!,
                StringComparer.Ordinal);

            foreach (var (path, branch) in registered)
            {
                if (string.IsNullOrEmpty(branch)) continue;
                if (liveBranches.Contains(branch)) continue; // handled by loop (1)

                var leaf = Path.GetFileName(path) ?? string.Empty;
                var isPerWs = PerWorkstreamLeafPrefixes.Any(
                    p => leaf.StartsWith(p, StringComparison.Ordinal));
                if (!isPerWs) continue; // plan/qa/story/base worktrees owned by other hooks

                if (IsWithinGrace(path, grace)) continue;

                if (await this.TryRemoveAsync(repo, branch, "orphan", slug, ct))
                {
                    removed++;
                }
            }

            // (3) On-disk orphan sweep — directories under {cloneRoot}\.worktree\
            // that git no longer tracks (absent from `git worktree list`, no
            // `.git` pointer). These are the residue of a RemoveWorktreeAsync
            // whose `git worktree remove` dropped the registration but whose
            // follow-up delete failed on a locked out\ / .forge\ subtree: once
            // unregistered, loops (1)/(2) above never see them (both iterate
            // git-registered worktrees) and `git worktree prune` can't help.
            // Only stage-*/ws-* leaves are swept (plan-*/qa-*/merge-* belong to
            // other hooks); anything still carrying a `.git` entry is left to the
            // branch-keyed path (it may be an active or mid-rebase worktree).
            // Honors the same grace window.
            var wtRoot = Path.Combine(repo.CloneRoot, ".worktree");
            if (Directory.Exists(wtRoot))
            {
                var registeredPaths = new HashSet<string>(
                    registered.Select(r => NormalizeForCompare(r.Path)), StringComparer.Ordinal);

                IReadOnlyList<string> leaves;
                try
                {
                    leaves = Directory.EnumerateDirectories(wtRoot).ToList();
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex,
                        "WorktreeReaper: failed to enumerate {Root} for orphan sweep (product {Slug}); skipping.",
                        wtRoot, slug);
                    leaves = Array.Empty<string>();
                }

                foreach (var dir in leaves)
                {
                    var leaf = Path.GetFileName(dir) ?? string.Empty;
                    if (!PerWorkstreamLeafPrefixes.Any(p => leaf.StartsWith(p, StringComparison.Ordinal))) continue;
                    if (registeredPaths.Contains(NormalizeForCompare(dir))) continue; // handled by (1)/(2)
                    if (File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"))) continue;
                    if (IsWithinGrace(dir, grace)) continue;

                    if (await this.git.TryRemoveOrphanWorktreeDirAsync(repo, dir, ct))
                    {
                        removed++;
                        this.logger.LogInformation(
                            "WorktreeReaper: removed ORPHAN worktree dir {Path} (unregistered, no .git; product={Slug}).",
                            dir, slug);
                    }
                }
            }
        }

        if (removed > 0)
        {
            this.logger.LogInformation(
                "WorktreeReaper: removed {Count} stale worktree(s) in product={Slug}.",
                removed, slug);
        }

        return removed;
    }

    private async Task<bool> TryRemoveAsync(
        RepoRow repo, string branch, string? status, string slug, CancellationToken ct)
    {
        try
        {
            await this.git.RemoveWorktreeAsync(repo, branch, ct);
            this.logger.LogInformation(
                "WorktreeReaper: removed worktree for branch {Branch} (status={Status}, product={Slug}).",
                branch, status, slug);
            return true;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex,
                "WorktreeReaper: failed to remove worktree for branch {Branch} (product {Slug}); continuing.",
                branch, slug);
            return false;
        }
    }

    /// <summary>
    /// Pass (1b): delete the local and origin branch of each <c>complete</c>
    /// workstream whose work is merged. A branch is closed only when ALL hold:
    /// <list type="bullet">
    /// <item>at least one PR from that branch is merged and none is still open
    /// (the work is on the target branch, nothing is pending review);</item>
    /// <item>no other workstream row on the same branch is still live
    /// (anything but complete / cancelled / historic);</item>
    /// <item>it is not the repo default, <c>main</c>/<c>master</c>, or any
    /// spawn's base / plan / QA branch;</item>
    /// <item>it is not still checked out in a worktree (one inside the grace
    /// window survives pass (1); the next tick closes it);</item>
    /// <item>a local or origin-tracking ref still exists, so already-clean rows
    /// cost no git call (one <c>for-each-ref</c> per repo per tick).</item>
    /// </list>
    /// The delete itself is <see cref="IStoryGitOps.DeleteBranchAsync"/>, which
    /// is best-effort (logs and continues) and refuses base branches again.
    /// Returns the number of branches it asked to close.
    /// </summary>
    internal async Task<int> PruneMergedBranchesAsync(
        ProductDbContext db,
        RepoRow repo,
        IReadOnlyList<WsRef> repoWorkstreams,
        IReadOnlySet<string> stillCheckedOut,
        string slug,
        CancellationToken ct)
    {
        var completeBranches = repoWorkstreams
            .Where(w => w.Status == "complete" && !string.IsNullOrEmpty(w.Branch))
            .Select(w => w.Branch!)
            .ToHashSet(StringComparer.Ordinal);
        if (completeBranches.Count == 0) return 0;

        // A branch shared with a live row (re-planned workstream reusing the
        // name, retry in flight) must survive.
        foreach (var w in repoWorkstreams)
        {
            if (string.IsNullOrEmpty(w.Branch)) continue;
            if (w.Status is "complete" or "cancelled" or WorkstreamStatuses.Historic) continue;
            completeBranches.Remove(w.Branch);
        }

        var protectedBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "master" };
        if (!string.IsNullOrEmpty(repo.DefaultBranch)) protectedBranches.Add(repo.DefaultBranch);
        var spawnBranches = await db.StorySpawns.AsNoTracking()
            .Where(s => s.RepoId == repo.Id || s.RepoId == null)
            .Select(s => new { s.BaseBranch, s.PlanBranch, s.QaBranch })
            .ToListAsync(ct);
        foreach (var s in spawnBranches)
        {
            if (!string.IsNullOrEmpty(s.BaseBranch)) protectedBranches.Add(s.BaseBranch);
            if (!string.IsNullOrEmpty(s.PlanBranch)) protectedBranches.Add(s.PlanBranch);
            if (!string.IsNullOrEmpty(s.QaBranch)) protectedBranches.Add(s.QaBranch);
        }

        completeBranches.RemoveWhere(b => protectedBranches.Contains(b) || stillCheckedOut.Contains(b));
        if (completeBranches.Count == 0) return 0;

        var candidates = completeBranches.ToList();
        var prs = await db.Prs.AsNoTracking()
            .Where(p => p.Branch != null && candidates.Contains(p.Branch)
                && (p.RepoId == repo.Id || p.RepoId == null))
            .Select(p => new { p.Branch, p.Col, p.IsActive })
            .ToListAsync(ct);
        var merged = prs
            .GroupBy(p => p.Branch!, StringComparer.Ordinal)
            .Where(g => g.Any(p => string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase))
                && !g.Any(p => p.IsActive))
            .Select(g => g.Key)
            .ToList();
        if (merged.Count == 0) return 0;

        IReadOnlySet<string> refs;
        try
        {
            refs = await EnumerateBranchRefsAsync(repo.CloneRoot!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex,
                "WorktreeReaper: failed to list branch refs for repo {Repo} (product {Slug}); skipping merged-branch prune.",
                repo.Id, slug);
            return 0;
        }

        var closed = 0;
        foreach (var branch in merged)
        {
            if (!refs.Contains($"refs/heads/{branch}") && !refs.Contains($"refs/remotes/origin/{branch}")) continue;

            await this.git.DeleteBranchAsync(repo, branch, deleteRemote: true, ct);
            closed++;
            this.logger.LogInformation(
                "WorktreeReaper: closed merged workstream branch {Branch} (local + origin, product={Slug}).",
                branch, slug);
        }

        return closed;
    }

    /// <summary>
    /// Worktrees safe to remove. KEEPS active / review / merging / blocked /
    /// stalled — see class remarks for the review-fix correctness hazard.
    /// </summary>
    private static bool ShouldPrune(string status) => status switch
    {
        "planning" => true,
        "cancelled" => true,
        WorkstreamStatuses.Historic => true,
        "complete" => true,
        _ => false,
    };

    private static string NormalizeForCompare(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
    }

    private static bool IsWithinGrace(string worktreePath, TimeSpan grace)
    {
        if (grace <= TimeSpan.Zero) return false;
        try
        {
            if (!Directory.Exists(worktreePath)) return false;
            var lastWrite = Directory.GetLastWriteTimeUtc(worktreePath);
            return DateTime.UtcNow - lastWrite < grace;
        }
        catch
        {
            // If we can't stat the directory, err on the side of NOT reaping.
            return true;
        }
    }

    private static async Task<IReadOnlyList<(string Path, string Branch)>> EnumerateWorktreesAsync(
        string cloneRoot, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cloneRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--porcelain");
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        if (proc.ExitCode != 0) return Array.Empty<(string, string)>();

        var results = new List<(string Path, string Branch)>();
        string? currentPath = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal) && currentPath is not null)
            {
                var branch = line["branch refs/heads/".Length..].Trim();
                results.Add((currentPath, branch));
                currentPath = null;
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                currentPath = null;
            }
        }
        return results;
    }

    /// <summary>
    /// Full ref names under <c>refs/heads</c> and <c>refs/remotes/origin</c>,
    /// from one local <c>git for-each-ref</c> (no network).
    /// </summary>
    private static async Task<IReadOnlySet<string>> EnumerateBranchRefsAsync(string cloneRoot, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cloneRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        psi.ArgumentList.Add("for-each-ref");
        psi.ArgumentList.Add("--format=%(refname)");
        psi.ArgumentList.Add("refs/heads");
        psi.ArgumentList.Add("refs/remotes/origin");
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git for-each-ref exited {proc.ExitCode}");
        }

        return stdout.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private TimeSpan GetTickInterval()
    {
        var raw = this.config["Forge:WorktreeReaper:TickInterval"];
        if (TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero) return parsed;
        return TimeSpan.FromMinutes(15);
    }

    private int GetGraceMinutes()
    {
        var raw = this.config["Forge:WorktreeReaper:GraceMinutes"];
        if (int.TryParse(raw, out var parsed) && parsed >= 0) return parsed;
        return 10;
    }

    private bool IsEnabled()
    {
        var raw = this.config["Forge:WorktreeReaper:Enabled"];
        return string.IsNullOrEmpty(raw) || !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The workstream fields the reaper reads.</summary>
    internal sealed record WsRef(string Id, string? Status, string? Branch, string? RepoId);
}
