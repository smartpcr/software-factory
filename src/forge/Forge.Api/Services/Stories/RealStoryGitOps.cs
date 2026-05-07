// -----------------------------------------------------------------------
// <copyright file="RealStoryGitOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Forge.Api.Persistence;

/// <summary>
/// Real-git implementation of <see cref="IStoryGitOps"/> — drives the user's
/// actual repo at <see cref="RepoRow.CloneRoot"/> via the <c>git</c> CLI.
/// Branches go to <c>origin</c>; file existence / read goes through
/// <c>git show origin/{branch}:{path}</c> so we don't need to keep multiple
/// worktrees mounted.
///
/// <para>
/// Operations on the orchestrator's gate branches (<c>story/.../plan</c>,
/// <c>story/.../qa</c>, <c>ws/.../...</c>) all happen via a per-call
/// dedicated worktree under <c>{cloneRoot}/.worktree/spawn-{slug}/</c> —
/// matches the convention <see cref="Services.CrewQueueWorker"/> uses for
/// live work-item exec, and keeps story commits out of the operator's
/// primary checkout.
/// </para>
/// </summary>
public sealed class RealStoryGitOps : IStoryGitOps
{
    private readonly ILogger<RealStoryGitOps>? logger;

    /// <summary>
    /// Phase 42 — per-branch git lock. Concurrent architect agents share one
    /// worktree (git refuses two worktrees on one branch); this serialises
    /// staging + commit ops so they don't race the git index.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> branchLocks = new();

    public RealStoryGitOps(ILogger<RealStoryGitOps>? logger = null)
    {
        this.logger = logger;
    }

    public async Task EnsureBranchAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Make sure we have the latest base.
        await this.GitAsync(root, ct, "fetch", "origin", baseBranch);

        // Does the branch already exist on origin? If so, make sure local
        // tracking ref is up to date and exit.
        var (lsCode, lsOut, _) = await this.RunGitCaptureAsync(root, ct, "ls-remote", "--heads", "origin", branch);
        if (lsCode == 0 && !string.IsNullOrWhiteSpace(lsOut))
        {
            await this.GitAsync(root, ct, "fetch", "origin", branch);
            return;
        }

        // Doesn't exist remotely — create + push.
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);
        await this.EnsureWorktreeAsync(root, wtPath, branch, baseBranch, ct);
        await this.GitAsync(wtPath, ct, "push", "-u", "origin", branch);
    }

    public async Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        await this.EnsureWorktreeAsync(root, wtPath, branch, "main", ct);

        // Materialize the file map under the worktree.
        foreach (var (relPath, content) in files)
        {
            // Forbid escapes — every path must be repo-relative + forward-slash.
            if (Path.IsPathRooted(relPath) || relPath.Contains(".."))
                throw new InvalidOperationException($"Refusing absolute or parent-traversal path '{relPath}' in CommitFilesAsync.");
            var full = Path.Combine(wtPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, content, ct);
        }

        await this.GitAsync(wtPath, ct, "add", "-A");

        // Detect "nothing to commit" — git returns non-zero for an empty
        // commit unless --allow-empty is passed; we don't want empty
        // history rows.
        var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "status", "--porcelain");
        if (statusCode == 0 && string.IsNullOrWhiteSpace(statusOut))
        {
            this.logger?.LogInformation("CommitFilesAsync: no changes to commit on branch {Branch}", branch);
        }
        else
        {
            await this.GitAsync(wtPath, ct, "-c", "user.email=forge-architect@local", "-c", "user.name=Forge Architect", "commit", "-m", message);
        }

        await this.PushWithAutoRecoverAsync(wtPath, branch, ct);

        // Return the head sha of the branch.
        var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "rev-parse", "HEAD");
        return shaCode == 0 ? shaOut.Trim() : string.Empty;
    }

    public async Task<bool> FileExistsAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        await this.RunGitCaptureAsync(root, ct, "fetch", "origin", branch);
        var (code, _, _) = await this.RunGitCaptureAsync(root, ct, "cat-file", "-e", $"origin/{branch}:{filePath}");
        return code == 0;
    }

    public async Task<string?> ReadFileAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        await this.RunGitCaptureAsync(root, ct, "fetch", "origin", branch);
        var (code, stdout, _) = await this.RunGitCaptureAsync(root, ct, "show", $"origin/{branch}:{filePath}");
        return code == 0 ? stdout : null;
    }

    public async Task MergeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Phase 45 follow-up — git refuses to check out the same branch in
        // two different worktrees. EnsureBranchAsync may have already created
        // a worktree for this base branch (e.g. when the operator chose
        // feature/memory-pressure as their spawn base). Reuse it. Otherwise
        // fall back to a dedicated `merge-{base}` worktree.
        var wtPath = await this.TryFindExistingWorktreeAsync(root, baseBranch, ct);
        if (wtPath is null)
        {
            var sanitized = SanitizeBranchSlug($"merge-{baseBranch}");
            wtPath = WorktreePath(root, sanitized);
            await this.EnsureWorktreeAsync(root, wtPath, baseBranch, baseBranch, ct);
        }

        // Phase 45 follow-up — sync-not-re-merge.
        //
        // Story flow always calls this AFTER provider.MergePrAsync, which
        // already merged the source into the base on the upstream remote
        // (ADO squash merge / GitHub merge button). origin/{baseBranch}
        // therefore already contains every file the source brought in.
        // The previous code did `merge --no-ff origin/{branch}` here on
        // top, producing add/add conflicts on every file the upstream
        // merge had just added (operator hit this on 2026-05-05 with
        // architecture.md, tech-spec.md, implementation-plan.md,
        // e2e-scenarios.md, workstreams.json — all freshly added by the
        // ADO squash merge, then re-added by the local merge attempt).
        //
        // The local worktree's job is to MIRROR the remote, not to do its
        // own merge. fetch + reset --hard origin/{baseBranch} accomplishes
        // that cleanly. We don't push anything (the upstream already has
        // the right tip).
        await this.GitAsync(wtPath, ct, "fetch", "origin", baseBranch);
        await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{baseBranch}");

        // Sanity check — the source branch's tip should now be reachable
        // from origin/{baseBranch} (the upstream merge brought it in). If
        // it isn't, the upstream merge somehow didn't include this source;
        // the operator needs to inspect manually. Log a warning but don't
        // fail the call: the merge upstream is the source of truth, so a
        // local-only mismatch is a recoverable cache divergence.
        try
        {
            await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
            var (mbCode, _, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "merge-base", "--is-ancestor", $"origin/{branch}", "HEAD");
            if (mbCode != 0)
            {
                this.logger?.LogWarning(
                    "MergeAsync sanity: origin/{Branch} is NOT an ancestor of HEAD on {Base} after sync. "
                    + "Upstream merge may not have actually merged this source — investigate the PR state.",
                    branch, baseBranch);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex,
                "MergeAsync ancestor sanity check failed (non-fatal); local sync to origin/{Base} already done.",
                baseBranch);
        }
    }

    /// <summary>
    /// Phase 45 follow-up — return the absolute path of an existing worktree
    /// already checked out on <paramref name="branch"/>, or null when no
    /// worktree has it. Parses <c>git worktree list --porcelain</c> output:
    /// each worktree block is <c>worktree {path}\n... branch refs/heads/{name}</c>
    /// separated by blank lines. Returning a hit lets <see cref="MergeAsync"/>
    /// and <see cref="RebaseBaseBranchAsync"/> reuse the existing worktree
    /// instead of triggering "branch is already used by worktree" from git.
    /// </summary>
    private async Task<string?> TryFindExistingWorktreeAsync(string root, string branch, CancellationToken ct)
    {
        var (code, stdout, _) = await this.RunGitCaptureAsync(root, ct, "worktree", "list", "--porcelain");
        if (code != 0 || string.IsNullOrEmpty(stdout)) return null;

        string? currentPath = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                var b = line["branch refs/heads/".Length..].Trim();
                if (string.Equals(b, branch, StringComparison.Ordinal) && !string.IsNullOrEmpty(currentPath))
                {
                    return currentPath;
                }
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                currentPath = null; // block separator
            }
        }
        return null;
    }

    /// <summary>
    /// Phase 45 — bring <paramref name="baseBranch"/> up to <c>origin/{upstream}</c>
    /// inside a dedicated worktree. Used when the operator changes a spawn's
    /// base branch and we need to pull in any upstream commits before
    /// replaying the spawn's already-merged PRs onto it. Pushes the result
    /// back to origin so subsequent merges target the rebased tip.
    /// </summary>
    public async Task RebaseBaseBranchAsync(
        RepoRow? repo, string baseBranch, string upstreamBranch, CancellationToken ct = default)
    {
        if (string.Equals(baseBranch, upstreamBranch, StringComparison.Ordinal))
        {
            // Rebasing main onto main is a no-op — and a self-loop fetch races.
            return;
        }

        var root = ResolveCloneRoot(repo);

        // Phase 45 follow-up — same worktree-reuse rule as MergeAsync. If
        // the base branch is already checked out somewhere (typically the
        // worktree EnsureBranchAsync created when first materialising the
        // branch), reuse it. Otherwise create a fresh `rebase-{base}` one.
        var wtPath = await this.TryFindExistingWorktreeAsync(root, baseBranch, ct);
        if (wtPath is null)
        {
            var sanitized = SanitizeBranchSlug($"rebase-{baseBranch}");
            wtPath = WorktreePath(root, sanitized);
            await this.EnsureWorktreeAsync(root, wtPath, baseBranch, upstreamBranch, ct);
        }

        await this.GitAsync(wtPath, ct, "fetch", "origin", baseBranch);
        await this.GitAsync(wtPath, ct, "fetch", "origin", upstreamBranch);
        await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{baseBranch}");

        // `git rebase` against the upstream tip; `--rebase-merges` keeps any
        // merge commits the operator already pulled in. A clean linear base
        // (no prior merge commits) just fast-forwards.
        await this.GitAsync(wtPath, ct,
            "-c", "user.email=forge-rebaser@local",
            "-c", "user.name=Forge",
            "rebase", "--rebase-merges", $"origin/{upstreamBranch}");

        // `--force-with-lease` so we never clobber a concurrent push from
        // another operator. Branch protection on a base branch should reject
        // this push anyway; that's a deliberate guard.
        await this.GitAsync(wtPath, ct, "push", "--force-with-lease", "origin", baseBranch);
    }

    /// <inheritdoc/>
    public async Task<bool> MergeUpstreamIntoBranchAsync(
        RepoRow? repo, string branch, string upstreamBranch, bool force = true, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        // Ensure the worktree is up to date.
        await this.EnsureWorktreeAsync(root, wtPath, branch, branch, ct);
        await this.GitAsync(wtPath, ct, "fetch", "origin", upstreamBranch);
        await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
        await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{branch}");

        // Check if upstream is already an ancestor — nothing to merge.
        var (mbCode, _, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "merge-base", "--is-ancestor", $"origin/{upstreamBranch}", "HEAD");
        if (mbCode == 0)
        {
            if (force)
            {
                // Caller (force-merge / story AutoMerge) wants this PR
                // merged regardless of provider-side stale-mergeable
                // cache. Source is already a descendant of upstream so
                // the tree is fine, but the provider may still report
                // "merge conflicts" from a stale cache. Create an empty
                // commit + push so the provider sees a NEW source SHA
                // and recomputes mergeable. Empty commits get squashed
                // away on PR completion, so they don't pollute base
                // history.
                await this.RunGitCaptureAsync(
                    wtPath, ct,
                    "-c", "user.email=forge-merger@local",
                    "-c", "user.name=Forge",
                    "commit", "--allow-empty", "-m",
                    $"forge: nudge mergeable on {branch} (origin/{upstreamBranch} already ancestor)");
                await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
                this.logger?.LogInformation(
                    "MergeUpstreamIntoBranchAsync: origin/{Upstream} already ancestor of {Branch}; pushed empty commit to nudge provider mergeable.",
                    upstreamBranch, branch);
                return true;
            }
            this.logger?.LogInformation(
                "MergeUpstreamIntoBranchAsync: origin/{Upstream} already ancestor of {Branch}, skipping.",
                upstreamBranch, branch);
            return true;
        }

        // Attempt a normal merge from upstream.
        var (mergeCode, mergeOut, mergeErr) = await this.RunGitCaptureAsync(
            wtPath, ct,
            "-c", "user.email=forge-merger@local",
            "-c", "user.name=Forge",
            "merge", "--no-edit", $"origin/{upstreamBranch}");

        if (mergeCode != 0)
        {
            await this.RunGitCaptureAsync(wtPath, ct, "merge", "--abort");

            if (!force)
            {
                this.logger?.LogInformation(
                    "MergeUpstreamIntoBranchAsync: clean merge of origin/{Upstream} into {Branch} had conflicts; "
                    + "force=false so surfacing the conflict to the caller without -X ours.",
                    upstreamBranch, branch);
                return false;
            }

            // Merge conflict + force=true → auto-resolve with -X ours by
            // keeping the source branch's (agent's) changes. The PR source
            // branch represents intentional work; the upstream side is
            // background drift. Destructive — only run when the caller has
            // explicit authorisation (force=true / story-level AutoMerge).
            this.logger?.LogWarning(
                "MergeUpstreamIntoBranchAsync: clean merge of origin/{Upstream} into {Branch} had conflicts; "
                + "force=true so retrying with -X ours.",
                upstreamBranch, branch);

            var (oursCode, oursOut, oursErr) = await this.RunGitCaptureAsync(
                wtPath, ct,
                "-c", "user.email=forge-merger@local",
                "-c", "user.name=Forge",
                "merge", "--no-edit", "-X", "ours", $"origin/{upstreamBranch}");

            if (oursCode != 0)
            {
                this.logger?.LogError(
                    "MergeUpstreamIntoBranchAsync: -X ours merge also failed for origin/{Upstream} into {Branch}. "
                    + "stdout={StdOut} stderr={StdErr}",
                    upstreamBranch, branch, oursOut, oursErr);
                await this.RunGitCaptureAsync(wtPath, ct, "merge", "--abort");
                return false;
            }

            this.logger?.LogInformation(
                "MergeUpstreamIntoBranchAsync: resolved conflicts with -X ours for origin/{Upstream} into {Branch}.",
                upstreamBranch, branch);
        }

        // Push the merged result.
        await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
        this.logger?.LogInformation(
            "MergeUpstreamIntoBranchAsync: merged origin/{Upstream} into {Branch} and pushed.",
            upstreamBranch, branch);
        return true;
    }

    public async Task<string> EnsureWorktreeAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        // Serialise the ensure step against itself per-branch so two concurrent
        // callers don't race `git worktree add`.
        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await this.EnsureWorktreeAsync(root, wtPath, branch, baseBranch, ct);
        }
        finally
        {
            gate.Release();
        }
        return wtPath;
    }

    public async Task<string> CommitPathInWorktreeAsync(
        RepoRow? repo, string branch, string repoRelativePath, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoRelativePath))
            throw new ArgumentException("Path is required.", nameof(repoRelativePath));
        if (Path.IsPathRooted(repoRelativePath) || repoRelativePath.Contains(".."))
            throw new InvalidOperationException($"Refusing absolute or parent-traversal path '{repoRelativePath}'.");

        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        // Serialise per-branch so concurrent agent commits don't race the
        // git index lock.
        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Worktree must exist before we stage anything.
            await this.EnsureWorktreeAsync(root, wtPath, branch, "main", ct);

            // Stage only the requested path so a sibling agent's mid-edit
            // changes don't get rolled into this commit.
            var diskPath = Path.Combine(wtPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath))
            {
                this.logger?.LogInformation(
                    "CommitPathInWorktreeAsync: file '{Path}' does not exist on disk for branch {Branch}; skipping commit.",
                    repoRelativePath, branch);
                return string.Empty;
            }
            await this.GitAsync(wtPath, ct, "add", "--", repoRelativePath);

            // No-op when the path's content is identical to the branch tip.
            var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "status", "--porcelain", "--", repoRelativePath);
            if (statusCode == 0 && string.IsNullOrWhiteSpace(statusOut))
            {
                return string.Empty;
            }

            await this.GitAsync(wtPath, ct,
                "-c", "user.email=forge-architect@local",
                "-c", "user.name=Forge Architect",
                "commit", "-m", message, "--", repoRelativePath);

            var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "rev-parse", "HEAD");
            return shaCode == 0 ? shaOut.Trim() : string.Empty;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PushBranchAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        if (!Directory.Exists(wtPath))
        {
            this.logger?.LogInformation("PushBranchAsync: worktree '{Path}' does not exist; skipping.", wtPath);
            return;
        }

        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Stage + commit any uncommitted changes so they're included
            // in the push (e.g. a generator interrupted mid-write).
            await this.RunGitCaptureAsync(wtPath, ct, "add", "-A");
            var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "status", "--porcelain");
            if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
            {
                await this.RunGitCaptureAsync(wtPath, ct,
                    "-c", "user.email=forge-architect@local",
                    "-c", "user.name=Forge Architect",
                    "commit", "-m", "plan: stage uncommitted changes before push");
            }

            await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Phase 45 follow-up — list repo-relative paths of files the agent
    /// modified inside <paramref name="worktreePath"/>. Combines two
    /// sources so we don't lose changes regardless of how the agent
    /// chose to "save its work":
    /// <list type="number">
    /// <item><c>git status --porcelain -z</c> — files the agent edited
    /// but didn't commit (the expected/intended path).</item>
    /// <item><c>git diff --name-only origin/{branch}..HEAD</c> — files
    /// touched by commits the agent made itself via its <c>bash</c> /
    /// <c>run_command</c> tool but that haven't been pushed yet.
    /// Observed 2026-05-05 when the engineer prompt mentioned
    /// <c>dotnet build</c> and the agent inferred it was free to run
    /// <c>git add -A &amp;&amp; git commit</c> too. Treating those as
    /// "no changes" was a false negative that halted impl phases.</item>
    /// </list>
    /// Skips deletions (the orchestrator's <see cref="CommitFilesAsync"/>
    /// dict contract can't express them) and path-traversal entries.
    /// Returns an empty list when the worktree is clean AND the local
    /// branch matches origin (or the upstream ref doesn't exist yet).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(
        RepoRow? repo, string worktreePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
            return Array.Empty<string>();

        // Use HashSet so a path that appears in BOTH (e.g. agent committed
        // file X then edited it again post-commit) only shows up once.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        // ── 1) Uncommitted changes ──────────────────────────────────────
        // `-uall` (== `--untracked-files=all`) is critical: without it git
        // collapses untracked files inside a brand-new directory into the
        // directory's own entry (e.g. `?? xraft-core/`). The strategy then
        // calls File.Exists on the directory path, which returns false,
        // and the agent's source files vanish from the dict — observed
        // 2026-05-05 on the from-scratch xraft repo where the engineer
        // wrote a full Cargo workspace but the resulting PR had zero
        // commits because every file lived under a fresh top-level dir.
        var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "status", "--porcelain", "-uall", "-z");
        if (statusCode == 0 && !string.IsNullOrEmpty(statusOut))
        {
            // -z output: NUL-delimited records of `XY path` (renames are
            // `R  newpath\0oldpath`). Walk char-by-char; skip the prefix
            // byte pair + space so we extract just the path.
            var entries = statusOut.Split('\0');
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Length < 4) continue;
                var status = entry.Substring(0, 2);
                var path = entry.Substring(3);
                if (status[0] == 'D' || status[1] == 'D') continue;
                if (status[0] == 'R' || status[0] == 'C')
                {
                    if (i + 1 < entries.Length) i++;
                }
                if (string.IsNullOrEmpty(path)) continue;
                if (path.Contains("..", StringComparison.Ordinal)) continue;
                var norm = path.Replace('\\', '/');
                // Skip well-known build-artifact directories. .gitignore
                // is the proper place to handle this, but a from-scratch
                // workstream often hasn't authored one yet, and we don't
                // want a 100-MB Rust `target/` dir or a `node_modules/`
                // tree showing up on the PR. Conservative list — only
                // dirs whose entire purpose is generated/cached output.
                if (IsBuildArtifactPath(norm)) continue;
                if (paths.Add(norm)) ordered.Add(norm);
            }
        }

        // ── 2) Local commits ahead of origin/{branch} ───────────────────
        // Resolve the branch from the worktree itself (no signature
        // change needed at the IStoryGitOps level). If the worktree is
        // detached or origin/{branch} doesn't exist yet (very-first
        // workstream commit before the initial push), fall back to
        // returning just the uncommitted set.
        var (branchCode, branchOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "rev-parse", "--abbrev-ref", "HEAD");
        if (branchCode != 0 || string.IsNullOrWhiteSpace(branchOut)) return ordered;
        var branch = branchOut.Trim();
        if (branch == "HEAD") return ordered; // detached

        var (refCode, _, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "rev-parse", "--verify", $"refs/remotes/origin/{branch}");
        if (refCode != 0) return ordered; // upstream ref missing — nothing to diff against

        var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "diff", "--name-only", "-z", $"origin/{branch}..HEAD");
        if (diffCode != 0 || string.IsNullOrEmpty(diffOut)) return ordered;

        foreach (var entry in diffOut.Split('\0'))
        {
            if (string.IsNullOrEmpty(entry)) continue;
            if (entry.Contains("..", StringComparison.Ordinal)) continue;
            var norm = entry.Replace('\\', '/');
            if (IsBuildArtifactPath(norm)) continue;
            if (paths.Add(norm)) ordered.Add(norm);
        }

        return ordered;
    }

    /// <summary>
    /// True for paths under directories whose contents are by convention
    /// generated/cached (Rust <c>target/</c>, Node <c>node_modules/</c>,
    /// .NET <c>obj/</c>, Python <c>__pycache__/</c>, etc.). Used as a
    /// fallback when the workstream hasn't authored a <c>.gitignore</c>
    /// yet (typical for from-scratch repos). Conservative — only matches
    /// directories whose entire purpose is throwaway output, never a
    /// directory that might legitimately hold source.
    /// </summary>
    private static bool IsBuildArtifactPath(string repoRelativePath)
    {
        if (string.IsNullOrEmpty(repoRelativePath)) return false;
        // Anchor on path-segment boundaries so e.g. `src/target_resolver.rs`
        // is NOT matched by the `target/` rule. The path is normalised to
        // forward slashes by callers.
        var p = repoRelativePath.Replace('\\', '/');
        ReadOnlySpan<string> dirs =
        [
            "target/",            // Rust
            "node_modules/",      // Node
            "__pycache__/",       // Python bytecode
            ".pytest_cache/",     // Pytest
            ".mypy_cache/",       // mypy
            ".ruff_cache/",       // ruff
            ".tox/",              // tox
            ".venv/",             // Python virtualenv
            "venv/",              // Python virtualenv (alt)
            "obj/",               // .NET intermediate
            ".gradle/",           // Gradle cache
            ".idea/",             // JetBrains
            ".vs/",               // Visual Studio cache
            ".cache/",            // generic
            "dist/",              // common JS bundler output
            ".next/",             // Next.js
            ".nuxt/",             // Nuxt
            ".turbo/",            // Turborepo
            ".parcel-cache/",     // Parcel
        ];
        foreach (var dir in dirs)
        {
            if (p.StartsWith(dir, StringComparison.Ordinal)) return true;
            if (p.Contains("/" + dir, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ResolveCloneRoot(RepoRow? repo)
    {
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot))
            throw new InvalidOperationException("RepoRow.CloneRoot is required for git ops. Configure the repo first.");
        if (!Directory.Exists(repo.CloneRoot))
            throw new InvalidOperationException($"CloneRoot '{repo.CloneRoot}' does not exist on disk. Clone the repo first.");
        return repo.CloneRoot;
    }

    private static string WorktreePath(string cloneRoot, string slug)
    {
        var wtRoot = Path.Combine(cloneRoot, ".worktree");
        Directory.CreateDirectory(wtRoot);
        return Path.Combine(wtRoot, $"spawn-{slug}");
    }

    private static string SanitizeBranchSlug(string branch)
    {
        var sb = new StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }

    private async Task EnsureWorktreeAsync(string cloneRoot, string wtPath, string branch, string baseBranch, CancellationToken ct)
    {
        // Step 1: prune stale worktree records first so a previous abort
        // doesn't leave a registered-but-missing entry that confuses us.
        await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");

        // Step 2: registered + on-disk → reuse. Normalize both sides because
        // `git worktree list` reports forward-slash paths even on Windows
        // while wtPath is backslash-separated.
        var (listCode, listOut, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "list", "--porcelain");
        var wtPathNormalized = wtPath.Replace('\\', '/');
        var listOutNormalized = listOut.Replace('\\', '/');
        if (listCode == 0 && listOutNormalized.Contains(wtPathNormalized, StringComparison.OrdinalIgnoreCase) && Directory.Exists(wtPath))
        {
            // Best-effort fetch — the remote branch may have been deleted
            // after a PR merge. The local worktree still has its commits.
            await this.RunGitCaptureAsync(wtPath, ct, "fetch", "origin", branch);
            var (code, _, _) = await this.RunGitCaptureAsync(wtPath, ct, "checkout", branch);
            if (code != 0)
            {
                await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
            }
            return;
        }

        // Step 3: directory exists on disk but isn't a registered worktree
        // (orphaned from a prior aborted run). Force-remove via git first
        // so internal refs are cleaned, then nuke the directory.
        if (Directory.Exists(wtPath))
        {
            this.logger?.LogInformation("Removing orphaned worktree directory at {Path}", wtPath);
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "remove", "--force", wtPath);
            if (Directory.Exists(wtPath))
            {
                try { Directory.Delete(wtPath, recursive: true); }
                catch (IOException ex)
                {
                    this.logger?.LogWarning(ex, "Could not delete orphaned worktree dir {Path}", wtPath);
                }
            }
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");
        }

        // Step 4: now add the worktree fresh.
        var (existsCode, existsOut, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "ls-remote", "--heads", "origin", branch);
        if (existsCode == 0 && !string.IsNullOrWhiteSpace(existsOut))
        {
            // Branch already on origin → check it out into the worktree.
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", branch);
            await this.GitAsync(cloneRoot, ct, "worktree", "add", wtPath, $"origin/{branch}");
            await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
        }
        else
        {
            // New branch off baseBranch.
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", baseBranch);
            await this.GitAsync(cloneRoot, ct, "worktree", "add", "-b", branch, wtPath, $"origin/{baseBranch}");
        }
    }

    /// <summary>
    /// Push <paramref name="branch"/> to <c>origin</c>; on a non-fast-forward
    /// rejection, fetch + rebase the local commits onto <c>origin/{branch}</c>
    /// and retry the push once. Recovers the common case where a prior
    /// orchestrator run advanced the remote branch but the dedicated
    /// worktree's local ref didn't catch up — without this, the spawn halts
    /// and the operator has to click Retry, which fails with the same error.
    /// </summary>
    private async Task PushWithAutoRecoverAsync(string wtPath, string branch, CancellationToken ct)
    {
        var (code, stdout, stderr) = await this.RunGitCaptureAsync(wtPath, ct, "push", "origin", branch);
        if (code == 0) return;

        var combined = (stdout + "\n" + stderr).ToLowerInvariant();
        var isNonFastForward = combined.Contains("non-fast-forward")
            || combined.Contains("[rejected]")
            || combined.Contains("fetch first");
        if (!isNonFastForward)
        {
            throw new InvalidOperationException(
                $"git push origin {branch} failed (exit {code}) in '{wtPath}'.\nstdout: {stdout}\nstderr: {stderr}");
        }

        this.logger?.LogWarning(
            "Push to origin/{Branch} rejected as non-fast-forward; fetching + rebasing onto remote and retrying once.", branch);

        await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
        var (rebaseCode, rebaseOut, rebaseErr) = await this.RunGitCaptureAsync(
            wtPath, ct,
            "-c", "user.email=forge-architect@local",
            "-c", "user.name=Forge Architect",
            "rebase", $"origin/{branch}");
        if (rebaseCode != 0)
        {
            // Real conflict — abort so the worktree is left clean, then surface
            // the original push error with the rebase context so the operator
            // can resolve manually.
            await this.RunGitCaptureAsync(wtPath, ct, "rebase", "--abort");
            throw new InvalidOperationException(
                $"git push origin {branch} failed (exit {code}) in '{wtPath}' and auto-rebase onto origin/{branch} also failed.\n"
                + $"push stderr: {stderr}\nrebase stdout: {rebaseOut}\nrebase stderr: {rebaseErr}");
        }

        var (retryCode, retryOut, retryErr) = await this.RunGitCaptureAsync(wtPath, ct, "push", "origin", branch);
        if (retryCode != 0)
        {
            throw new InvalidOperationException(
                $"git push origin {branch} failed (exit {retryCode}) in '{wtPath}' after auto-rebase recover.\n"
                + $"stdout: {retryOut}\nstderr: {retryErr}");
        }

        this.logger?.LogInformation(
            "Push to origin/{Branch} succeeded after auto-rebase recover.", branch);
    }

    /// <summary>Run git, throwing on non-zero exit. Use <see cref="RunGitCaptureAsync"/> when you want to inspect the result.</summary>
    private async Task GitAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var (code, stdout, stderr) = await this.RunGitCaptureAsync(cwd, ct, args);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {code}) in '{cwd}'.\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureAsync(string cwd, CancellationToken ct, params string[] args)
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

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        this.logger?.LogDebug("git {Args} → exit={Exit} cwd={Cwd}", string.Join(' ', args), p.ExitCode, cwd);
        return (p.ExitCode, stdout, stderr);
    }
}
