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
/// dedicated worktree under <c>{cloneRoot}/.worktree/{slug}/</c> where
/// <c>{slug}</c> is supplied by the caller. The canonical slug formulas
/// live in <see cref="PlanDocStateHelpers"/> (plan docs / QA / QA-steps)
/// and <see cref="WorkstreamWorktreeSlug"/> (engineer workstreams).
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
        //
        // Phase 49 follow-up² — be defensive about the push outcome.
        // The ls-remote check above is a HINT, not authoritative: it
        // can return empty for transient network failures, auth retries,
        // or a moment when the remote was being updated by a concurrent
        // operation (e.g. PR #8 merging at the same instant). When that
        // happens we mistakenly think the branch is missing and try to
        // bootstrap it from `origin/main` — which then fails as
        // `non-fast-forward` because the branch DOES exist and has
        // commits ahead of main. Pre-fix that exception escaped to
        // `TickOneSpawnAsync` and set `spawn.LastError =
        // "implement failed: git push origin … (non-fast-forward)"`,
        // halting the entire spawn on a non-issue (the branch was
        // exactly where we wanted it, our existence check just blinked).
        // Now: if the push fails AND the failure reads like "branch
        // already exists" (non-fast-forward / rejected), assume the
        // branch is fine, fetch it, and return cleanly.
        var (pushCode, _, pushErr) = await this.RunGitCaptureAsync(root, ct,
            "push", "origin", $"refs/remotes/origin/{baseBranch}:refs/heads/{branch}");
        if (pushCode != 0)
        {
            var stderr = pushErr ?? string.Empty;
            var rejectedAsExisting =
                stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("[rejected]", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Updates were rejected", StringComparison.OrdinalIgnoreCase);
            if (rejectedAsExisting)
            {
                this.logger?.LogInformation(
                    "EnsureBranchAsync: push origin/{Base} → {Branch} rejected ({Why}); branch already exists, recovering with fetch.",
                    baseBranch, branch,
                    stderr.Length > 200 ? stderr[..200] + "…" : stderr);
                await this.GitAsync(root, ct, "fetch", "origin", branch);
                return;
            }
            throw new InvalidOperationException(
                $"git push origin refs/remotes/origin/{baseBranch}:refs/heads/{branch} "
                + $"failed (exit {pushCode}) in '{root}'. stderr: {stderr}");
        }

        // After the push, fetch back so the local remote-tracking ref is
        // populated. Downstream callers (RealStoryGitOps.ReadFileAsync,
        // EnsureWorktreeAsync's existing-on-origin branch) read
        // `refs/remotes/origin/{branch}` and would otherwise see "not
        // found" until the next periodic fetch.
        await this.GitAsync(root, ct, "fetch", "origin", branch);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 50 — implementation. Force-pushes the base tip to
    /// <c>refs/heads/{branch}</c> on origin so any orphan tip is replaced
    /// in one atomic operation. Then tears down any local mirror — the
    /// local branch ref and whichever worktree path was registered against
    /// the branch — so downstream callers don't reuse a stale checkout.
    /// Idempotent on a non-orphan branch (force-pushes an identical tree).
    /// </remarks>
    public async Task EnsureBranchFreshAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Step 1 — refresh local view of base so the force-push reads the
        // up-to-date tip rather than a stale fetched copy.
        await this.GitAsync(root, ct, "fetch", "origin", baseBranch);

        // Step 2 — atomic force-push: replaces the orphan tip OR creates
        // the branch fresh when missing. Runs under the operator's
        // current auth context (matches the caller's POST /spawn), so
        // any auth fix the operator applied since the prior failed
        // delete takes effect here.
        await this.GitAsync(root, ct, "push", "origin", "--force",
            $"refs/remotes/origin/{baseBranch}:refs/heads/{branch}");

        // Step 3 — drop any local mirror of the branch + its worktree.
        // EnsureWorktreeAsync (called later by the orchestrator) is
        // smart enough to recreate from the now-fresh origin tip; the
        // problem we're guarding against is its reuse of a directory
        // that still holds the OLD spawn's files. We ask git directly for
        // the worktree path registered against the branch (regardless of
        // which slug it lives under) so this stays correct even if the
        // operator hand-customised the directory layout.
        var registeredWtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (!string.IsNullOrEmpty(registeredWtPath) && Directory.Exists(registeredWtPath)
            && !IsSamePath(registeredWtPath, root) && !IsMainWorktreeDir(registeredWtPath))
        {
            await this.RunGitCaptureAsync(root, ct, "worktree", "remove", "--force", registeredWtPath);
            if (Directory.Exists(registeredWtPath))
            {
                try { Directory.Delete(registeredWtPath, recursive: true); }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex,
                        "EnsureBranchFreshAsync: could not raw-delete worktree {Path}; pruning git metadata only.",
                        registeredWtPath);
                }
            }
            await this.RunGitCaptureAsync(root, ct, "worktree", "prune");
        }

        // Local branch ref — best-effort. `git branch -D` refuses if the
        // ref is checked out somewhere (already handled above) or doesn't
        // exist locally (typical when the orphan only lived on origin).
        await this.RunGitCaptureAsync(root, ct, "branch", "-D", branch);

        // Step 4 — populate local origin/{branch} tracking ref so
        // downstream worktree-add sees the fresh tip.
        await this.GitAsync(root, ct, "fetch", "origin", branch);
    }

    public async Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, string worktreeSlug,
        IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worktreeSlug))
        {
            throw new ArgumentException(
                $"worktreeSlug is required (branch={branch}).", nameof(worktreeSlug));
        }
        var root = ResolveCloneRoot(repo);
        var wtPath = WorktreePath(root, worktreeSlug);

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
        await this.ScrubForgeScratchFromIndexAsync(wtPath, ct);

        // Delete .forge/ from disk so a subsequent `git add -A` (e.g., in
        // MergeUpstreamIntoBranchAsync) doesn't re-stage the files.
        var forgeDir = Path.Combine(wtPath, ".forge");
        if (Directory.Exists(forgeDir))
        {
            try { Directory.Delete(forgeDir, recursive: true); }
            catch { /* best-effort — Windows file handles may hold the dir */ }
        }

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
        //
        // Phase 51 Fix 6b — track whether MergeAsync materialized the
        // worktree itself (`createdHere=true`) so the post-sync cleanup
        // can tear down the merge-{base} fallback without nuking a
        // worktree some other code path (EnsureBranchAsync, plan-doc
        // workspace) is still using.
        var wtPath = await this.TryFindExistingWorktreeAsync(root, baseBranch, ct);
        var createdHere = false;
        if (wtPath is null)
        {
            var sanitized = SanitizeBranchSlug($"merge-{baseBranch}");
            wtPath = WorktreePath(root, sanitized);
            await this.EnsureWorktreeAsync(root, wtPath, baseBranch, baseBranch, ct);
            createdHere = true;
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

        // Phase 51 Fix 6b — clean up the `spawn-merge-{base}` fallback
        // worktree we materialized at the top. The local sync is done
        // (origin is the source of truth) and there's no other reason
        // to keep the working tree around — leaving it pollutes
        // `.worktree/` with dirs that accumulate per merge cycle and
        // were observed on 2026-05-12 as
        // `spawn-merge-feature-memory-pressure` etc. Only remove when
        // WE created it; a pre-existing worktree may still be in use by
        // an EnsureBranchAsync flow or operator inspection.
        if (createdHere && !string.IsNullOrEmpty(wtPath))
        {
            try
            {
                await this.RunGitCaptureAsync(root, ct, "worktree", "remove", "--force", wtPath);
                if (Directory.Exists(wtPath))
                {
                    try { Directory.Delete(wtPath, recursive: true); }
                    catch (IOException ex)
                    {
                        this.logger?.LogDebug(ex,
                            "MergeAsync: could not raw-delete worktree dir {Path} after `git worktree remove`; will be pruned on next claim.",
                            wtPath);
                    }
                }
                await this.RunGitCaptureAsync(root, ct, "worktree", "prune");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogDebug(ex,
                    "MergeAsync: best-effort worktree cleanup failed for {Path}; ignoring.",
                    wtPath);
            }
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
    /// Phase 55 — normalize a filesystem path for case-insensitive equality
    /// comparison on Windows. Returns the absolute, trimmed path with
    /// platform-native separators. Used by <see cref="IsSamePath"/> to
    /// detect when a worktree path equals the cloneRoot (and must NEVER
    /// be deleted).
    /// </summary>
    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Phase 55 — case-insensitive equality on normalized absolute paths.
    /// `git worktree list --porcelain` emits forward-slashes on Windows
    /// while callers pass back native separators; both normalize to the
    /// same canonical form.
    /// </summary>
    private static bool IsSamePath(string a, string b) =>
        string.Equals(NormalizeAbsolutePath(a), NormalizeAbsolutePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Phase 55 — structural detection of a main worktree. A linked
    /// worktree's `.git` entry is a regular file containing
    /// <c>gitdir: .../worktrees/{slug}</c>; the main worktree's `.git`
    /// is a real directory holding the actual git metadata. This is the
    /// symlink/junction-resilient backstop for cases where
    /// <see cref="IsSamePath"/>'s string comparison may not match
    /// (e.g., the cloneRoot is reached via a junction whose canonical
    /// form differs from the path git stored at registration time).
    /// </summary>
    private static bool IsMainWorktreeDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var dotGit = Path.Combine(path, ".git");
            return Directory.Exists(dotGit);
        }
        catch
        {
            return false;
        }
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

        // Find the EXISTING worktree on this branch regardless of which
        // canonical slug it lives under (stage-…, plan-…, qa-…). The
        // merge MUST happen in the same working directory the agent is
        // using, otherwise the merge commit lands in a sibling worktree
        // and the agent's tree stays at the pre-merge state (observed
        // 2026-05-10 on failover-cluster:XRAFT stage 1.2: behind base
        // by 1, agent saw README + docs only). Only fall back to creating
        // a new worktree at a sanitized-branch slug when NO worktree is
        // registered — degenerate case, but keeps the op idempotent.
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

        // 2026-05-13 — early ancestor check, before any destructive
        // operation. The function's documented contract (see
        // CrewStoryArtifactStrategy.cs:1664 "MergeUpstreamIntoBranchAsync
        // is idempotent (no-op when already up to date)") only became
        // true after this reorder: previously the `git reset --hard
        // origin/{branch}` below ran first and only then noticed the
        // merge was a no-op, vaporising local commits whose push hadn't
        // landed yet. With per-iter commit+push in CrewQueueWorker the
        // common case is "pushed, ancestor passes, nothing to do" —
        // short-circuit here so the reset is reached only when a real
        // merge is needed.
        var (mbCodeEarly, _, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "merge-base", "--is-ancestor", $"origin/{upstreamBranch}", "HEAD");
        if (mbCodeEarly == 0)
        {
            this.logger?.LogInformation(
                "MergeUpstreamIntoBranchAsync: origin/{Upstream} already ancestor of {Branch}, skipping (force={Force}, no reset).",
                upstreamBranch, branch, force);
            return true;
        }

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
            // Phase 50 follow-up — origin/{upstream} is already an ancestor
            // of HEAD, so the source branch already carries every commit
            // from upstream. Scrub any tracked .forge/ scratch files before
            // returning so the branch is clean for the provider merge.
            await this.ScrubForgeScratchFromTreeAsync(wtPath, ct);

            // If scrub created a commit, push it.
            var (pushCheckCode, pushCheckOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "rev-list", "--count", $"origin/{branch}..HEAD");
            if (pushCheckCode == 0 && int.TryParse(pushCheckOut.Trim(), out var ahead) && ahead > 0)
            {
                await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
            }
            //
            // We INTENTIONALLY no longer push an empty commit on the
            // `force=true` path here, even though we used to. The
            // historical justification ("nudge provider mergeable past a
            // stale cache") backfired on GitHub: every empty-commit push
            // invalidates GitHub's `mergeable` flag to UNKNOWN while it
            // recomputes in the background. AutoMergeWatcher would push
            // the nudge, race GitHub's recompute by ~1s, fire PUT
            // /merge → GitHub returns 405 "Pull Request is not
            // mergeable", the watcher logs the failure, then ticks again
            // 15s later and re-invalidates the cache before GitHub
            // finishes the previous recompute. Observed in production on
            // smartpcr/qq#1 (2026-05-12) — plan PR sat stranded for 1h+
            // with all four plan docs at verdict=pass; the AutoMergeWatcher
            // was self-DDOS-ing GitHub's mergeable computation. The
            // operator's PR-not-mergeable diagnosis showed both
            // `mergeable=UNKNOWN` and `mergeStateStatus=UNKNOWN`, with
            // every empty-commit log line in the server stdout matched
            // by a 405 within 1s.
            //
            // Trade-off accepted: in the rare case where GitHub or ADO
            // genuinely has a stale `mergeable` from before a real push,
            // the first MergePullRequestAsync call may 405. AutoMergeWatcher
            // loops on a 15s tick (no more empty-commit invalidations now),
            // GitHub's natural recompute completes within seconds, and the
            // next tick succeeds. Net: 15-30s slower in the rare stale-
            // cache case; never strands in the (now-removed) self-DDOS loop.
            this.logger?.LogInformation(
                "MergeUpstreamIntoBranchAsync: origin/{Upstream} already ancestor of {Branch}, skipping (force={Force}).",
                upstreamBranch, branch, force);
            return true;
        }

        // Pre-merge scrub: remove .forge/ from the current tree so it
        // can't create add/add or modify/delete tree conflicts during the
        // merge. Agents may commit .forge/iter-notes.md despite being told
        // not to; a previously-merged workstream PR may also carry .forge/
        // files into the base branch. Removing from "ours" before the
        // merge prevents the conflict entirely.
        await this.ScrubForgeScratchFromTreeAsync(wtPath, ct);

        // Attempt a normal merge from upstream.
        var (mergeCode, mergeOut, mergeErr) = await this.RunGitCaptureAsync(
            wtPath, ct,
            "-c", "user.email=forge-merger@local",
            "-c", "user.name=Forge",
            "merge", "--no-edit", $"origin/{upstreamBranch}");

        if (mergeCode != 0)
        {
            // Before aborting, check if the ONLY conflicts are .forge/
            // scratch files. If so, resolve them by deletion and complete
            // the merge — no need for -X ours on real source files.
            if (await this.TryResolveForgeConflictsAsync(wtPath, ct))
            {
                // All conflicts were under .forge/; complete the merge.
                var (continueCode, _, continueErr) = await this.RunGitCaptureAsync(
                    wtPath, ct,
                    "-c", "user.email=forge-merger@local",
                    "-c", "user.name=Forge",
                    "commit", "--no-edit");
                if (continueCode == 0)
                {
                    this.logger?.LogInformation(
                        "MergeUpstreamIntoBranchAsync: resolved .forge/-only conflicts for origin/{Upstream} into {Branch}.",
                        upstreamBranch, branch);
                    goto MergeSucceeded;
                }
                // commit failed — fall through to abort + retry.
                this.logger?.LogWarning(
                    "MergeUpstreamIntoBranchAsync: .forge/ conflict resolution succeeded but commit failed for {Branch}. stderr={Err}",
                    branch, continueErr);
            }

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
                // -X ours failed too — check if .forge/ conflicts are the
                // blocker. Resolve them and try to complete the merge.
                if (await this.TryResolveForgeConflictsAsync(wtPath, ct))
                {
                    var (c2, _, e2) = await this.RunGitCaptureAsync(
                        wtPath, ct,
                        "-c", "user.email=forge-merger@local",
                        "-c", "user.name=Forge",
                        "commit", "--no-edit");
                    if (c2 == 0)
                    {
                        this.logger?.LogInformation(
                            "MergeUpstreamIntoBranchAsync: resolved .forge/ tree conflicts after -X ours for {Branch}.",
                            branch);
                        goto MergeSucceeded;
                    }
                }
                // Still unresolvable — abort and surface to caller.
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

    MergeSucceeded:
        // Post-merge scrub: the upstream merge may have re-introduced
        // .forge/ files from the base branch. Remove them before pushing.
        await this.ScrubForgeScratchFromTreeAsync(wtPath, ct);

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
        if (string.IsNullOrWhiteSpace(worktreeSlug))
        {
            throw new ArgumentException(
                $"worktreeSlug is required (branch={branch}). Callers must supply the canonical slug from "
                + "PlanDocStateHelpers (plan / QA) or WorkstreamWorktreeSlug (engineer workstreams).",
                nameof(worktreeSlug));
        }

        var root = ResolveCloneRoot(repo);
        var wtPath = WorktreePath(root, worktreeSlug);

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
    /// Locate and remove the worktree currently associated with
    /// <paramref name="branch"/>, regardless of which canonical slug it
    /// lives under (<c>stage-{n.n}-{title}</c>, <c>plan-{key}</c>,
    /// <c>qa-{storyId}</c>, etc.). Best-effort: never throws, just logs.
    /// The branch ref itself is left intact. Used to free disk after a
    /// spawn's plan PR merges and after each engineer workstream's PR
    /// merges.
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

            // Phase 55 — never delete the cloneRoot itself. Two-pronged
            // detection because `TryFindExistingWorktreeAsync` returns
            // whatever path `git worktree list --porcelain` emitted, which
            // can differ from `repo.CloneRoot` by trailing slash, alt
            // separators, case, or symlink/junction. Belt: normalized path
            // equality. Suspenders: `.git`-is-a-directory structural check
            // (linked worktrees have `.git` as a pointer FILE; main
            // worktrees have it as a real DIR). Either signal means "main
            // worktree" — and that's the cloneRoot, never ours to delete.
            // Background: 2026-05-13 DeleteSpawn(BaseBranch=main) reached
            // here via the now-removed step 6a.1 and wiped the user's
            // `E:\forge\workflow` (including `.git`).
            if (IsSamePath(existing, repo.CloneRoot) || IsMainWorktreeDir(existing))
            {
                this.logger?.LogError(
                    "RemoveWorktreeAsync REFUSING to delete main worktree {Path} for branch {Branch}. "
                    + "This would wipe the cloneRoot (`{Root}`). Bailing.",
                    existing, branch, repo.CloneRoot);
                return;
            }

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

    /// <inheritdoc />
    public async Task<bool> WorktreeExistsAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        // Phase 59 — best-effort probe. Returns false when the repo / branch
        // / worktree is clearly absent; defaults to true on probe failure so
        // the caller falls back to its existing try/catch-around-Commit path
        // rather than mis-classifying a transient git error as "worktree
        // gone" and auto-resolving a live workstream's reviewer comments.
        try
        {
            if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return false;
            if (!Directory.Exists(repo.CloneRoot)) return false;

            var existing = await this.TryFindExistingWorktreeAsync(repo.CloneRoot, branch, ct);
            if (string.IsNullOrEmpty(existing)) return false;

            // Phase 55 — `TryFindExistingWorktreeAsync` may legitimately
            // return the main worktree (cloneRoot itself) when the operator
            // checked the clone out onto this branch. That counts as
            // "worktree exists for the branch" from the orchestrator's
            // perspective — DriveReviewAsync's CommitFilesAsync will
            // happily target the main worktree. So no special-casing
            // needed here.
            return Directory.Exists(existing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex,
                "WorktreeExistsAsync({Branch}) probe failed; assuming present so the caller falls back to its commit-throws path.",
                branch);
            return true;
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
        // Per-iter commit assumes the worktree was already created by an
        // earlier <c>EnsureWorktreeAsync</c> / <c>CommitFilesAsync</c> call.
        // Ask git for the registered path so we don't need to know the slug.
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (string.IsNullOrEmpty(wtPath))
        {
            throw new InvalidOperationException(
                $"CommitPathInWorktreeAsync: no worktree registered for branch '{branch}'. "
                + "Callers must materialise the worktree (via EnsureWorktreeAsync or CommitFilesAsync) first.");
        }

        // Serialise per-branch so concurrent agent commits don't race the
        // git index lock.
        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {

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
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);

        if (string.IsNullOrEmpty(wtPath) || !Directory.Exists(wtPath))
        {
            this.logger?.LogInformation(
                "PushBranchAsync: no worktree registered for branch '{Branch}'; skipping.", branch);
            return;
        }

        var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Stage + commit any uncommitted changes so they're included
            // in the push (e.g. a generator interrupted mid-write).
            await this.RunGitCaptureAsync(wtPath, ct, "add", "-A");
            await this.ScrubForgeScratchFromIndexAsync(wtPath, ct);
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

    /// <inheritdoc/>
    public async Task<IterCommitSnapshot?> SnapshotBranchHeadAsync(
        RepoRow? repo,
        string branch,
        string? baselineSha,
        CancellationToken ct = default)
    {
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot) || string.IsNullOrEmpty(branch))
        {
            return null;
        }

        // Prefer the worktree path for the branch so the rev-parse / log /
        // diff calls run in a checkout that exists on disk (avoids
        // surprises when the cloneRoot itself is on a different branch).
        // Fall back to the cloneRoot — `git rev-parse {branch}` resolves
        // against the local ref store regardless of working dir, so the
        // fallback still produces a valid SHA for a never-checked-out
        // branch (or one whose worktree was removed post-merge — the
        // exact case Phase 65 needs to survive).
        var workDir = await this.TryFindExistingWorktreeAsync(repo.CloneRoot, branch, ct);
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            workDir = repo.CloneRoot;
        }

        // 1. Resolve HEAD SHA on the branch ref.
        var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(workDir, ct, "rev-parse", "--verify", $"{branch}^{{commit}}");
        if (shaCode != 0 || string.IsNullOrWhiteSpace(shaOut)) return null;
        var newSha = shaOut.Trim();

        // 2. Pull the commit subject — short message for tooltip display.
        var (subjectCode, subjectOut, _) = await this.RunGitCaptureAsync(workDir, ct, "log", "-1", "--format=%s", newSha);
        var subject = subjectCode == 0 ? subjectOut.Trim() : string.Empty;

        // 3. When the caller has no baseline (iter 1 start, or a previous
        // iter that captured nothing), or the baseline equals HEAD (no
        // new commit since), report zero counts but still surface the
        // SHA so the caller can record the boundary observation.
        if (string.IsNullOrEmpty(baselineSha) || string.Equals(baselineSha, newSha, StringComparison.Ordinal))
        {
            return new IterCommitSnapshot(newSha, subject, 0, 0, 0);
        }

        // 4. Compute name-status diff for A/M/D classification.
        //    `git diff --name-status -z` emits NUL-separated records:
        //    "{status}\t{path}[\t{newPath}]" — renames carry two paths.
        var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(
            workDir, ct, "diff", "--name-status", "-z", $"{baselineSha}..{newSha}");
        if (diffCode != 0)
        {
            // Baseline may not be reachable from newSha (rebase, force-push,
            // shallow clone). Return the SHA + subject with null-ish zero
            // counts so the row still records "we saw this commit at iter
            // boundary" even if the delta is unknown.
            return new IterCommitSnapshot(newSha, subject, 0, 0, 0);
        }

        int added = 0, modified = 0, deleted = 0;
        var fields = diffOut.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i];
            if (string.IsNullOrEmpty(status)) continue;
            // Rename / copy entries are 3-field: status\0oldPath\0newPath.
            // Everything else is 2-field: status\0path.
            var skip = status.StartsWith('R') || status.StartsWith('C') ? 2 : 1;
            i += skip;
            if (i >= fields.Length) break;

            // Status letter is the first char (R100 / C95 etc carry digits).
            switch (status[0])
            {
                case 'A': added++; break;
                case 'M': modified++; break;
                case 'D': deleted++; break;
                case 'R': modified++; break;  // rename counts as modify
                case 'C': added++; break;     // copy adds a new path
                case 'T': modified++; break;  // type change
                default: break;               // U (unmerged), X, ? — ignore
            }
        }

        return new IterCommitSnapshot(newSha, subject, added, modified, deleted);
    }

    /// <inheritdoc/>
    public async Task<IterCommitSnapshot?> SnapshotWorktreeChangesAsync(
        RepoRow? repo,
        string worktreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }
        if (string.IsNullOrEmpty(baseBranch))
        {
            // Without a base reference there's nothing to diff against —
            // bail to null so the caller falls through to event aggregation.
            return null;
        }

        // 1. Capture HEAD sha + subject so the deliverable row records
        //    the worktree's commit anchor (matches the pre-existing
        //    SnapshotBranchHeadAsync shape so consumers don't branch).
        var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(worktreePath, ct, "rev-parse", "HEAD");
        var newSha = shaCode == 0 ? shaOut.Trim() : string.Empty;
        var subject = string.Empty;
        if (!string.IsNullOrEmpty(newSha))
        {
            var (subjectCode, subjectOut, _) = await this.RunGitCaptureAsync(worktreePath, ct, "log", "-1", "--format=%s", newSha);
            if (subjectCode == 0) subject = subjectOut.Trim();
        }

        // 2. Diff baseBranch → worktree (committed + uncommitted, tracked
        //    files only). The 3-dot form `baseBranch...HEAD` would show
        //    only committed deltas; we want the working-tree view so
        //    operator-relevant WIP shows up before the final commit.
        //    `git diff --name-status {base} -- ./` shows what's different
        //    between baseBranch and the working tree (combines committed
        //    deltas against base + uncommitted modifications to tracked
        //    files).
        var trackedPaths = new Dictionary<string, char>(StringComparer.Ordinal);
        var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "diff", "--name-status", "-z", $"{baseBranch}", "--", "./");
        if (diffCode == 0 && !string.IsNullOrEmpty(diffOut))
        {
            var fields = diffOut.Split('\0');
            for (var i = 0; i < fields.Length; i++)
            {
                var status = fields[i];
                if (string.IsNullOrEmpty(status)) continue;
                // Rename / copy: status\0oldPath\0newPath (3 fields).
                // All others: status\0path (2 fields).
                var skip = status.StartsWith('R') || status.StartsWith('C') ? 2 : 1;
                if (i + skip >= fields.Length) break;
                // The PATH the operator cares about is the destination
                // for renames/copies (the new path), the only path for
                // everything else.
                var path = fields[i + skip];
                i += skip;
                if (string.IsNullOrEmpty(path) || IsBuildArtifactPath(path)) continue;
                // Status letter is the first char (R100 / C95 carry digits).
                trackedPaths[path] = status[0];
            }
        }

        // 3. Untracked files — `git ls-files --others --exclude-standard`
        //    respects .gitignore so build artifacts / IDE noise stay out.
        var untrackedPaths = new HashSet<string>(StringComparer.Ordinal);
        var (lsCode, lsOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "ls-files", "--others", "--exclude-standard", "-z");
        if (lsCode == 0 && !string.IsNullOrEmpty(lsOut))
        {
            foreach (var p in lsOut.Split('\0'))
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (IsBuildArtifactPath(p)) continue;
                untrackedPaths.Add(p);
            }
        }

        // 4. Classify A/M/D. Untracked files become Added; tracked-status
        //    flags from the diff become Added (A/C), Modified (M/R/T),
        //    or Deleted (D). Renames are counted as a modify on the new
        //    path (same convention as SnapshotBranchHeadAsync).
        int added = 0, modified = 0, deleted = 0;
        foreach (var entry in trackedPaths)
        {
            switch (entry.Value)
            {
                case 'A': added++; break;
                case 'C': added++; break;
                case 'M': modified++; break;
                case 'R': modified++; break;
                case 'T': modified++; break;
                case 'D': deleted++; break;
                default: break;
            }
        }
        // Untracked → Added, but only if the path wasn't already tallied
        // as committed-added (avoids double-count when a file is staged
        // and also appears in ls-files via a different working-tree
        // listing path — defensive).
        foreach (var u in untrackedPaths)
        {
            if (!trackedPaths.ContainsKey(u)) added++;
        }

        return new IterCommitSnapshot(newSha, subject, added, modified, deleted);
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
            ".forge/",            // Forge iteration scratch (iter-notes, notes/)
        ];
        foreach (var dir in dirs)
        {
            if (p.StartsWith(dir, StringComparison.Ordinal)) return true;
            if (p.Contains("/" + dir, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ── Phase 79 — agent-driven merge-conflict resolver ──────────────────

    /// <inheritdoc/>
    public async Task<RebaseResult> RebaseAndDetectConflictsAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var existingWtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        var wtPath = !string.IsNullOrEmpty(existingWtPath)
            ? existingWtPath
            : WorktreePath(root, SanitizeBranchSlug(branch));
        await this.EnsureWorktreeAsync(root, wtPath, branch, branch, ct);

        // 1. Abort any leftover rebase from a previous attempt so we start clean.
        await this.AbortRebaseAsync(repo, branch, ct);

        // 2. Refresh both refs.
        await this.GitAsync(wtPath, ct, "fetch", "origin", baseBranch);
        var (lsBranchCode, lsBranchOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "ls-remote", "--heads", "origin", branch);
        if (lsBranchCode == 0 && !string.IsNullOrWhiteSpace(lsBranchOut))
        {
            await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
            await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{branch}");
        }

        // 3. Attempt the rebase. Capture stderr — git uses non-zero exit
        //    for both "conflict needs resolution" and "fatal: cannot rebase".
        var (rcCode, _, rcErr) = await this.RunGitCaptureAsync(
            wtPath, ct, "rebase", $"origin/{baseBranch}");
        if (rcCode == 0)
        {
            this.logger?.LogInformation(
                "Phase 79: rebase of {Branch} onto origin/{BaseBranch} clean.",
                branch, baseBranch);
            return new RebaseResult(RebaseState.Clean, Array.Empty<ConflictedFile>(), null);
        }

        // 4. Determine whether the non-zero is a conflict or a hard fail.
        //    `git rebase --show-current-patch` returning non-empty (any
        //    of UU/AA/DD/UD/DU/AU/UA in status) means we're paused on a
        //    conflict. Otherwise abort + return RebaseFailed.
        var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "status", "--porcelain");
        var conflictedPaths = new List<string>();
        if (statusCode == 0)
        {
            foreach (var rawLine in statusOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length < 3) continue;
                var x = line[0];
                var y = line[1];
                // Per git-status(1): unmerged paths use XY ∈ {DD, AU, UD, UA, DU, AA, UU}.
                var unmerged = (x == 'U' || y == 'U')
                    || (x == 'A' && y == 'A')
                    || (x == 'D' && y == 'D');
                if (!unmerged) continue;
                var path = line.Substring(3).Trim();
                if (path.StartsWith('"') && path.EndsWith('"'))
                {
                    path = path[1..^1];
                }
                if (!string.IsNullOrEmpty(path)) conflictedPaths.Add(path);
            }
        }
        if (conflictedPaths.Count == 0)
        {
            // Hard rebase failure (no commits to rebase, unstaged changes
            // blocking, fatal error). Abort + bubble the stderr.
            await this.AbortRebaseAsync(repo, branch, ct);
            return new RebaseResult(RebaseState.RebaseFailed, Array.Empty<ConflictedFile>(),
                $"git rebase exit={rcCode}: {rcErr?.Trim()}");
        }

        // 5. Read each conflicted file's on-disk content (markers preserved).
        var files = new List<ConflictedFile>(conflictedPaths.Count);
        foreach (var rel in conflictedPaths)
        {
            var diskPath = System.IO.Path.Combine(wtPath, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string content;
            try { content = await System.IO.File.ReadAllTextAsync(diskPath, ct); }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex,
                    "Phase 79: could not read conflicted file '{Path}' from worktree '{Wt}'; substituting empty content.",
                    rel, wtPath);
                content = string.Empty;
            }
            files.Add(new ConflictedFile(rel, content));
        }
        this.logger?.LogInformation(
            "Phase 79: rebase of {Branch} onto origin/{BaseBranch} paused on {N} conflicted file(s).",
            branch, baseBranch, files.Count);
        return new RebaseResult(RebaseState.Conflicted, files, null);
    }

    /// <inheritdoc/>
    public async Task<RebaseResult> ContinueRebaseAndPushAsync(
        RepoRow? repo,
        string branch,
        IReadOnlyDictionary<string, string> resolvedFiles,
        CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct)
            ?? throw new InvalidOperationException(
                $"Phase 79: ContinueRebaseAndPushAsync called for branch '{branch}' but no worktree is registered. RebaseAndDetectConflictsAsync was expected to run first.");

        // 1. Write resolved files + stage them.
        foreach (var kv in resolvedFiles)
        {
            var diskPath = System.IO.Path.Combine(wtPath, kv.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var dir = System.IO.Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            await System.IO.File.WriteAllTextAsync(diskPath, kv.Value, ct);
            await this.GitAsync(wtPath, ct, "add", "--", kv.Key);
        }

        // 2. Continue the rebase. May land at Clean, another conflict
        //    round, or a hard error (resolved files didn't actually
        //    resolve, e.g. left markers in place).
        //    GIT_EDITOR=true prevents the rebase --continue from
        //    prompting for an interactive commit message edit.
        var (rcCode, _, rcErr) = await this.RunGitCaptureWithEnvAsync(
            wtPath, ct,
            envOverride: new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" },
            args: new[] { "rebase", "--continue" });

        if (rcCode != 0)
        {
            // Either another conflict round or a hard failure. Re-inspect
            // git status — same logic as the initial RebaseAndDetectConflictsAsync.
            var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "status", "--porcelain");
            var conflictedPaths = new List<string>();
            if (statusCode == 0)
            {
                foreach (var rawLine in statusOut.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length < 3) continue;
                    var x = line[0]; var y = line[1];
                    var unmerged = (x == 'U' || y == 'U')
                        || (x == 'A' && y == 'A')
                        || (x == 'D' && y == 'D');
                    if (!unmerged) continue;
                    var path = line.Substring(3).Trim();
                    if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
                    if (!string.IsNullOrEmpty(path)) conflictedPaths.Add(path);
                }
            }
            if (conflictedPaths.Count == 0)
            {
                await this.AbortRebaseAsync(repo, branch, ct);
                return new RebaseResult(RebaseState.RebaseFailed, Array.Empty<ConflictedFile>(),
                    $"git rebase --continue exit={rcCode}: {rcErr?.Trim()}");
            }
            var files = new List<ConflictedFile>(conflictedPaths.Count);
            foreach (var rel in conflictedPaths)
            {
                var diskPath = System.IO.Path.Combine(wtPath, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                string content;
                try { content = await System.IO.File.ReadAllTextAsync(diskPath, ct); }
                catch { content = string.Empty; }
                files.Add(new ConflictedFile(rel, content));
            }
            this.logger?.LogInformation(
                "Phase 79: rebase --continue paused on {N} additional conflicted file(s) for {Branch}.",
                files.Count, branch);
            return new RebaseResult(RebaseState.Conflicted, files, null);
        }

        // 3. Rebase clean. Force-push with-lease.
        //    Capture the new tip + the origin tip so --force-with-lease
        //    can compare them. If origin/branch has moved (e.g. concurrent
        //    operator push), the push fails safely.
        var (oldOrigCode, oldOrigOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "rev-parse", $"origin/{branch}");
        var oldOriginSha = oldOrigCode == 0 ? oldOrigOut.Trim() : string.Empty;
        string[] pushArgs;
        if (!string.IsNullOrEmpty(oldOriginSha))
        {
            pushArgs = new[]
            {
                "push", "--force-with-lease=" + branch + ":" + oldOriginSha,
                "origin", branch,
            };
        }
        else
        {
            // No origin yet for this branch (shouldn't happen at this
            // stage of the pipeline) — fall back to a plain --force-with-lease.
            pushArgs = new[] { "push", "--force-with-lease", "origin", branch };
        }
        var (pushCode, _, pushErr) = await this.RunGitCaptureAsync(wtPath, ct, pushArgs);
        if (pushCode != 0)
        {
            await this.AbortRebaseAsync(repo, branch, ct);
            return new RebaseResult(RebaseState.RebaseFailed, Array.Empty<ConflictedFile>(),
                $"force-push exit={pushCode}: {pushErr?.Trim()}");
        }

        this.logger?.LogInformation(
            "Phase 79: rebase of {Branch} clean + force-pushed; {N} file(s) resolved.",
            branch, resolvedFiles.Count);
        return new RebaseResult(RebaseState.Clean, Array.Empty<ConflictedFile>(), null);
    }

    /// <inheritdoc/>
    public async Task AbortRebaseAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (string.IsNullOrEmpty(wtPath)) return;
        // Idempotent: --quit + --abort both no-op when no rebase is in progress.
        await this.RunGitCaptureAsync(wtPath, ct, "rebase", "--abort");
    }

    /// <summary>
    /// Like <see cref="RunGitCaptureAsync"/> but with an environment-variable
    /// override (used by Phase 79 to set <c>GIT_EDITOR=true</c> so
    /// <c>git rebase --continue</c> doesn't drop into the editor).
    /// </summary>
    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureWithEnvAsync(
        string cwd, CancellationToken ct,
        IReadOnlyDictionary<string, string?> envOverride,
        string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in envOverride)
        {
            if (kv.Value is null) psi.Environment.Remove(kv.Key);
            else psi.Environment[kv.Key] = kv.Value;
        }
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Scrubs Forge scratch paths (<c>.forge/</c>) from the git index so
    /// they never land in a commit. Handles both freshly-staged untracked
    /// files and already-tracked files from prior iterations. Uses
    /// <c>--ignore-unmatch</c> so the call is a safe no-op when the
    /// directory doesn't exist. Best-effort: any failure is swallowed.
    /// </summary>
    public async Task ScrubForgeScratchFromIndexAsync(string wtPath, CancellationToken ct)
    {
        try
        {
            await this.RunGitCaptureAsync(wtPath, ct,
                "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Removes <c>.forge/</c> from both the git index AND the working tree
    /// (on disk). If the removal produces a staged diff, commits the
    /// deletion so that the branch tree is clean before a merge or push.
    /// Best-effort: failures are logged but never thrown.
    /// </summary>
    private async Task ScrubForgeScratchFromTreeAsync(string wtPath, CancellationToken ct)
    {
        try
        {
            // Remove from the index (handles tracked files from prior iterations).
            await this.RunGitCaptureAsync(wtPath, ct,
                "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");

            // Remove from disk so a subsequent `git add -A` or merge commit
            // doesn't re-introduce them. Swallow IO exceptions — on Windows,
            // file handles from an agent process can keep directories locked.
            var forgeDir = Path.Combine(wtPath, ".forge");
            if (Directory.Exists(forgeDir))
            {
                try { Directory.Delete(forgeDir, recursive: true); }
                catch (Exception ex)
                {
                    this.logger?.LogDebug(ex,
                        "ScrubForgeScratchFromTreeAsync: could not delete .forge/ from disk at {WtPath}; "
                        + "index scrub still applied.", wtPath);
                }
            }

            // If the scrub staged any deletions, commit them so the tree is
            // clean for the next merge. Only check .forge/ paths to avoid
            // accidentally committing unrelated staged changes.
            var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(wtPath, ct,
                "diff", "--cached", "--name-only", "--", ".forge/");
            if (diffCode == 0 && !string.IsNullOrWhiteSpace(diffOut))
            {
                await this.RunGitCaptureAsync(wtPath, ct,
                    "-c", "user.email=forge-merger@local",
                    "-c", "user.name=Forge",
                    "commit", "-m", "forge: remove scratch files from tree");
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex,
                "ScrubForgeScratchFromTreeAsync: best-effort scrub failed at {WtPath}.", wtPath);
        }
    }

    /// <summary>
    /// During an in-progress merge with conflicts, resolves any conflicts
    /// under <c>.forge/</c> by removing those paths from the index + disk.
    /// Returns <c>true</c> if ALL remaining conflicts were under
    /// <c>.forge/</c> (merge can be completed), <c>false</c> if non-forge
    /// conflicts remain.
    /// </summary>
    private async Task<bool> TryResolveForgeConflictsAsync(string wtPath, CancellationToken ct)
    {
        try
        {
            // List unmerged (conflicting) paths.
            var (code, output, _) = await this.RunGitCaptureAsync(wtPath, ct,
                "diff", "--name-only", "--diff-filter=U");
            if (code != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var forgeConflicts = new List<string>();
            bool hasNonForgeConflict = false;

            foreach (var line in lines)
            {
                var norm = line.Replace('\\', '/');
                if (norm.StartsWith(".forge/", StringComparison.Ordinal)
                    || norm.Equals(".forge", StringComparison.Ordinal))
                {
                    forgeConflicts.Add(line);
                }
                else
                {
                    hasNonForgeConflict = true;
                }
            }

            if (forgeConflicts.Count == 0)
                return false;

            // Resolve .forge/ conflicts by removing them.
            foreach (var path in forgeConflicts)
            {
                await this.RunGitCaptureAsync(wtPath, ct,
                    "rm", "--ignore-unmatch", "--", path);
            }

            // Delete from disk too.
            var forgeDir = Path.Combine(wtPath, ".forge");
            if (Directory.Exists(forgeDir))
            {
                try { Directory.Delete(forgeDir, recursive: true); }
                catch { /* best-effort */ }
            }

            this.logger?.LogInformation(
                "TryResolveForgeConflictsAsync: resolved {Count} .forge/ conflict(s) in {WtPath}; "
                + "hasNonForgeConflict={HasNonForge}.",
                forgeConflicts.Count, wtPath, hasNonForgeConflict);

            return !hasNonForgeConflict;
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex,
                "TryResolveForgeConflictsAsync: failed at {WtPath}.", wtPath);
            return false;
        }
    }

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
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Worktree slug is required.", nameof(slug));
        var wtRoot = Path.Combine(cloneRoot, ".worktree");
        Directory.CreateDirectory(wtRoot);
        return Path.Combine(wtRoot, slug);
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

        // The branch may already be registered to a DIFFERENT worktree
        // path (operator-customised layout, leftover from an aborted
        // claim, etc.). Detect that and unregister it before we try to
        // add a new worktree, otherwise `git worktree add` aborts with
        // "branch already checked out at {oldPath}". The branch ref (and
        // any local commits the agent made) survives the worktree
        // removal — only the working directory is torn down.
        var existingOnBranch = await this.TryFindExistingWorktreeAsync(cloneRoot, branch, ct);
        if (!string.IsNullOrEmpty(existingOnBranch))
        {
            var wantedNormalized = wtPath.Replace('\\', '/');
            var existingNormalized = existingOnBranch.Replace('\\', '/');
            if (!string.Equals(wantedNormalized, existingNormalized, StringComparison.OrdinalIgnoreCase))
            {
                // Phase 55 — refuse to delete the cloneRoot itself if
                // `TryFindExistingWorktreeAsync` returned it (this happens
                // when the operator manually checked out the main clone
                // onto a `ws/...` branch, or any base-branch hand-off
                // where the cloneRoot's HEAD matches the queried branch).
                // Two-pronged detection mirrors RemoveWorktreeAsync —
                // normalized path equality + structural `.git`-is-a-dir
                // check. Background: 2026-05-13 DeleteSpawn(BaseBranch=
                // main) reached an analogous code path and wiped the
                // user's `E:\forge\workflow`.
                if (IsSamePath(existingOnBranch, cloneRoot) || IsMainWorktreeDir(existingOnBranch))
                {
                    this.logger?.LogError(
                        "EnsureWorktreeAsync REFUSING to migrate main worktree {Path} for branch {Branch}. "
                        + "This would wipe the cloneRoot. Bailing out of migration; reusing main worktree as-is.",
                        existingOnBranch, branch);
                }
                else
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
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");

        // Bounded safety net so a credential prompt that goes
        // unanswered (operator AFK, desktop locked, GCM stuck) can't
        // strand the worker. Even in FailFast mode this protects
        // against pathological cases (DNS resolution stalls, broken
        // network helpers, etc.). Override via the env var below.
        // Default: 180s — long enough for an interactive sign-in.
        var timeoutSeconds = 180;
        var timeoutRaw = Environment.GetEnvironmentVariable("FORGE_GIT_NETWORK_TIMEOUT_SECONDS");
        if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0) timeoutSeconds = parsed;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Start the stream reads WITHOUT the cancellation token so they
        // naturally drain to EOF when the process exits (whether the
        // process exited normally or was killed by us on timeout). If
        // we cancel the reads with linkedCts we'd lose the stderr that
        // explains the credential issue.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            this.logger?.LogDebug("git {Args} → exit={Exit} cwd={Cwd}", string.Join(' ', args), p.ExitCode, cwd);
            return (p.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            // Drain whatever stderr was emitted before the kill — that
            // is the most useful diagnostic for the operator.
            string drainedStderr = string.Empty;
            try { drainedStderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* best-effort */ }
            throw new TimeoutException(
                $"git {string.Join(' ', args)} timed out after {timeoutSeconds}s in '{cwd}'. " +
                $"If this is a credential challenge, run the command interactively first to cache the credential, then retry. " +
                $"Partial stderr: {drainedStderr}");
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
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
