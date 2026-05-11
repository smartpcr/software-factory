// -----------------------------------------------------------------------
// <copyright file="WorktreeChangeFeed.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Api.Realtime;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Phase 16.6 / 16.8 — per-workstream live feed. Polls the workstream's
/// worktree path at 1 Hz and emits a <c>task.worktree-tick</c> event on each
/// active <c>task.{workItemId}</c> SignalR group whenever the snapshot
/// (file mtimes, git status, HEAD) changes. Multiple work items pointing at
/// the same worktree share a single watcher; the worktree is unloaded when
/// the last subscriber leaves.
/// <para>
/// The implementation uses a poll loop rather than <c>FileSystemWatcher</c>
/// for portability — file watching on network-mapped paths and inside
/// containers is unreliable; a 1 Hz poll covers the operator's perception
/// of "live" without any of the platform pitfalls. Burst edits coalesce
/// naturally (one tick per second regardless of how many writes happened).
/// </para>
/// </summary>
public sealed class WorktreeChangeFeed : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const int MaxPathsPerCategory = 200;

    private readonly TaskChannel channel;
    private readonly ILogger<WorktreeChangeFeed> logger;

    /// <summary>
    /// workItemId → subscription. Many work items can map to the same
    /// worktree path; we still emit one tick per work item so the SignalR
    /// fan-out is correctly scoped.
    /// </summary>
    private readonly ConcurrentDictionary<string, Subscription> subs = new();

    /// <summary>worktreePath → last-known snapshot. Reused across ticks for diff detection.</summary>
    private readonly ConcurrentDictionary<string, Snapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? cts;
    private Task? loop;

    public WorktreeChangeFeed(TaskChannel channel, ILogger<WorktreeChangeFeed> logger)
    {
        this.channel = channel;
        this.logger = logger;
    }

    /// <summary>
    /// Register a work item to receive <c>task.worktree-tick</c> events.
    /// <paramref name="baseBranch"/> determines which ref the branchDiff
    /// + ahead/behind counters anchor against — must be the workstream's
    /// real fork point (typically <c>spawn.BaseBranch</c>) or the
    /// counters will be wrong. Use
    /// <see cref="Forge.Api.Services.Stories.WorkItemBaseBranchResolver"/>
    /// to resolve.
    /// </summary>
    public void Subscribe(string workItemId, string workstreamId, string worktreePath, string baseBranch)
    {
        if (string.IsNullOrEmpty(workItemId) || string.IsNullOrEmpty(worktreePath)) return;
        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch;
        this.subs[workItemId] = new Subscription(workstreamId, worktreePath, resolvedBase);
    }

    /// <summary>Stop receiving ticks for the work item. Idempotent.</summary>
    public void Unsubscribe(string workItemId) => this.subs.TryRemove(workItemId, out _);

    /// <summary>Active subscriber count per worktree path. Used by the meter and tests.</summary>
    public int CountSubscribers(string worktreePath) =>
        this.subs.Values.Count(s => string.Equals(s.WorktreePath, worktreePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most recent snapshot for a path (any base branch), or null if never sampled.</summary>
    public Snapshot? GetLatest(string worktreePath)
    {
        foreach (var kvp in this.snapshots)
        {
            if (kvp.Key.StartsWith(worktreePath + "\0", StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Run one tick across all active <c>(path, baseBranch)</c> groups.
    /// Public so tests can drive the loop deterministically without
    /// spawning the hosted timer.
    /// <para>Phase 49 follow-up — was previously keyed by path only,
    /// which forced every subscriber sharing a path to share an
    /// anchor. The current implementation groups by both axes so
    /// branchDiff/ahead/behind always agree with each subscriber's
    /// own spawn base branch.</para>
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var groups = this.subs.Values
            .GroupBy(s => (s.WorktreePath, s.BaseBranch),
                (k, _) => k)
            .ToList();
        foreach (var (path, baseBranch) in groups)
        {
            try
            {
                var snap = await SampleAsync(path, baseBranch, ct);
                if (snap is null) continue;
                var cacheKey = CacheKey(path, baseBranch);
                if (this.snapshots.TryGetValue(cacheKey, out var prev) && prev.Equals(snap)) continue;
                this.snapshots[cacheKey] = snap;

                // Fan out per-subscriber so each work-item channel receives the tick.
                var workItems = this.subs
                    .Where(kvp => string.Equals(kvp.Value.WorktreePath, path, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(kvp.Value.BaseBranch, baseBranch, StringComparison.Ordinal))
                    .ToList();
                foreach (var (wid, sub) in workItems)
                {
                    await this.channel.EmitAsync(
                        wid,
                        "task.worktree-tick",
                        new
                        {
                            workstreamId = sub.WorkstreamId,
                            worktreePath = sub.WorktreePath,
                            baseBranch = sub.BaseBranch,
                            added = snap.Added,
                            modified = snap.Modified,
                            deleted = snap.Deleted,
                            paths = new
                            {
                                added = snap.AddedPaths,
                                modified = snap.ModifiedPaths,
                                deleted = snap.DeletedPaths,
                            },
                            ahead = snap.Ahead,
                            behind = snap.Behind,
                            dirty = snap.Dirty,
                            head = snap.HeadSha == null ? null : new
                            {
                                sha = snap.HeadSha,
                                message = snap.HeadMessage,
                                author = snap.HeadAuthor,
                                at = snap.HeadAt,
                            },
                            truncated = snap.Truncated,
                            branchDiff = new
                            {
                                added = snap.BranchDiffAdded ?? (IReadOnlyList<string>)Array.Empty<string>(),
                                modified = snap.BranchDiffModified ?? (IReadOnlyList<string>)Array.Empty<string>(),
                                deleted = snap.BranchDiffDeleted ?? (IReadOnlyList<string>)Array.Empty<string>(),
                            },
                        },
                        ct: ct);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "WorktreeChangeFeed tick failed for {Path} base={Base}", path, baseBranch);
            }
        }
    }

    private static string CacheKey(string path, string baseBranch) =>
        $"{path}\0{baseBranch}";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.loop = Task.Run(() => this.LoopAsync(this.cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.cts?.Cancel();
        if (this.loop is not null)
        {
            try { await this.loop; } catch { /* swallow shutdown */ }
        }
    }

    public void Dispose()
    {
        this.cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!this.subs.IsEmpty) await this.RunOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { this.logger.LogDebug(ex, "WorktreeChangeFeed loop tick errored"); }
            try { await Task.Delay(TickInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Build a snapshot of the worktree against the given base branch.
    /// Returns null if the path isn't a git repo (a misconfigured
    /// workstream shouldn't break the loop).
    /// <para>The branchDiff arrays match
    /// <see cref="WorktreeCommitsScanner.BranchTotals"/>: anchored at
    /// the fork point (<c>git merge-base origin/{baseBranch} HEAD</c>),
    /// folding tracked-but-uncommitted + untracked entries from
    /// <c>git status --porcelain</c> so reviewers see the same set of
    /// files in the Worktree-tab pills as in the PR's file list.</para>
    /// </summary>
    public async Task<Snapshot?> SampleAsync(string path, string baseBranch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git"))) return null;

        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch;

        // git status --porcelain=v2 --branch — kept for the dirty paths
        // (Added/Modified/Deleted path lists + the dirty scalar).
        var status = await RunGitAsync(path, "status --porcelain=v2 --branch", ct);
        if (status is null) return null;
        var parsed = ParsePorcelainV2(status);

        // git log -1 for HEAD
        var head = await RunGitAsync(path, "log -1 --format=%H%x09%s%x09%an%x09%aI", ct);
        string? sha = null, message = null, author = null;
        DateTimeOffset? at = null;
        if (!string.IsNullOrEmpty(head))
        {
            var parts = head!.Trim().Split('\t', 4);
            if (parts.Length >= 4)
            {
                sha = parts[0];
                message = parts[1];
                author = parts[2];
                if (DateTimeOffset.TryParse(parts[3], out var dt)) at = dt;
            }
        }

        var truncated =
            parsed.AddedPaths.Count    >= MaxPathsPerCategory
            || parsed.ModifiedPaths.Count >= MaxPathsPerCategory
            || parsed.DeletedPaths.Count  >= MaxPathsPerCategory;

        // Ahead/behind vs the workstream's REAL base ref. Mirrors
        // WorktreeCommitsScanner so the Worktree-tab pills (live snap)
        // never disagree with /commits' counters.
        int ahead = 0, behind = 0;
        var revList = await RunGitAsync(path,
            $"rev-list --left-right --count origin/{resolvedBase}...HEAD", ct);
        if (!string.IsNullOrEmpty(revList))
        {
            var rlParts = revList!.Trim().Split('\t');
            if (rlParts.Length == 2)
            {
                int.TryParse(rlParts[0], out behind);
                int.TryParse(rlParts[1], out ahead);
            }
        }

        // Branch diff vs the workstream's fork point. The merge-base is
        // the most recent common ancestor of origin/{baseBranch} and
        // HEAD — diffing from there scopes the result to "what THIS
        // workstream contributed", regardless of how the base branch
        // has advanced (sibling PRs landing, etc).
        var mergeBase = await RunGitAsync(path, $"merge-base origin/{resolvedBase} HEAD", ct);
        var diffAnchor = !string.IsNullOrWhiteSpace(mergeBase)
            ? mergeBase!.Trim()
            : $"origin/{resolvedBase}";

        var branchDiffAdded = new List<string>();
        var branchDiffModified = new List<string>();
        var branchDiffDeleted = new List<string>();
        var diffOutput = await RunGitAsync(path, $"diff --name-status {diffAnchor}", ct);
        if (!string.IsNullOrEmpty(diffOutput))
        {
            foreach (var rawLine in diffOutput!.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                // `git diff --name-status` outputs <status>\t<path>, but
                // rename / copy lines are <status>\t<old>\t<new>. Split
                // on TAB and treat R/C as a modification on the new path.
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var diffStatus = parts[0];
                if (diffStatus.StartsWith("R", StringComparison.Ordinal) || diffStatus.StartsWith("C", StringComparison.Ordinal))
                {
                    if (parts.Length >= 3) branchDiffModified.Add(parts[2]);
                    continue;
                }
                var filePath = parts[1];
                if (string.IsNullOrEmpty(filePath)) continue;
                switch (diffStatus)
                {
                    case "A": branchDiffAdded.Add(filePath); break;
                    case "D": branchDiffDeleted.Add(filePath); break;
                    case "M":
                    case "T": branchDiffModified.Add(filePath); break;
                    default: branchDiffModified.Add(filePath); break;
                }
            }
        }

        // Fold uncommitted entries into the totals so untracked files
        // (which `git diff` is blind to) and tracked-but-uncommitted
        // changes both surface in the Worktree-tab pills — same
        // semantics as WorktreeCommitsScanner.BranchTotals.
        var seen = new HashSet<string>(branchDiffAdded, StringComparer.Ordinal);
        seen.UnionWith(branchDiffModified);
        seen.UnionWith(branchDiffDeleted);
        foreach (var p in parsed.AddedPaths)    if (seen.Add(p)) branchDiffAdded.Add(p);
        foreach (var p in parsed.ModifiedPaths) if (seen.Add(p)) branchDiffModified.Add(p);
        foreach (var p in parsed.DeletedPaths)  if (seen.Add(p)) branchDiffDeleted.Add(p);

        return new Snapshot(
            Added:    parsed.AddedPaths.Count,
            Modified: parsed.ModifiedPaths.Count,
            Deleted:  parsed.DeletedPaths.Count,
            AddedPaths:    parsed.AddedPaths.Take(MaxPathsPerCategory).ToList(),
            ModifiedPaths: parsed.ModifiedPaths.Take(MaxPathsPerCategory).ToList(),
            DeletedPaths:  parsed.DeletedPaths.Take(MaxPathsPerCategory).ToList(),
            Ahead:  ahead,
            Behind: behind,
            Dirty:  parsed.AddedPaths.Count + parsed.ModifiedPaths.Count + parsed.DeletedPaths.Count,
            HeadSha:     sha,
            HeadMessage: message,
            HeadAuthor:  author,
            HeadAt:      at,
            Truncated:   truncated,
            BranchDiffAdded:    branchDiffAdded,
            BranchDiffModified: branchDiffModified,
            BranchDiffDeleted:  branchDiffDeleted);
    }

    private static async Task<string?> RunGitAsync(string cwd, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse <c>git status --porcelain=v2 --branch</c> into ahead/behind + path lists.</summary>
    public static PorcelainV2 ParsePorcelainV2(string text)
    {
        var added    = new List<string>();
        var modified = new List<string>();
        var deleted  = new List<string>();
        int ahead = 0, behind = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // # branch.ab +1 -2
            if (line.StartsWith("# branch.ab "))
            {
                var rest = line.Substring("# branch.ab ".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in rest)
                {
                    if (token.StartsWith("+") && int.TryParse(token.Substring(1), out var a)) ahead = a;
                    else if (token.StartsWith("-") && int.TryParse(token.Substring(1), out var b)) behind = b;
                }
                continue;
            }

            // Comment lines start with #.
            if (line.StartsWith("#")) continue;

            // 1 / 2 entries: " 1 XY ... <path>"  /  " 2 XY ... <path>\t<orig>"
            // Untracked entries: "? <path>"
            // Ignored entries:   "! <path>"
            if (line.Length < 2) continue;
            var marker = line[0];
            var afterSpace = line.IndexOf(' ');
            if (afterSpace < 0 || afterSpace + 1 >= line.Length) continue;

            switch (marker)
            {
                case '?':
                    added.Add(line.Substring(2));
                    break;
                case '1':
                case '2':
                {
                    // 1 XY sub modeH modeI modeW hashH hashI path
                    // Tokens 0=marker 1=XY 2..7=metadata 8=path (rename has \t after)
                    var tokens = line.Split(' ', 9);
                    if (tokens.Length < 9) continue;
                    var xy = tokens[1];
                    var path = tokens[8];
                    var tabIx = path.IndexOf('\t');
                    if (tabIx > 0) path = path.Substring(0, tabIx);

                    // Index (X) takes priority over worktree (Y) for our coarse bucketing.
                    var ch = xy.Length >= 1 ? xy[0] : '.';
                    if (ch == '.') ch = xy.Length >= 2 ? xy[1] : '.';
                    switch (ch)
                    {
                        case 'A': added.Add(path); break;
                        case 'M':
                        case 'R':
                        case 'C':
                        case 'T':
                        case 'U': modified.Add(path); break;
                        case 'D': deleted.Add(path); break;
                        default: modified.Add(path); break;
                    }
                    break;
                }
            }
        }

        return new PorcelainV2(added, modified, deleted, ahead, behind);
    }

    public sealed record Subscription(string WorkstreamId, string WorktreePath, string BaseBranch);

    public sealed record Snapshot(
        int Added,
        int Modified,
        int Deleted,
        IReadOnlyList<string> AddedPaths,
        IReadOnlyList<string> ModifiedPaths,
        IReadOnlyList<string> DeletedPaths,
        int Ahead,
        int Behind,
        int Dirty,
        string? HeadSha,
        string? HeadMessage,
        string? HeadAuthor,
        DateTimeOffset? HeadAt,
        bool Truncated,
        IReadOnlyList<string>? BranchDiffAdded = null,
        IReadOnlyList<string>? BranchDiffModified = null,
        IReadOnlyList<string>? BranchDiffDeleted = null)
    {
        // Equals(): cheap content comparison so an unchanged tick is free.
        // Phase 49 follow-up — include branchDiff arrays so a tick that
        // only changes the cumulative branch totals (e.g. agent commits
        // a new file, dirty count stays at 0 but branchDiff grows)
        // still emits.
        public bool Equals(Snapshot? other) =>
            other is not null
            && this.Added == other.Added && this.Modified == other.Modified && this.Deleted == other.Deleted
            && this.Ahead == other.Ahead && this.Behind == other.Behind
            && this.HeadSha == other.HeadSha
            && PathsEqual(this.AddedPaths, other.AddedPaths)
            && PathsEqual(this.ModifiedPaths, other.ModifiedPaths)
            && PathsEqual(this.DeletedPaths, other.DeletedPaths)
            && PathsEqual(this.BranchDiffAdded ?? Array.Empty<string>(), other.BranchDiffAdded ?? Array.Empty<string>())
            && PathsEqual(this.BranchDiffModified ?? Array.Empty<string>(), other.BranchDiffModified ?? Array.Empty<string>())
            && PathsEqual(this.BranchDiffDeleted ?? Array.Empty<string>(), other.BranchDiffDeleted ?? Array.Empty<string>());

        public override int GetHashCode() => HashCode.Combine(this.Added, this.Modified, this.Deleted, this.Ahead, this.Behind, this.HeadSha);

        private static bool PathsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public sealed record PorcelainV2(
        List<string> AddedPaths,
        List<string> ModifiedPaths,
        List<string> DeletedPaths,
        int Ahead,
        int Behind);
}
