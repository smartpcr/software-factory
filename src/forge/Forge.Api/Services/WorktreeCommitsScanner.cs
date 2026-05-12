// -----------------------------------------------------------------------
// <copyright file="WorktreeCommitsScanner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;

/// <summary>
/// Phase 49 follow-up — single source of truth for the
/// <c>/api/work-items/{id}/commits</c> response shape. Lifted out of
/// <c>TaskExecutionEndpoints</c> so:
/// <list type="bullet">
///   <item><description>The diff/log/status parsing can be unit-tested
///   against a real temp git repo without standing up the full Forge web
///   app, DB, DI, etc. (see <c>Phase49WorktreeCommitsScannerTests</c>).</description></item>
///   <item><description>The merge-base anchor fix lives in one place: the
///   pre-fix code diffed the working tree against <c>origin/{baseBranch}</c>
///   directly, which inflated the Worktree-tab counters whenever
///   <c>origin/{baseBranch}</c> had advanced past the workstream's fork
///   point (other PRs merged in the meantime). Observed 2026-05-10 on
///   failover-cluster:XRAFT stage 1.2: <c>+0 ~0 -45</c> was reported even
///   though the workstream itself had committed nothing — the 45
///   "deletions" were actually files PR #2 (sibling stage 1.1) had
///   added on <c>origin/feature/xraft</c> that the stale 1.2 worktree
///   wasn't aware of. Anchoring against the merge-base eliminates that
///   class of false positive.</description></item>
/// </list>
/// </summary>
internal static class WorktreeCommitsScanner
{
    /// <summary>One row of <c>git log --name-status</c> output for a single commit.</summary>
    public sealed record CommitFileChange(string Status, string Path, string? OldPath = null);

    /// <summary>One commit on the workstream's branch ahead of the fork point.</summary>
    public sealed record CommitEntry(
        string Sha,
        string Message,
        string Author,
        string At,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted,
        IReadOnlyList<CommitFileChange> Other);

    /// <summary><c>git status --porcelain</c> classified into add/modify/delete buckets.</summary>
    public sealed record UncommittedChanges(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted);

    /// <summary>
    /// Cumulative committed + uncommitted diff between the workstream's
    /// fork point on <c>origin/{baseBranch}</c> and the worktree's
    /// working tree. Deduplicated per-path across commits (a file added
    /// in commit 1 and modified in commit 2 collapses to one Added entry
    /// — matches what a reviewer sees in the PR's file list).
    /// </summary>
    public sealed record BranchTotals(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted);

    public sealed record ScanResult(
        int Ahead,
        int Behind,
        string? HeadSha,
        IReadOnlyList<CommitEntry> Commits,
        UncommittedChanges Uncommitted,
        BranchTotals BranchTotals);

    /// <summary>
    /// Scan a worktree for its commits + branch totals + uncommitted
    /// changes relative to the fork point on <c>origin/{baseBranch}</c>.
    /// Returns an empty <see cref="ScanResult"/> if <paramref name="worktreePath"/>
    /// doesn't exist on disk.
    /// </summary>
    /// <param name="worktreePath">Absolute path to the worktree directory.</param>
    /// <param name="baseBranch">Branch the worktree forked from; used as the diff anchor (via merge-base).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<ScanResult> ScanAsync(string worktreePath, string baseBranch, CancellationToken ct)
        => ScanAsync(worktreePath, baseBranch, fetchUpstream: false, ct);

    /// <summary>
    /// Per-worktree timestamp of the last successful upstream fetch. Used
    /// by the stale-while-revalidate path to throttle background fetches
    /// to one per <see cref="FetchCooldown"/> window (operators rapidly
    /// reopening the Worktree tab don't cause N concurrent git-fetch
    /// processes to pile up on the same .git directory).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastFetchAt = new();
    private static readonly TimeSpan FetchCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <para>Scan with explicit control over whether to block on a fresh
    /// <c>git fetch origin {baseBranch}</c>. The blocking fetch is the
    /// single biggest cost (typical 1–2s round-trip to GitHub) so the
    /// default <c>fetchUpstream=false</c> path skips it and uses
    /// whatever <c>origin/{baseBranch}</c> ref is locally cached.
    /// A background fetch is fired (rate-limited to one per 15s per
    /// worktree) so the NEXT call sees fresh refs — classic
    /// stale-while-revalidate.</para>
    ///
    /// <para>Cold-load (no fetch, parallelized git ops): ~600ms.</para>
    /// <para>Refresh path (<c>fetchUpstream=true</c>): ~2–4s.</para>
    /// </summary>
    public static async Task<ScanResult> ScanAsync(string worktreePath, string baseBranch, bool fetchUpstream, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            return Empty();
        }

        if (fetchUpstream)
        {
            // Operator explicitly asked for a refresh (Worktree tab's
            // refresh button passes ?refresh=true). Scope the fetch to
            // the single base branch rather than `git fetch origin
            // --quiet` (which walks every ref the remote knows about);
            // measured ~2s either way but the scoped form avoids
            // pulling unrelated refs into the local clone.
            await RunGitAsync(worktreePath, ct, "fetch", "origin", baseBranch, "--quiet");
            LastFetchAt[worktreePath] = DateTime.UtcNow;
        }
        else
        {
            // Stale-while-revalidate. Fire a background fetch (no
            // await) so the next caller within this worktree benefits
            // from fresh refs. Throttled per-worktree so a tab open by
            // 5 panes simultaneously doesn't fan out 5 fetches.
            var now = DateTime.UtcNow;
            var last = LastFetchAt.TryGetValue(worktreePath, out var prior) ? prior : DateTime.MinValue;
            if (now - last > FetchCooldown)
            {
                LastFetchAt[worktreePath] = now;
                var bgBranch = baseBranch;
                var bgPath = worktreePath;
                _ = Task.Run(async () =>
                {
                    try { await RunGitAsync(bgPath, CancellationToken.None, "fetch", "origin", bgBranch, "--quiet"); }
                    catch { /* best-effort; next scan will retry */ }
                });
            }
        }

        // Phase 49 follow-up — run the four independent local git ops
        // in parallel. They're all read-only against the same .git
        // (git's internal locking is fine with concurrent reads) and
        // none depend on each other's output. Sequential they totalled
        // ~1300ms in the failover-cluster:XRAFT repo; parallel collapses
        // to the slowest single op (~600ms for `diff` or `merge-base`).
        var headTask = RunGitAsync(worktreePath, ct, "rev-parse", "HEAD");
        var statusTask = RunGitAsync(worktreePath, ct, "status", "--porcelain");
        var revListTask = RunGitAsync(worktreePath, ct,
            "rev-list", "--left-right", "--count", $"origin/{baseBranch}...HEAD");
        var mergeBaseTask = RunGitAsync(worktreePath, ct,
            "merge-base", $"origin/{baseBranch}", "HEAD");

        await Task.WhenAll(headTask, statusTask, revListTask, mergeBaseTask);

        var (headCode, headSha, _) = headTask.Result;
        var (stCode, stOut, _) = statusTask.Result;
        var (acCode, acOut, _) = revListTask.Result;
        var (mbCode, mbOut, _) = mergeBaseTask.Result;

        // Ahead / behind vs the remote base branch. This stays anchored
        // at the CURRENT remote tip so the operator can see "this branch
        // is behind by N commits"; the diff scope below uses merge-base
        // instead so upstream-only changes don't pollute the workstream's
        // contribution counters.
        int ahead = 0, behind = 0;
        if (acCode == 0)
        {
            var parts = acOut.Trim().Split('\t');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out behind);
                int.TryParse(parts[1], out ahead);
            }
        }

        // HEAD sha for convenience.
        var headShaStr = headCode == 0 ? headSha.Trim() : null;

        // Phase 49 follow-up — use the fork-point (merge-base of
        // origin/{baseBranch} and HEAD) as the diff anchor instead of
        // origin/{baseBranch} directly. The merge-base is the most
        // recent common ancestor — i.e. exactly the point where this
        // workstream's branch diverged. Diffing from there shows ONLY
        // what this workstream contributed, regardless of how
        // origin/{baseBranch} has advanced since (other PRs merged,
        // sibling stages, etc.). If merge-base fails (HEAD totally
        // disconnected from the remote — unusual; fresh worktree before
        // first fetch?), fall back to the literal origin ref so the
        // endpoint stays non-empty.
        var diffAnchor = mbCode == 0 && !string.IsNullOrWhiteSpace(mbOut)
            ? mbOut.Trim()
            : $"origin/{baseBranch}";

        // Commit list + branch totals diff — also parallel, both depend
        // on diffAnchor which is now resolved.
        var logTask = RunGitAsync(worktreePath, ct,
            "log",
            $"{diffAnchor}..HEAD",
            "--pretty=format:__COMMIT__%H%x09%aI%x09%aN%x09%s",
            "--name-status");
        var diffTask = RunGitAsync(worktreePath, ct, "diff", "--name-status", diffAnchor);

        await Task.WhenAll(logTask, diffTask);

        var (logCode, logOut, _) = logTask.Result;
        var (totalsCode, totalsOut, _) = diffTask.Result;

        var commits = new List<CommitEntry>();
        if (logCode == 0 && !string.IsNullOrWhiteSpace(logOut))
        {
            CommitEntry? cur = null;
            var added = new List<string>();
            var modified = new List<string>();
            var deleted = new List<string>();
            var other = new List<CommitFileChange>();
            void Flush()
            {
                if (cur is null) return;
                commits.Add(cur with
                {
                    Added = added.ToArray(),
                    Modified = modified.ToArray(),
                    Deleted = deleted.ToArray(),
                    Other = other.ToArray(),
                });
                added.Clear(); modified.Clear(); deleted.Clear(); other.Clear();
            }
            foreach (var rawLine in logOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("__COMMIT__", StringComparison.Ordinal))
                {
                    Flush();
                    var fields = line.Substring("__COMMIT__".Length).Split('\t');
                    if (fields.Length >= 4)
                    {
                        cur = new CommitEntry(
                            Sha: fields[0],
                            Message: fields[3],
                            Author: fields[2],
                            At: fields[1],
                            Added: Array.Empty<string>(),
                            Modified: Array.Empty<string>(),
                            Deleted: Array.Empty<string>(),
                            Other: Array.Empty<CommitFileChange>());
                    }
                }
                else if (cur is not null)
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var status = parts[0];
                    // Renames/copies show 3 cols: R100 oldpath newpath. Track
                    // the rename in `other` and the new path under modified
                    // so the summary still surfaces it.
                    if (status.StartsWith("R", StringComparison.Ordinal) || status.StartsWith("C", StringComparison.Ordinal))
                    {
                        if (parts.Length >= 3)
                        {
                            other.Add(new CommitFileChange(status, parts[2], parts[1]));
                            modified.Add(parts[2]);
                        }
                        continue;
                    }
                    var path = parts[1];
                    switch (status)
                    {
                        case "A": added.Add(path); break;
                        case "M": modified.Add(path); break;
                        case "D": deleted.Add(path); break;
                        default: other.Add(new CommitFileChange(status, path)); break;
                    }
                }
            }
            Flush();
        }

        // Currently uncommitted (working-tree + staged, excluding untracked
        // dirs that the worktree-feed already skips). Result already
        // captured above via parallel statusTask.
        var uAdded = new List<string>();
        var uMod = new List<string>();
        var uDel = new List<string>();
        if (stCode == 0)
        {
            foreach (var rawLine in stOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length < 4) continue;
                var status = line.Substring(0, 2);
                var path = line.Substring(3);
                // The two columns are <staged><unstaged>. We classify by the
                // first non-space column, treating ?? (untracked) as added.
                if (status[0] == 'A' || status[0] == '?' || status == "??") uAdded.Add(path);
                else if (status[0] == 'D' || status[1] == 'D') uDel.Add(path);
                else uMod.Add(path);
            }
        }

        // Cumulative branch totals (committed + uncommitted) vs the fork
        // point. `git diff --name-status <merge-base>` compares the
        // WORKING TREE (HEAD + dirty-but-tracked) against the fork point
        // so committed-and-modified changes both fold into one status
        // per path. Untracked files do NOT show up in git diff, so we
        // union the uncommitted-changes set (which IS populated from
        // `git status --porcelain` and includes untracked) below to
        // catch new files that haven't been git-add'd yet.
        // Result already captured above via parallel diffTask.
        var tAdded = new List<string>();
        var tMod = new List<string>();
        var tDel = new List<string>();
        if (totalsCode == 0)
        {
            foreach (var rawLine in totalsOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var status = parts[0];
                // Renames / copies: R100\t<old>\t<new> — bucket as Modified.
                if (status.StartsWith("R", StringComparison.Ordinal) || status.StartsWith("C", StringComparison.Ordinal))
                {
                    if (parts.Length >= 3) tMod.Add(parts[2]);
                    continue;
                }
                var path = parts[1];
                switch (status)
                {
                    case "A": tAdded.Add(path); break;
                    case "D": tDel.Add(path); break;
                    case "M":
                    case "T": tMod.Add(path); break;
                    default: tMod.Add(path); break;
                }
            }
        }

        // Phase 49 follow-up — union uncommitted entries that aren't
        // already in the diff totals. Without this, untracked files
        // (which `git diff` is blind to) wouldn't surface in the
        // Worktree-tab pills at all — the engineer would write a new
        // file mid-iter and see `+0` until they ran `git add`. We don't
        // overwrite existing entries (a file already in tAdded stays
        // tAdded even if it's been further-modified uncommitted; the
        // NET effect on the base is still an add).
        var seen = new HashSet<string>(tAdded, StringComparer.Ordinal);
        seen.UnionWith(tMod);
        seen.UnionWith(tDel);
        foreach (var p in uAdded) if (seen.Add(p)) tAdded.Add(p);
        foreach (var p in uMod) if (seen.Add(p)) tMod.Add(p);
        foreach (var p in uDel) if (seen.Add(p)) tDel.Add(p);

        return new ScanResult(
            Ahead: ahead,
            Behind: behind,
            HeadSha: headShaStr,
            Commits: commits,
            Uncommitted: new UncommittedChanges(uAdded, uMod, uDel),
            BranchTotals: new BranchTotals(tAdded, tMod, tDel));
    }

    private static ScanResult Empty() => new(
        Ahead: 0,
        Behind: 0,
        HeadSha: null,
        Commits: Array.Empty<CommitEntry>(),
        Uncommitted: new UncommittedChanges(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        BranchTotals: new BranchTotals(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));

    private static async Task<(int code, string stdout, string stderr)> RunGitAsync(
        string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
