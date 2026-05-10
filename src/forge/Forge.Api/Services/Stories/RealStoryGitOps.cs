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

        // Phase 49 follow-up — push the new branch ref to origin WITHOUT
        // creating a worktree. The legacy code materialised a worktree
        // here just so it could `git push -u` from it, but that produced
        // 100+ char `.worktree/{slug}` directories for EVERY workstream
        // the claim sweep visited (observed 2026-05-10 on
        // failover-cluster:XRAFT: 20 stale worktrees for unstarted
        // stages). The lazy `EnsureWorktreeAsync` inside
        // `ProduceWorkstreamChangesAsync` still creates a worktree when
        // an agent actually needs one. Pushing
        // `refs/remotes/origin/{base}:refs/heads/{branch}` advances the
        // remote-tracking ref directly — no working copy required.
        await this.GitAsync(root, ct, "push", "origin",
            $"refs/remotes/origin/{baseBranch}:refs/heads/{branch}");

        // After the push, fetch back so the local remote-tracking ref is
        // populated. Downstream callers (RealStoryGitOps.ReadFileAsync,
        // EnsureWorktreeAsync's existing-on-origin branch) read
        // `refs/remotes/origin/{branch}` and would otherwise see "not
        // found" until the next periodic fetch.
        await this.GitAsync(root, ct, "fetch", "origin", branch);
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

        // Phase 49 follow-up — find the EXISTING worktree on this branch
        // (regardless of which directory layout it lives under: the new
        // `stage-{n.n}-{slug}` short name, the legacy `spawn-{slug}`, or
        // any operator-customised path). The merge MUST happen in the
        // same working directory the agent is using, otherwise the
        // merge commit lands in a sibling worktree and the agent's
        // tree stays at the pre-merge state (observed 2026-05-10 on
        // failover-cluster:XRAFT stage 1.2: behind base by 1, agent
        // saw README + docs only). Only fall back to creating a new
        // worktree at the legacy path when NO worktree is registered.
        var existingWtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        var wtPath = !string.IsNullOrEmpty(existingWtPath)
            ? existingWtPath
            : WorktreePath(root, SanitizeBranchSlug(branch));

        // Ensure the worktree is up to date.
        await this.EnsureWorktreeAsync(root, wtPath, branch, branch, ct);
        await this.GitAsync(wtPath, ct, "fetch", "origin", upstreamBranch);

        var (existsCode, existsOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "ls-remote", "--heads", "origin", branch);
        var branchOnOrigin = existsCode == 0 && !string.IsNullOrWhiteSpace(existsOut);

        if (branchOnOrigin)
        {
            await this.GitAsync(wtPath, ct, "fetch", "origin", branch);

            // Phase 47b — diagnostic breadcrumb for the worktree-wipe
            // investigation. The engineer agent's uncommitted edits
            // shouldn't be present here in normal operation: this method
            // is reached AFTER CommitFilesAsync pushed any pending work
            // to origin. If `git status` shows uncommitted changes at
            // this point, something committed-but-not-pushed will be
            // lost, OR the agent left edits on disk that the orchestrator
            // didn't capture into the file dict (a bug). Capture the
            // pre-reset state so we can correlate against the
            // workstream's deliverable history.
            var preReset = await this.CaptureWorktreeStateAsync(wtPath, ct);
            var (orgShaCode, orgShaOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "rev-parse", $"origin/{branch}");
            var originSha = orgShaCode == 0 ? orgShaOut.Trim() : "(unknown)";
            if (preReset.UncommittedCount > 0 || !string.Equals(preReset.HeadSha, originSha, StringComparison.OrdinalIgnoreCase))
            {
                this.logger?.LogWarning(
                    "WORKTREE_FORCE_RESET merge-upstream: branch={Branch} wtPath={WtPath} "
                    + "preResetHead={Head} originSha={OriginSha} uncommittedFileCount={UncommittedCount} "
                    + "sampleUncommittedPaths={SamplePaths} "
                    + "(about to `git reset --hard origin/{Branch}` before merging origin/{Upstream}; "
                    + "reflog can recover within gc-grace).",
                    branch, wtPath, preReset.HeadSha, originSha,
                    preReset.UncommittedCount, string.Join("|", preReset.SamplePaths),
                    branch, upstreamBranch);
            }

            await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{branch}");
        }
        else
        {
            // First-time merge into a never-pushed branch — push local commits
            // so origin/{branch} exists; PushWithAutoRecover at the end then
            // has a remote to push against without inventing one.
            this.logger?.LogInformation(
                "MergeUpstreamIntoBranchAsync: origin/{Branch} not on origin; pushing local commits first.",
                branch);
            await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
        }

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
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default, string? worktreeSlug = null)
    {
        var root = ResolveCloneRoot(repo);
        // Phase 49 — when the caller passes a precomputed slug (engineer
        // workstreams use WorkstreamWorktreeSlug.Compute), root the
        // worktree directory at it directly. Otherwise fall back to the
        // legacy `spawn-{branch-slug}` shape so plan-doc / QA paths
        // (which already have short names) don't churn.
        var wtPath = !string.IsNullOrWhiteSpace(worktreeSlug)
            ? Path.Combine(root, ".worktree", worktreeSlug)
            : WorktreePath(root, SanitizeBranchSlug(branch));

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

    /// <summary>
    /// Phase 49 follow-up — locate and remove the worktree currently
    /// associated with <paramref name="branch"/>, regardless of which
    /// directory layout (legacy <c>spawn-{slug}</c> vs. new
    /// <c>stage-{n.n}-{slug}</c>) it lives under. Best-effort: never
    /// throws, just logs. The branch ref itself is left intact (an
    /// already-merged plan / workstream branch is still useful for
    /// audit). Used to free disk after a spawn's plan PR merges (plan
    /// worktree becomes stale) and after each engineer workstream's PR
    /// merges (engineer worktree becomes stale).
    /// </summary>
    public async Task RemoveWorktreeAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branch)) return;
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return;
        if (!Directory.Exists(repo.CloneRoot)) return;

        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await this.RunGitCaptureAsync(repo.CloneRoot, ct, "worktree", "prune");
            var existing = await this.TryFindExistingWorktreeAsync(repo.CloneRoot, branch, ct);
            if (string.IsNullOrEmpty(existing))
            {
                this.logger?.LogDebug(
                    "RemoveWorktreeAsync: no worktree registered for branch {Branch} on repo {Repo}; nothing to clean.",
                    branch, repo.Id);
                return;
            }

            this.logger?.LogInformation(
                "RemoveWorktreeAsync: removing worktree {Path} for branch {Branch}.", existing, branch);
            await this.RunGitCaptureAsync(repo.CloneRoot, ct, "worktree", "remove", "--force", existing);
            if (Directory.Exists(existing))
            {
                try { Directory.Delete(existing, recursive: true); }
                catch (IOException ex)
                {
                    this.logger?.LogWarning(ex,
                        "RemoveWorktreeAsync: could not delete dir {Path} after `git worktree remove`; will be pruned on next claim.",
                        existing);
                }
            }
            await this.RunGitCaptureAsync(repo.CloneRoot, ct, "worktree", "prune");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "RemoveWorktreeAsync: best-effort cleanup failed for branch {Branch}; ignoring.", branch);
        }
        finally
        {
            gate.Release();
        }
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
    /// <summary>
    /// Combine two views of "what the agent contributed":
    /// <list type="number">
    /// <item><c>git status --porcelain -z</c> — files the agent edited
    /// but didn't commit (the expected/intended path).</item>
    /// <item><c>git diff --name-only origin/{ref}..HEAD</c> — files
    /// touched by commits the agent made itself via its <c>bash</c> /
    /// <c>run_command</c> tool but that haven't been pushed yet.
    /// Observed 2026-05-05 when the engineer prompt mentioned
    /// <c>dotnet build</c> and the agent inferred it was free to run
    /// <c>git add -A &amp;&amp; git commit</c> too. Treating those as
    /// "no changes" was a false negative that halted impl phases.</item>
    /// </list>
    ///
    /// <para>Phase 49 follow-up — the reference for source #2 is now
    /// caller-controlled. When <paramref name="baseBranch"/> is
    /// non-empty, diff against <c>origin/{baseBranch}</c> (the merge
    /// target — gives the cumulative scope of the PR). When null/empty,
    /// fall back to <c>origin/{HEAD branch}</c> (the legacy "delta since
    /// last published state" frame, suitable for per-iter no-progress
    /// detection where we want the counter to reset on uncommitted edits
    /// each iter rather than latching to "always non-zero" after the
    /// agent's first self-commit). The post-pass commit path passes
    /// <c>spawn.BaseBranch</c> so the workstream's contribution is
    /// computed against the PR target, not against a stale workstream
    /// remote (observed 2026-05-10 on failover-cluster:XRAFT, where
    /// <c>origin/{ws-branch}</c> was pinned to the plan PR base instead
    /// of <c>origin/main</c>).</para>
    ///
    /// <para>Skips deletions (the orchestrator's <see cref="CommitFilesAsync"/>
    /// dict contract can't express them) and path-traversal entries.
    /// Returns an empty list when the worktree is clean AND the
    /// reference ref doesn't exist or there is no committed delta.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(
        RepoRow? repo, string worktreePath, string? baseBranch = null, CancellationToken ct = default)
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

        // ── 2) Local commits ahead of the reference ref ─────────────────
        // Phase 49 — when baseBranch is supplied, diff against the merge
        // target (origin/{baseBranch}). This is the conceptually correct
        // frame for the post-pass commit decision: a workstream's
        // contribution is what would land in the PR, which is HEAD's
        // delta from the target it merges into — NOT the workstream's
        // own remote tracking branch (which may be at an unrelated tip
        // or missing entirely for a never-pushed branch). When
        // baseBranch is null/empty, fall back to origin/{HEAD branch}
        // for per-iter no-progress detection where we want the counter
        // to track delta-since-last-publish, not cumulative-since-base.
        string refToDiffAgainst;
        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            refToDiffAgainst = $"origin/{baseBranch}";
        }
        else
        {
            var (branchCode, branchOut, _) = await this.RunGitCaptureAsync(
                worktreePath, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (branchCode != 0 || string.IsNullOrWhiteSpace(branchOut)) return ordered;
            var branch = branchOut.Trim();
            if (branch == "HEAD") return ordered; // detached
            refToDiffAgainst = $"origin/{branch}";
        }

        var (refCode, _, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "rev-parse", "--verify", $"refs/remotes/{refToDiffAgainst}");
        if (refCode != 0) return ordered; // reference ref missing — nothing to diff against

        var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "diff", "--name-only", "-z", $"{refToDiffAgainst}..HEAD");
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

        // Phase 49 follow-up — the branch may be registered to a DIFFERENT
        // worktree path under the legacy `spawn-{branch-slug}` layout
        // (typically 100+ chars). Detect that and unregister before we try
        // to add the short-name worktree, otherwise `git worktree add` aborts
        // with "branch already checked out at {oldPath}". The branch ref
        // (and any local commits the agent made) survives the worktree
        // removal — only the working directory is torn down.
        var existingOnBranch = await this.TryFindExistingWorktreeAsync(cloneRoot, branch, ct);
        if (!string.IsNullOrEmpty(existingOnBranch))
        {
            var wantedNormalized = wtPath.Replace('\\', '/');
            var existingNormalized = existingOnBranch.Replace('\\', '/');
            if (!string.Equals(wantedNormalized, existingNormalized, StringComparison.OrdinalIgnoreCase))
            {
                this.logger?.LogInformation(
                    "Phase 49 worktree migration: branch {Branch} is registered at legacy path {Old}; "
                    + "removing so it can be re-added at the short path {New}.",
                    branch, existingOnBranch, wtPath);
                await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "remove", "--force", existingOnBranch);
                if (Directory.Exists(existingOnBranch))
                {
                    try { Directory.Delete(existingOnBranch, recursive: true); }
                    catch (IOException ex)
                    {
                        this.logger?.LogWarning(ex,
                            "Could not delete legacy worktree dir {Path}; git worktree remove may have cleared the registration only.",
                            existingOnBranch);
                    }
                }
                await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");
            }
        }

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
            var (code, coStdout, coStderr) = await this.RunGitCaptureAsync(wtPath, ct, "checkout", branch);
            if (code != 0)
            {
                // Phase 47b — diagnostic breadcrumb for the worktree-wipe
                // investigation. Production observed (2026-05-09) the
                // engineer agent calling `edit` 65× with confirmed tool
                // results, yet `git status` empty between iter spans —
                // suggesting one of two destructive paths discarded the
                // edits. This fallback is one of them: `git checkout -B
                // {branch} origin/{branch}` is a hard reset of the branch
                // ref AND the working tree to origin's HEAD. Anything
                // uncommitted that conflicts with origin's tree is wiped.
                // Capture the pre-reset state (uncommitted file count,
                // sample paths, HEAD SHA, original checkout error) so we
                // can correlate against the workstream's deliverable
                // history when investigating "agent edited but no diff".
                var preReset = await this.CaptureWorktreeStateAsync(wtPath, ct);
                this.logger?.LogWarning(
                    "WORKTREE_FORCE_RESET ensure-fallback: branch={Branch} wtPath={WtPath} "
                    + "checkoutExit={CheckoutCode} checkoutStdout={CheckoutStdout} checkoutStderr={CheckoutStderr} "
                    + "preResetHead={Head} uncommittedFileCount={UncommittedCount} sampleUncommittedPaths={SamplePaths} "
                    + "(falling back to destructive `git checkout -B {Branch} origin/{Branch}` — ANY uncommitted changes that "
                    + "conflict with origin/{Branch} will be discarded; reflog can recover within gc-grace).",
                    branch, wtPath, code, Trim(coStdout, 200), Trim(coStderr, 200),
                    preReset.HeadSha, preReset.UncommittedCount, string.Join("|", preReset.SamplePaths),
                    branch, branch, branch);

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
            // New branch off baseBranch — but a prior drive may have already
            // created the local branch ref (step 3 cleans the worktree dir
            // and registration, not the branch). Probe with rev-parse and
            // attach the worktree to the existing ref instead of failing
            // with "fatal: a branch named '…' already exists". Mirrors the
            // pattern at CrewQueueWorker.cs:1006-1011.
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", baseBranch);
            var (localCode, _, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "rev-parse", "--verify", $"refs/heads/{branch}");
            if (localCode == 0)
            {
                await this.GitAsync(cloneRoot, ct, "worktree", "add", wtPath, branch);
            }
            else
            {
                await this.GitAsync(cloneRoot, ct, "worktree", "add", "-b", branch, wtPath, $"origin/{baseBranch}");
            }
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

    /// <summary>
    /// Phase 47b — snapshot a worktree's pre-destructive-op state for the
    /// WORKTREE_FORCE_RESET diagnostic breadcrumb. Captures HEAD SHA + the
    /// uncommitted-file count + a small sample of the actual paths so the
    /// operator can correlate against the workstream's deliverable history
    /// when investigating "agent edited but no diff".
    /// </summary>
    private async Task<(string HeadSha, int UncommittedCount, IReadOnlyList<string> SamplePaths)>
        CaptureWorktreeStateAsync(string wtPath, CancellationToken ct)
    {
        string headSha = "(unknown)";
        try
        {
            var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "rev-parse", "HEAD");
            if (shaCode == 0) headSha = shaOut.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var samples = new List<string>(8);
        var count = 0;
        try
        {
            var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "status", "--porcelain", "-uall");
            if (statusCode == 0 && !string.IsNullOrEmpty(statusOut))
            {
                foreach (var line in statusOut.Split('\n'))
                {
                    if (line.Length < 4) continue;
                    count++;
                    if (samples.Count < 8)
                    {
                        var path = line.Substring(3).Trim();
                        if (!string.IsNullOrEmpty(path)) samples.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return (headSha, count, samples);
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value.Trim() : value.Substring(0, max).Trim() + "…";
    }
}
