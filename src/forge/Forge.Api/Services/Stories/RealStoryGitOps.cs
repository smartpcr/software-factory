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

    // GitHub auth service (gh-CLI backed). Used to inject a non-interactive
    // credential into GitHub network ops (push/fetch/…) — Forge has a silent
    // path for ADO (GCM_AZREPOS_CREDENTIALTYPE=oauth) but, before this, none
    // for GitHub: pushes fell back to GCM's interactive dialog, which on an
    // unattended/locked desktop hangs (180s timeout) or is cancelled
    // ("could not read Username … terminal prompts disabled", exit 128) and
    // HALTED the whole spawn. Observed code-intelligence:ENABLE-MCP base-branch
    // push to github.com/smartpcr/code-intelligence, 2026-06-24.
    private readonly Forge.Api.Auth.IAuthService? githubAuth;

    private readonly SemaphoreSlim ghTokenGate = new(1, 1);
    private string? cachedGhToken;
    private DateTimeOffset ghTokenCachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan GhTokenTtl = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Phase 42 — per-branch git lock. Concurrent architect agents share one
    /// worktree (git refuses two worktrees on one branch); this serialises
    /// staging + commit ops so they don't race the git index.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> branchLocks = new();

    public RealStoryGitOps(
        ILogger<RealStoryGitOps>? logger = null,
        IEnumerable<Forge.Api.Auth.IAuthService>? authServices = null)
    {
        this.logger = logger;
        Forge.Api.Auth.IAuthService? gh = null;
        if (authServices is not null)
        {
            foreach (var a in authServices)
            {
                if (a.Kind == "github") { gh = a; break; }
            }
        }
        this.githubAuth = gh;
    }

    /// <summary>
    /// True when <paramref name="args"/> is a git subcommand that makes a
    /// network request to the remote — the only ops that need a credential.
    /// Local ops (commit/status/reset/config/…) are skipped so we don't pay a
    /// token lookup or attach an unused header to them.
    /// </summary>
    private static bool IsNetworkGitOp(string[] args)
        => args.Length > 0 && args[0] is "push" or "fetch" or "pull" or "clone" or "ls-remote";

    /// <summary>
    /// Resolves the current GitHub access token (gh-CLI) with a short in-memory
    /// TTL so a burst of network ops doesn't shell out to <c>gh</c> each time
    /// (the underlying <see cref="Forge.Api.Auth.IAuthService"/> also caches).
    /// Returns null when no GitHub auth service is wired or no token is
    /// available — callers then fall back to the prior GCM behaviour.
    /// </summary>
    private async Task<string?> ResolveGitHubTokenAsync(CancellationToken ct)
    {
        if (this.githubAuth is null) return null;
        if (this.cachedGhToken is not null && DateTimeOffset.UtcNow - this.ghTokenCachedAt < GhTokenTtl)
        {
            return this.cachedGhToken;
        }
        await this.ghTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.cachedGhToken is not null && DateTimeOffset.UtcNow - this.ghTokenCachedAt < GhTokenTtl)
            {
                return this.cachedGhToken;
            }
            var token = await this.githubAuth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                this.cachedGhToken = token;
                this.ghTokenCachedAt = DateTimeOffset.UtcNow;
            }
            return token;
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "ResolveGitHubTokenAsync failed; GitHub network ops fall back to GCM.");
            return null;
        }
        finally
        {
            this.ghTokenGate.Release();
        }
    }

    public async Task EnsureBranchAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Make sure we have the latest base. Capture (don't throw): this is a
        // shared-clone network op that can transiently fail on a Windows
        // config / packed-refs race (GCM rewriting the per-clone .gitconfig
        // mid-read → exit 128, sometimes with EMPTY stderr the retry classifier
        // can't match — observed 2026-07-01 on the `update` clone, which HALTED
        // the whole spawn: "implement failed: git fetch origin main failed
        // (exit 128)"). A stale local origin/{base} is harmless here — the
        // branch-recreate path below and the later worktree merge/rebase both
        // re-fetch — so a transient base fetch must NOT halt the orchestrator.
        // The branch fetch a few lines down was soft-failed for this same
        // reason (transient race bubbling to TickOneSpawnAsync); this closes
        // the matching gap on the base fetch. RunGitCaptureAsync already runs
        // the lock + empty-stderr-network retry internally, so reaching here
        // non-zero means the transient budget was genuinely exhausted.
        var (baseFetchCode, _, baseFetchErr) = await this.RunGitCaptureAsync(root, ct, "fetch", "origin", baseBranch);
        if (baseFetchCode != 0)
        {
            this.logger?.LogWarning(
                "EnsureBranchAsync: `fetch origin {Base}` failed (exit {Code}) in '{Root}'; "
                + "continuing with the last-known base ref (stale base is recovered by the "
                + "downstream branch-create + worktree merge). stderr: {Err}",
                baseBranch, baseFetchCode, root, baseFetchErr);
        }

        // Does the branch already exist on origin? If so, make sure local
        // tracking ref is up to date and exit.
        var (lsCode, lsOut, _) = await this.RunGitCaptureAsync(root, ct, "ls-remote", "--heads", "origin", branch);
        if (lsCode == 0 && !string.IsNullOrWhiteSpace(lsOut))
        {
            // Capture (don't throw): ls-remote and this fetch are two separate
            // round-trips, so the ref can vanish between them — an operator
            // Restart deletes the remote branch, or a sibling PR merge
            // auto-deletes it. Pre-fix, that fetch threw "couldn't find remote
            // ref" and the exception bubbled to TickOneSpawnAsync, halting the
            // ENTIRE spawn (ORCHESTRATOR HALTED) on a transient race. Now: if
            // the fetch fails because the ref is gone, fall through to the
            // push-create path below and re-create the branch from base.
            var (fetchCode, _, fetchErr) = await this.RunGitCaptureAsync(root, ct, "fetch", "origin", branch);
            if (fetchCode == 0)
            {
                return;
            }

            var refMissing = (fetchErr ?? string.Empty)
                .Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase);
            if (!refMissing)
            {
                throw new InvalidOperationException(
                    $"git fetch origin {branch} failed (exit {fetchCode}) in '{root}'.\nstderr: {fetchErr}");
            }

            this.logger?.LogInformation(
                "EnsureBranchAsync: ls-remote saw {Branch} but the fetch missed it "
                + "(ref deleted mid-flight — Restart or sibling-merge auto-delete); "
                + "re-creating from {Base}.",
                branch, baseBranch);
            // fall through to the push-from-base recreate path
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

        // 2026-05-28 — defense against Win32Exception "working directory
        // missing". EnsureWorktreeAsync trusts git's internal worktree
        // state (it short-circuits when `git worktree list` lists this
        // path), but the on-disk directory can disappear out of band:
        // a sibling spawn's cleanup, an antivirus quarantine, an operator
        // `rm`, or a Windows %TEMP%-cleaner pass. When `Process.Start`
        // is then handed this path as cwd it throws Win32Exception
        // "An error occurred trying to start process 'git' with working
        // directory '…'", which the qa-e2e catch site stamps as a hard
        // stall — observed code-intelligence:AST-PARSER-FOR-ADDIT stage
        // 1.5-e2e 2026-05-28: iter-3 passed at 08:33:11Z, post-pass commit
        // hit Win32 at 08:33:16Z, workstream sat stalled for 11+ hours
        // despite the deliverable being valid. Re-prune + re-create the
        // worktree in place when the on-disk dir is missing.
        if (!Directory.Exists(wtPath))
        {
            this.logger?.LogWarning(
                "CommitFilesAsync: worktree directory missing at {WtPath} after EnsureWorktreeAsync " +
                "(branch={Branch}). Pruning stale ref and re-creating in place before proceeding.",
                wtPath, branch);
            await this.RunGitCaptureAsync(root, ct, "worktree", "prune");
            await this.EnsureWorktreeAsync(root, wtPath, branch, "main", ct);
            if (!Directory.Exists(wtPath))
            {
                throw new InvalidOperationException(
                    $"CommitFilesAsync: failed to materialize worktree at '{wtPath}' for branch '{branch}' " +
                    "after `git worktree prune` + EnsureWorktreeAsync — operator intervention required.");
            }
        }

        // Phase 81 — preflight: fail loudly when any input path is excluded
        // by the repo's .gitignore (e.g. OneBranch "/*\n!/src/" allowlists
        // silently exclude docs/**). Without this, `git add -A` would succeed
        // and `git status --porcelain` would return empty, turning the commit
        // into a no-op that ships an unchanged branch under an "empty" PR.
        // See <see cref="GitIgnoredPathException"/> for the full failure mode.
        if (files.Count > 0)
        {
            await this.AssertPathsNotIgnoredAsync(wtPath, branch, files.Keys, ct);
        }

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

        await this.StageAllAndScrubAsync(wtPath, ct);

        // Delete Forge scratch from disk so a subsequent `git add -A` (e.g.,
        // in MergeUpstreamIntoBranchAsync) doesn't re-stage the files.
        // Preserve .forge/memory: Copilot CLI/SDK session state lives there.
        this.DeleteForgeScratchExceptMemory(wtPath);

        // Detect "nothing to commit" — git returns non-zero for an empty
        // commit unless --allow-empty is passed; we don't want empty
        // history rows.
        //
        // IMPORTANT: check the STAGED index, NOT `git status --porcelain`.
        // Porcelain also reports untracked working-tree entries (e.g. a
        // leftover `__pycache__/` dir that `git add -A` skipped or that the
        // scrub steps above just removed from the index). Those make
        // porcelain non-empty even when nothing is staged, so the old
        // porcelain check fell through to `git commit`, which exits 1
        // ("nothing added to commit but untracked files present") and threw.
        // That permanently stranded any idempotent re-commit: observed on
        // code-intelligence:CLEAN-CODE qa-e2e `1.2-e2e`, where the files were
        // already committed+pushed on the first run, so every re-tick
        // re-wrote identical content, staged nothing, and the commit
        // hard-failed BEFORE the orchestrator could reach OpenPrAsync — a
        // passing deliverable that never produced a PR, looping every tick.
        // `git diff --cached --quiet` exits 0 iff the index == HEAD (nothing
        // staged), so we skip the commit and still push + return HEAD so the
        // PR step proceeds against the already-committed branch tip.
        var (stagedCode, _, _) = await this.RunGitCaptureAsync(wtPath, ct, "diff", "--cached", "--quiet");
        if (stagedCode == 0)
        {
            this.logger?.LogInformation("CommitFilesAsync: no staged changes to commit on branch {Branch}", branch);
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

    /// <inheritdoc />
    public async Task<bool> CommitAllAndPushAsync(
        string worktreePath, string commitMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            commitMessage = "forge: per-iter snapshot";
        }

        try
        {
            // Quick gate: anything to stage?
            var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(
                worktreePath, ct, "status", "--porcelain");
            if (statusCode != 0)
            {
                this.logger?.LogWarning(
                    "CommitAllAndPushAsync: `git status` exited {Code} at {Path}; skipping per-iter commit.",
                    statusCode, worktreePath);
                return false;
            }
            if (string.IsNullOrWhiteSpace(statusOut))
            {
                return false; // clean worktree — nothing to commit
            }

            // Resolve the current branch from the worktree itself so callers
            // don't have to thread it through.
            var (branchCode, branchOut, _) = await this.RunGitCaptureAsync(
                worktreePath, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (branchCode != 0 || string.IsNullOrWhiteSpace(branchOut))
            {
                this.logger?.LogWarning(
                    "CommitAllAndPushAsync: could not resolve HEAD branch at {Path}; skipping.",
                    worktreePath);
                return false;
            }
            var branch = branchOut.Trim();
            if (branch == "HEAD")
            {
                this.logger?.LogWarning(
                    "CommitAllAndPushAsync: HEAD is detached at {Path}; skipping per-iter push.",
                    worktreePath);
                return false;
            }

            // Stage everything, scrubbing .forge/ scratch + well-known build
            // artifacts (mirrors the CommitFilesAsync pipeline).
            await this.StageAllAndScrubAsync(worktreePath, ct);

            // Re-check: after the scrub, is anything actually staged?
            var (stagedCode, _, _) = await this.RunGitCaptureAsync(
                worktreePath, ct, "diff", "--cached", "--quiet");
            if (stagedCode == 0)
            {
                // index == HEAD — every change was scrubbed (e.g. only
                // .forge/ + build artifacts in the worktree). Treat as
                // clean rather than failing the iter.
                return false;
            }

            // Commit with the forge-engineer identity. Mirrors
            // CrewQueueWorker.cs:1844-1849.
            var (commitCode, _, commitErr) = await this.RunGitCaptureAsync(
                worktreePath, ct,
                "-c", "user.email=forge-engineer@local",
                "-c", "user.name=Forge",
                "commit", "-m", commitMessage);
            if (commitCode != 0)
            {
                this.logger?.LogWarning(
                    "CommitAllAndPushAsync: `git commit` exited {Code} on branch {Branch}: {Err}",
                    commitCode, branch, commitErr);
                return false;
            }

            // Push. PushWithAutoRecoverAsync handles the typical fast-forward
            // failure modes (non-fast-forward → fetch + merge → re-push).
            try
            {
                await this.PushWithAutoRecoverAsync(worktreePath, branch, ct);
            }
            catch (Exception pushEx) when (pushEx is not OperationCanceledException)
            {
                this.logger?.LogWarning(pushEx,
                    "CommitAllAndPushAsync: push failed on branch {Branch}; commit is local-only and the next iter's EnsureWorktreeAsync `git reset --hard origin/{Branch}` will discard it. Reporting false so the caller does NOT emit task.iter-commit.pushed (which would lie about durability) — see Phase 52 stuck-workstream fix 2026-05-28.",
                    branch, branch);
                // 2026-05-28: was `return true` with the rationale "still
                // report true so the caller knows progress was made
                // locally". That rationale was wrong — the local commit
                // is destroyed by the next iter's `git reset --hard
                // origin/<branch>` (see EnsureWorktreeAsync), so
                // reporting success is actively misleading. The
                // strategy caller at CrewStoryArtifactStrategy:7015
                // emits task.iter-commit.pushed on the truthy return
                // — that event has been lying about every iter where
                // push failed, masking the divergence until the
                // eventual non-swallowed push call dies at
                // PushBranchAsync / MergeUpstreamIntoBranchAsync /
                // CommitFilesAsync. Observed root cause for
                // code-intelligence:CLEAN-CODE stage 9.1 burning
                // ~$4,500 across SD2+E9 pair rotations before operator
                // unstick.
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex,
                "CommitAllAndPushAsync: unexpected failure at {Path}; continuing with iter.",
                worktreePath);
            return false;
        }
    }

    public async Task<bool> BranchHasChangesVsBaseAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        // Refresh both refs from origin — CommitFilesAsync / MergeUpstreamIntoBranchAsync
        // already pushed the branch, so origin reflects the committed state.
        await this.RunGitCaptureAsync(root, ct, "fetch", "origin", baseBranch, branch);
        // `git diff --quiet A...B` exits 0 when there is NO diff from the
        // merge-base of A,B to B, and 1 when there IS a diff. Any other code
        // (e.g. 128 for a missing ref) is treated as "assume changes" so a
        // transient failure never suppresses a legitimate PR.
        var (code, _, _) = await this.RunGitCaptureAsync(
            root, ct, "diff", "--quiet", $"origin/{baseBranch}...origin/{branch}");
        return code != 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implementation note — uses <c>git merge-base --is-ancestor A B</c>
    /// (exit 0 ⇒ A is an ancestor of B). Fails CLOSED: any non-zero exit
    /// (including 1 for "not ancestor", 128 for "missing ref", or a
    /// transient git failure) returns <c>false</c>. The asymmetry with
    /// <see cref="BranchHasChangesVsBaseAsync"/> (which fails OPEN) is
    /// intentional — a stuck "already-merged" probe should fall through to
    /// the empty-PR exception so genuine empty PRs still surface to the
    /// operator instead of being silently swallowed.
    /// </remarks>
    public async Task<bool> IsBranchAlreadyMergedIntoBaseAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        try
        {
            // Refresh both refs from origin so the ancestry probe sees the
            // latest tip the upstream merge button moved base to.
            await this.RunGitCaptureAsync(root, ct, "fetch", "origin", baseBranch, branch);
            var (code, _, _) = await this.RunGitCaptureAsync(
                root, ct, "merge-base", "--is-ancestor",
                $"origin/{branch}", $"origin/{baseBranch}");
            return code == 0;
        }
        catch
        {
            // Fail-closed: a transient probe failure must not bypass the
            // empty-PR refusal path. Caller will treat this as "not
            // already merged" and surface the EmptyPrMergeRefusedException
            // so the operator sees the issue.
            return false;
        }
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
    /// <para>
    /// Phase 79b (2026-05-18) — a worktree paused mid-rebase reports as
    /// <c>detached</c> in porcelain output, hiding the branch attribution,
    /// even though git itself still owns the branch via the per-worktree
    /// <c>rebase-merge/head-name</c> (or legacy <c>rebase-apply/head-name</c>)
    /// state file. Without recognising that, callers fell back to a
    /// sanitised-branch slug and then collided with "branch already used by
    /// worktree at …" from <c>git checkout -B</c>, producing the misleading
    /// "no worktree is registered" error the operator saw on
    /// code-intelligence:AGENT-MEMORY stages 7.2 / 7.4. After the porcelain
    /// scan misses, we therefore probe the rebase state files directly.
    /// </para>
    /// </summary>
    internal async Task<string?> TryFindExistingWorktreeAsync(string root, string branch, CancellationToken ct)
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

        // Phase 79b fallback — scan per-worktree rebase state for the branch.
        return await this.TryFindWorktreeFromRebaseStateAsync(root, branch, ct);
    }

    /// <summary>
    /// Phase 79b — locate a worktree that is paused mid-rebase on
    /// <paramref name="branch"/>. <c>git worktree list --porcelain</c> emits
    /// a worktree as <c>detached</c> while a rebase is in flight, so the
    /// porcelain-only scan in <see cref="TryFindExistingWorktreeAsync"/>
    /// returns null even though git refuses any other worktree's
    /// <c>checkout -B {branch}</c>. We instead read the per-worktree
    /// metadata under <c>$GIT_COMMON_DIR/worktrees/{slug}/</c>: a rebase in
    /// progress writes <c>rebase-merge/head-name</c> (default since git
    /// 2.27) or <c>rebase-apply/head-name</c> (legacy <c>--apply</c>
    /// backend), each containing a single line <c>refs/heads/{branch}\n</c>.
    /// The worktree's working-directory path is resolved via the <c>gitdir</c>
    /// pointer file in the same metadata directory.
    /// </summary>
    private async Task<string?> TryFindWorktreeFromRebaseStateAsync(
        string root, string branch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(root)) return null;

        // Resolve the worktrees metadata directory via git so this stays
        // correct when `root` is itself a linked worktree.
        var (rcCode, rcStdout, _) = await this.RunGitCaptureAsync(
            root, ct, "rev-parse", "--git-common-dir");
        if (rcCode != 0 || string.IsNullOrWhiteSpace(rcStdout)) return null;

        var gitCommonDir = rcStdout.Trim();
        if (!Path.IsPathRooted(gitCommonDir))
        {
            gitCommonDir = Path.GetFullPath(Path.Combine(root, gitCommonDir));
        }
        var worktreesDir = Path.Combine(gitCommonDir, "worktrees");
        if (!Directory.Exists(worktreesDir)) return null;

        var wantedHeadName = "refs/heads/" + branch;

        IEnumerable<string> slugDirs;
        try { slugDirs = Directory.EnumerateDirectories(worktreesDir); }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex,
                "Phase 79b: could not enumerate worktrees dir '{Dir}'.", worktreesDir);
            return null;
        }

        foreach (var slugDir in slugDirs)
        {
            ct.ThrowIfCancellationRequested();

            string? matchedHeadNamePath = null;
            foreach (var rel in new[] { "rebase-merge/head-name", "rebase-apply/head-name" })
            {
                var p = Path.Combine(slugDir,
                    rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) continue;

                string headName;
                try { headName = (await File.ReadAllTextAsync(p, ct)).Trim(); }
                catch (Exception ex)
                {
                    this.logger?.LogDebug(ex,
                        "Phase 79b: could not read rebase head-name '{Path}'.", p);
                    continue;
                }

                if (string.Equals(headName, wantedHeadName, StringComparison.Ordinal))
                {
                    matchedHeadNamePath = p;
                    break;
                }
            }

            if (matchedHeadNamePath is null) continue;

            var gitdirFile = Path.Combine(slugDir, "gitdir");
            if (!File.Exists(gitdirFile)) continue;

            string gitdir;
            try { gitdir = (await File.ReadAllTextAsync(gitdirFile, ct)).Trim(); }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex,
                    "Phase 79b: could not read gitdir file '{Path}'.", gitdirFile);
                continue;
            }

            // `gitdir` is "{wtPath}/.git" (or "{wtPath}\\.git" on Windows
            // from older git versions). The worktree path is its parent.
            var wtPath = Path.GetDirectoryName(gitdir);
            if (string.IsNullOrEmpty(wtPath)) continue;

            this.logger?.LogInformation(
                "Phase 79b: branch '{Branch}' located via mid-rebase state at worktree '{WtPath}' (slug '{Slug}').",
                branch, wtPath, Path.GetFileName(slugDir));
            return wtPath;
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
                // `git worktree remove --force` deletes the tracked files AND the
                // git registration, but leaves locked gitignored subtrees behind:
                // the agent's `.forge\memory\session-state` (held by copilot.exe)
                // and `out\` build/test output (held by an orphaned testhost/
                // dotnet from the build gate). A naive Directory.Delete throws on
                // those — and because the registration is ALREADY gone, that
                // strands an unrecoverable orphan: `git worktree prune` finds no
                // dangling entry, a later RemoveWorktreeAsync early-returns
                // ("nothing registered"), and WorktreeReaper only scans
                // registered worktrees. Use the lock-breaking deleter (retries +
                // Restart Manager) so the locked dirs actually go; anything it
                // still can't clear is collected by WorktreeReaper's on-disk
                // orphan sweep once the lock releases.
                await this.ForceDeleteWorktreeDirAsync(existing, ct);
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
    public async Task<bool> TryRemoveOrphanWorktreeDirAsync(
        RepoRow? repo, string worktreeDir, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(worktreeDir)) return false;
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return false;
        if (!Directory.Exists(repo.CloneRoot)) return false;

        // Safety: only ever touch a leaf directly under `{cloneRoot}\.worktree\`,
        // and NEVER the cloneRoot / main worktree. Mirrors the Phase 55 guard in
        // RemoveWorktreeAsync that stopped a base-branch cleanup from wiping the
        // operator's clone (`.git` and all).
        var wtRoot = Path.Combine(repo.CloneRoot, ".worktree");
        var parent = Path.GetDirectoryName(
            worktreeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null || !IsSamePath(parent, wtRoot)) return false;
        if (IsSamePath(worktreeDir, repo.CloneRoot) || IsMainWorktreeDir(worktreeDir)) return false;
        if (!Directory.Exists(worktreeDir)) return true;

        // A still-git-attached worktree (`.git` file pointer or dir) must go
        // through the branch-keyed RemoveWorktreeAsync path, not this one — the
        // orphan sweep is strictly for directories git has already forgotten.
        if (File.Exists(Path.Combine(worktreeDir, ".git"))
            || Directory.Exists(Path.Combine(worktreeDir, ".git")))
        {
            return false;
        }

        var gate = branchLocks.GetOrAdd(NormalizeAbsolutePath(worktreeDir), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // remove+prune first in case a stale registration somehow lingers
            // (typically a no-op for a truly orphaned dir), then force-delete the
            // locked remnant subtree with the Restart-Manager lock-breaker.
            await this.RunGitCaptureAsync(repo.CloneRoot, ct, "worktree", "remove", "--force", worktreeDir);
            await this.ForceDeleteWorktreeDirAsync(worktreeDir, ct);
            await this.RunGitCaptureAsync(repo.CloneRoot, ct, "worktree", "prune");
            return !Directory.Exists(worktreeDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "TryRemoveOrphanWorktreeDirAsync: failed to remove orphan worktree dir {Path} (repo {Repo}); will retry next tick.",
                worktreeDir, repo.Id);
            return !Directory.Exists(worktreeDir);
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

            // Phase 81 — preflight: fail loudly when the path is excluded by
            // .gitignore (e.g. OneBranch allowlists). Without this, `git add`
            // silently succeeds, `git status --porcelain` returns empty, and
            // the per-iter commit returns "" — turning every iteration into a
            // no-op commit that pushes nothing.
            await this.AssertPathsNotIgnoredAsync(wtPath, branch, new[] { repoRelativePath }, ct);

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

    /// <inheritdoc />
    public async Task AssertPlanDocPathsNotIgnoredAsync(
        RepoRow? repo, string branch, IEnumerable<string> repoRelativePaths, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (string.IsNullOrEmpty(wtPath))
        {
            throw new InvalidOperationException(
                $"AssertPlanDocPathsNotIgnoredAsync: no worktree registered for branch '{branch}'. "
                + "Callers must materialise the worktree (via EnsureWorktreeAsync) first.");
        }
        await this.AssertPathsNotIgnoredAsync(wtPath, branch, repoRelativePaths, ct);
    }

    /// <inheritdoc />
    public async Task EnsurePlanDocPathsCommittableAsync(
        RepoRow? repo,
        string branch,
        IEnumerable<string> repoRelativePaths,
        string planDocBaseDir,
        CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (string.IsNullOrEmpty(wtPath))
        {
            throw new InvalidOperationException(
                $"EnsurePlanDocPathsCommittableAsync: no worktree registered for branch '{branch}'. "
                + "Callers must materialise the worktree (via EnsureWorktreeAsync) first.");
        }

        var pathList = (repoRelativePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (pathList.Length == 0) return;

        // Validate planDocBaseDir up front — fail clearly on unsafe input
        // BEFORE running git so the operator sees a config error, not a
        // confusing git error.
        _ = ComputeGitignoreNarrowExceptions(planDocBaseDir);

        // Happy path: preflight already passes (either the repo's .gitignore
        // doesn't block the path, or a prior run already landed the auto-fix
        // exception on this branch). Either way nothing to do.
        try
        {
            await this.AssertPathsNotIgnoredAsync(wtPath, branch, pathList, ct);
            return;
        }
        catch (GitIgnoredPathException firstFailure)
        {
            this.logger?.LogWarning(
                "Plan-doc path(s) ignored on branch '{Branch}'; attempting .gitignore narrow auto-fix for base '{Base}'. Paths: {Paths}",
                branch, planDocBaseDir, string.Join(", ", firstFailure.IgnoredPaths));

            // Serialise per-branch so a concurrent commit on the same branch
            // doesn't race the .gitignore write or our prep commit.
            var gate = branchLocks.GetOrAdd(branch, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Recheck under the lock — another caller may have just
                // landed the fix while we were waiting.
                try
                {
                    await this.AssertPathsNotIgnoredAsync(wtPath, branch, pathList, ct);
                    return;
                }
                catch (GitIgnoredPathException)
                {
                    // Fall through to apply the fix.
                }

                await this.ApplyGitignoreNarrowFixAsync(wtPath, branch, planDocBaseDir, ct);
            }
            finally
            {
                gate.Release();
            }

            // Re-verify outside the lock — if the path is STILL ignored, the
            // root .gitignore wasn't the blocker (likely a nested .gitignore
            // under docs/ or similar). Re-throw with operator guidance.
            try
            {
                await this.AssertPathsNotIgnoredAsync(wtPath, branch, pathList, ct);
            }
            catch (GitIgnoredPathException stillIgnored)
            {
                throw new GitIgnoredPathException(
                    stillIgnored.Branch,
                    stillIgnored.IgnoredPaths,
                    stillIgnored.GitCheckIgnoreOutput.TrimEnd()
                    + "\n\nForge appended a narrow allowlist exception to the root .gitignore but the path is STILL ignored. "
                    + "This usually means a NESTED .gitignore (e.g. docs/.gitignore) is blocking the path with higher precedence. "
                    + "Operator must either remove the nested ignore rule or set repo.PlanDocBaseDir to a path that no .gitignore (root OR nested) blocks.");
            }
        }
    }

    /// <summary>
    /// Phase 86 — generalised auto-fix for any commit base directory (not
    /// just plan docs). Delegates to <see cref="EnsurePlanDocPathsCommittableAsync"/>
    /// because the underlying narrow-allowlist logic is content-agnostic:
    /// the caller picks <paramref name="baseDir"/>, the helper computes the
    /// minimal <c>!/{baseDir}/</c> exception chain (via
    /// <see cref="ComputeGitignoreNarrowExceptions"/>), appends it as a prep
    /// commit on <paramref name="branch"/>, and re-verifies.
    ///
    /// <para>Worktree-required: caller must invoke
    /// <see cref="EnsureWorktreeAsync"/> first so <c>git check-ignore</c>
    /// has a working directory.</para>
    /// </summary>
    public Task EnsurePathsCommittableAsync(
        RepoRow? repo,
        string branch,
        IEnumerable<string> repoRelativePaths,
        string baseDir,
        CancellationToken ct = default)
        => this.EnsurePlanDocPathsCommittableAsync(repo, branch, repoRelativePaths, baseDir, ct);

    /// <summary>
    /// Phase 85 — build the minimal narrow allowlist exception chain that
    /// un-ignores <paramref name="planDocBaseDir"/> under a root-level
    /// catch-all ignore (e.g. OneBranch's <c>/*/</c>). Each ancestor
    /// directory needs both <c>!/x/</c> (un-ignore the dir itself) and
    /// <c>/x/*</c> (re-ignore its other immediate children so the un-ignore
    /// footprint stays minimal); the leaf gets only the un-ignore.
    ///
    /// <para>Example for <c>docs/stories</c>:</para>
    /// <code>
    /// !/docs/
    /// /docs/*
    /// !/docs/stories/
    /// </code>
    ///
    /// <para>Strict validation: each segment must be
    /// <c>[A-Za-z0-9._-]+</c> — gitignore metacharacters (<c>*</c>, <c>!</c>,
    /// <c>#</c>, <c>[</c>, etc.) and whitespace are rejected so an
    /// operator-supplied <see cref="RepoRow.PlanDocBaseDir"/> cannot produce
    /// malformed rules.</para>
    /// </summary>
    internal static IReadOnlyList<string> ComputeGitignoreNarrowExceptions(string planDocBaseDir)
    {
        if (string.IsNullOrWhiteSpace(planDocBaseDir))
        {
            throw new ArgumentException("planDocBaseDir is required.", nameof(planDocBaseDir));
        }

        var normalized = planDocBaseDir.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException(
                "planDocBaseDir must contain at least one path segment.", nameof(planDocBaseDir));
        }

        foreach (var part in parts)
        {
            if (!SafeBaseDirSegment.IsMatch(part) || part == "." || part == "..")
            {
                throw new ArgumentException(
                    $"planDocBaseDir segment '{part}' is unsafe; only [A-Za-z0-9._-] segments other than '.' and '..' allowed.",
                    nameof(planDocBaseDir));
            }
        }

        var rules = new List<string>(capacity: parts.Length * 2);
        var current = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            current = current + "/" + parts[i];
            rules.Add("!" + current + "/");
            if (i < parts.Length - 1)
            {
                rules.Add(current + "/*");
            }
        }
        return rules;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeBaseDirSegment
        = new(@"^[A-Za-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string GitignoreAutoFixMarker = "# Forge plan-doc allowlist (auto-fix)";

    /// <summary>
    /// Phase 85 — append a Forge-owned narrow allowlist block to the root
    /// <c>.gitignore</c> on <paramref name="branch"/>, commit it as a single
    /// prep commit, and push to <c>origin</c>. The append is unconditional
    /// (control flow already established that the preflight failed), so
    /// concurrent / repeated invocations on the same branch are bounded by
    /// the per-branch lock in the caller — they only happen once per branch
    /// per orchestrator tick.
    /// </summary>
    private async Task ApplyGitignoreNarrowFixAsync(
        string wtPath, string branch, string planDocBaseDir, CancellationToken ct)
    {
        var gitignorePath = Path.Combine(wtPath, ".gitignore");
        var rules = ComputeGitignoreNarrowExceptions(planDocBaseDir);

        var existing = File.Exists(gitignorePath)
            ? await File.ReadAllTextAsync(gitignorePath, ct)
            : string.Empty;

        var sb = new StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal))
        {
            sb.Append('\n');
        }
        sb.Append('\n');
        sb.Append(GitignoreAutoFixMarker).Append('\n');
        foreach (var rule in rules)
        {
            sb.Append(rule).Append('\n');
        }

        await File.WriteAllTextAsync(gitignorePath, sb.ToString(), new UTF8Encoding(false), ct);

        await this.GitAsync(wtPath, ct, "add", "--", ".gitignore");

        // Safety: confirm something actually got staged. A no-op stage means
        // the .gitignore was already at the target content (e.g. via an
        // out-of-band edit that we then re-wrote identically) — surfacing as
        // "nothing to commit" rather than throwing.
        var (stagedCode, _, _) = await this.RunGitCaptureAsync(wtPath, ct, "diff", "--cached", "--quiet");
        if (stagedCode == 0)
        {
            this.logger?.LogWarning(
                ".gitignore auto-fix on branch '{Branch}' produced no staged change; nothing to commit. " +
                "Re-verification will determine whether the path is now committable.", branch);
            return;
        }

        await this.GitAsync(wtPath, ct,
            "-c", "user.email=forge-architect@local",
            "-c", "user.name=Forge Architect",
            "commit", "-m",
            $"chore: allow {planDocBaseDir}/ in .gitignore (Forge auto-fix)",
            "--", ".gitignore");

        this.logger?.LogInformation(
            "Applied .gitignore auto-fix on branch '{Branch}' for plan-doc base '{Base}': appended {Count} rule(s) and committed.",
            branch, planDocBaseDir, rules.Count);

        await this.PushWithAutoRecoverAsync(wtPath, branch, ct);
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
            // in the push (e.g. a generator interrupted mid-write). Route
            // through StageAllAndScrubAsync so build artifacts (__pycache__,
            // node_modules, target, …) are scrubbed here too — the old path
            // scrubbed only .forge/ and so re-committed artifacts that
            // CommitFilesAsync had already removed (the flip-flop leak).
            await this.StageAllAndScrubAsync(wtPath, ct);
            // Commit only when something is actually STAGED. `git status
            // --porcelain` also reports untracked / scrubbed working-tree
            // entries (e.g. a leftover __pycache__/ the scrub just un-staged),
            // which would make `git commit` fail with "nothing added to
            // commit" — so gate on the staged index instead (mirrors
            // CommitFilesAsync).
            var (stagedCode, _, _) = await this.RunGitCaptureAsync(
                wtPath, ct, "diff", "--cached", "--quiet");
            if (stagedCode != 0)
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
    /// <item><c>git diff --name-only origin/{ref}...HEAD</c> — files
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
        //
        // 2026-06-03 — use the THREE-DOT range `{ref}...HEAD` (== diff
        // from merge-base(ref, HEAD) to HEAD), NOT the two-dot `{ref}..HEAD`.
        // For `git diff`, two-dot `A..B` is identical to `git diff A B`,
        // which reports EVERY difference between the tips — including the
        // inverse of any commits that landed on the base AFTER this
        // worktree branched. On a story where many sibling workstreams
        // merge concurrently into a shared base (e.g. feature/PR-Ingester),
        // those sibling files contaminate the changed-file set, inflating
        // it from the engineer's real contribution to a superset spanning
        // the whole story. The evaluator then scores against phantom files
        // it can't attribute (scope mismatch) and caps just under pass.
        // Observed 2026-06-03 on code-intelligence:PR-INGESTER stage 5.3 /
        // 5.7: two-dot reported 115 files, three-dot 16 (the true diff).
        // Three-dot matches what GitHub's PR "Files changed" view shows
        // (`base...head`), so it is the correct frame for BOTH the
        // evaluator ground-truth and the post-pass commit decision. When
        // the base hasn't advanced since branching, merge-base == base, so
        // three-dot collapses to two-dot and nothing changes for the common
        // case.
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
            worktreePath, ct, "diff", "--name-only", "-z", $"{refToDiffAgainst}...HEAD");
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
    public async Task<IReadOnlyList<string>> GetChangedFilesFromMergedPrAsync(
        RepoRow? repo,
        string worktreePath,
        string? mergeCommitSha,
        string? mergeBaseSha = null,
        string? mergeHeadSha = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            return Array.Empty<string>();
        }

        // Preferred path: provider-captured source/target tips from merge
        // time. This covers merge commits, squash merges, and rebase/FF
        // merges where "parent..mergeCommit" would only show the last PR
        // commit.
        var rangePaths = await this.DiffResolvedRangeAsync(
            worktreePath, mergeBaseSha, mergeHeadSha, ct);
        if (rangePaths.Count > 0)
        {
            return rangePaths;
        }

        // Fallback: diff the merge/squash commit against its first parent.
        // If the provider did not persist a SHA yet, HEAD is still useful for
        // the common recovery case where the worktree is parked directly on
        // the base-branch merge commit.
        var commit = await this.ResolveCommitishAsync(worktreePath, mergeCommitSha, ct);
        if (string.IsNullOrEmpty(commit) && string.IsNullOrWhiteSpace(mergeCommitSha))
        {
            commit = await this.ResolveCommitishAsync(worktreePath, "HEAD", ct);
        }
        if (string.IsNullOrEmpty(commit))
        {
            return Array.Empty<string>();
        }

        var (parentsCode, parentsOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "rev-list", "--parents", "-n", "1", commit);
        if (parentsCode != 0 || string.IsNullOrWhiteSpace(parentsOut))
        {
            return Array.Empty<string>();
        }

        var parts = parentsOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return Array.Empty<string>();
        }

        return await this.DiffNameOnlyAsync(worktreePath, parts[1], parts[0], ct);
    }

    private async Task<IReadOnlyList<string>> DiffResolvedRangeAsync(
        string worktreePath,
        string? baseSha,
        string? headSha,
        CancellationToken ct)
    {
        var baseCommit = await this.ResolveCommitishAsync(worktreePath, baseSha, ct);
        var headCommit = await this.ResolveCommitishAsync(worktreePath, headSha, ct);
        if (string.IsNullOrEmpty(baseCommit) || string.IsNullOrEmpty(headCommit)
            || string.Equals(baseCommit, headCommit, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        return await this.DiffNameOnlyAsync(worktreePath, baseCommit, headCommit, ct);
    }

    private async Task<string?> ResolveCommitishAsync(
        string worktreePath,
        string? commitish,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commitish))
        {
            return null;
        }

        var trimmed = commitish.Trim();
        var (code, stdout, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "rev-parse", "--verify", $"{trimmed}^{{commit}}");
        if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return stdout.Trim();
        }

        if (LooksLikeSha(trimmed))
        {
            await this.RunGitCaptureAsync(worktreePath, ct, "fetch", "--no-tags", "origin", trimmed);
            (code, stdout, _) = await this.RunGitCaptureAsync(
                worktreePath, ct, "rev-parse", "--verify", $"{trimmed}^{{commit}}");
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout.Trim();
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> DiffNameOnlyAsync(
        string worktreePath,
        string baseCommit,
        string headCommit,
        CancellationToken ct)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (diffCode, diffOut, _) = await this.RunGitCaptureAsync(
            worktreePath, ct, "diff", "--name-only", "--diff-filter=ACMRT", "-z", $"{baseCommit}..{headCommit}");
        if (diffCode != 0 || string.IsNullOrEmpty(diffOut))
        {
            return ordered;
        }

        foreach (var entry in diffOut.Split('\0'))
        {
            if (string.IsNullOrEmpty(entry)) continue;
            if (entry.Contains("..", StringComparison.Ordinal)) continue;
            var norm = entry.Replace('\\', '/');
            if (IsBuildArtifactPath(norm)) continue;
            if (seen.Add(norm)) ordered.Add(norm);
        }

        return ordered;
    }

    private static bool LooksLikeSha(string value)
    {
        if (value.Length is < 7 or > 64) return false;
        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch)) return false;
        }
        return true;
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
    /// generated/cached. Delegates to <see cref="BuildArtifactPaths.IsBuildArtifact"/>
    /// (the single source of truth shared with
    /// <c>CrewStoryArtifactStrategy.ReadAllFiles</c> and the index scrub) so
    /// the filter list can't drift between the producer and the committer.
    /// </summary>
    private static bool IsBuildArtifactPath(string repoRelativePath) =>
        BuildArtifactPaths.IsBuildArtifact(repoRelativePath);

    // ── Phase 79 — agent-driven merge-conflict resolver ──────────────────
    //
    // Phase 79c (2026-05-18): the resolver runs `git merge origin/{base}`
    // in the workstream worktree (NOT a rebase). The merge approach
    // preserves the ws branch's commit history and does not require a
    // force-push to origin, so reviewer comments stay anchored to the
    // lines they were posted against. The old rebase implementation
    // routinely orphaned late-arriving review comments after the
    // resolver's force-push rewrote the branch tip.

    /// <inheritdoc/>
    public async Task<MergeResult> MergeBaseIntoBranchAndDetectConflictsAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var existingWtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        var wtPath = !string.IsNullOrEmpty(existingWtPath)
            ? existingWtPath
            : WorktreePath(root, SanitizeBranchSlug(branch));
        await this.EnsureWorktreeAsync(root, wtPath, branch, branch, ct);

        // 1. Abort any leftover merge from a previous attempt so we start clean.
        await this.AbortMergeAsync(repo, branch, ct);

        // 2. Refresh both refs.
        await this.GitAsync(wtPath, ct, "fetch", "origin", baseBranch);
        var (lsBranchCode, lsBranchOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "ls-remote", "--heads", "origin", branch);
        if (lsBranchCode == 0 && !string.IsNullOrWhiteSpace(lsBranchOut))
        {
            await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
            await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{branch}");
        }

        // 3. Attempt the merge. GIT_EDITOR=true so a merge commit doesn't
        //    drop into an interactive editor for the default merge message.
        //    Capture stderr — git uses non-zero exit for both "conflict
        //    needs resolution" and "fatal: cannot merge".
        var (rcCode, _, rcErr) = await this.RunGitCaptureWithEnvAsync(
            wtPath, ct,
            envOverride: new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" },
            args: new[] { "merge", "--no-ff", "--no-edit", $"origin/{baseBranch}" });
        if (rcCode == 0)
        {
            this.logger?.LogInformation(
                "Phase 79c: merge of origin/{BaseBranch} into {Branch} clean (no conflicts).",
                baseBranch, branch);
            return new MergeResult(MergeState.Clean, Array.Empty<ConflictedFile>(), null);
        }

        // 4. Determine whether the non-zero is a conflict or a hard fail.
        //    git-status unmerged entries (XY ∈ {DD, AU, UD, UA, DU, AA, UU})
        //    mean we are paused on a conflict; otherwise abort + return
        //    MergeFailed.
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
            // Hard merge failure (dirty working tree, fatal error, etc.)
            // Abort + bubble the stderr.
            await this.AbortMergeAsync(repo, branch, ct);
            return new MergeResult(MergeState.MergeFailed, Array.Empty<ConflictedFile>(),
                $"git merge exit={rcCode}: {rcErr?.Trim()}");
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
                    "Phase 79c: could not read conflicted file '{Path}' from worktree '{Wt}'; substituting empty content.",
                    rel, wtPath);
                content = string.Empty;
            }
            files.Add(new ConflictedFile(rel, content));
        }
        this.logger?.LogInformation(
            "Phase 79c: merge of origin/{BaseBranch} into {Branch} paused on {N} conflicted file(s).",
            baseBranch, branch, files.Count);
        return new MergeResult(MergeState.Conflicted, files, null);
    }

    /// <inheritdoc/>
    public async Task<MergeResult> ContinueMergeAndPushAsync(
        RepoRow? repo,
        string branch,
        IReadOnlyDictionary<string, string> resolvedFiles,
        CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct)
            ?? throw new InvalidOperationException(
                $"Phase 79c: ContinueMergeAndPushAsync called for branch '{branch}' but no worktree is registered. MergeBaseIntoBranchAndDetectConflictsAsync was expected to run first.");

        // 1a. Defense-in-depth: if any resolved file still contains
        //     conflict markers, refuse to stage. Git happily commits any
        //     content, so without this guard a buggy resolver could land
        //     markers in the merge commit. The strategy layer already
        //     attempts an `ours`-side fallback, but the git ops layer is
        //     the last line of defense.
        var leftoverMarkers = new List<ConflictedFile>();
        foreach (var kv in resolvedFiles)
        {
            if (kv.Value.Contains("<<<<<<<", StringComparison.Ordinal)
                || kv.Value.Contains(">>>>>>>", StringComparison.Ordinal))
            {
                leftoverMarkers.Add(new ConflictedFile(kv.Key, kv.Value));
            }
        }
        if (leftoverMarkers.Count > 0)
        {
            this.logger?.LogWarning(
                "Phase 79c: caller passed {N} resolved file(s) with conflict markers still in place for {Branch}; refusing to commit.",
                leftoverMarkers.Count, branch);
            return new MergeResult(MergeState.Conflicted, leftoverMarkers, null);
        }

        // 1b. Write resolved files + stage them.
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

        // 2. If a merge is in progress (MERGE_HEAD present), commit it to
        //    finalize. If not (caller passed an empty dict after a Clean
        //    result from the detect step), skip straight to push.
        //    GIT_EDITOR=true keeps the merge commit message non-interactive.
        var mergeHeadPath = System.IO.Path.Combine(wtPath, ".git", "MERGE_HEAD");
        // For linked worktrees, .git is a file pointing at the metadata dir;
        // resolve via rev-parse so the probe is correct in both shapes.
        var (gdCode, gdOut, _) = await this.RunGitCaptureAsync(
            wtPath, ct, "rev-parse", "--git-dir");
        if (gdCode == 0 && !string.IsNullOrWhiteSpace(gdOut))
        {
            var gitDir = gdOut.Trim();
            if (!System.IO.Path.IsPathRooted(gitDir))
            {
                gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(wtPath, gitDir));
            }
            mergeHeadPath = System.IO.Path.Combine(gitDir, "MERGE_HEAD");
        }
        var mergeInFlight = System.IO.File.Exists(mergeHeadPath);

        if (mergeInFlight)
        {
            var (ciCode, _, ciErr) = await this.RunGitCaptureWithEnvAsync(
                wtPath, ct,
                envOverride: new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" },
                args: new[] { "commit", "--no-edit" });
            if (ciCode != 0)
            {
                // Likely cause: resolver left conflict markers in place,
                // so commit refuses. Re-inspect git status to surface the
                // still-conflicted set to the caller.
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
                    await this.AbortMergeAsync(repo, branch, ct);
                    return new MergeResult(MergeState.MergeFailed, Array.Empty<ConflictedFile>(),
                        $"git commit (merge) exit={ciCode}: {ciErr?.Trim()}");
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
                    "Phase 79c: resolver left {N} file(s) still conflicted for {Branch}; returning to caller.",
                    files.Count, branch);
                return new MergeResult(MergeState.Conflicted, files, null);
            }
        }

        // 3. Push the merged tip. Plain push — no --force. If origin moved
        //    (concurrent operator push), the push fails and we surface
        //    MergeFailed so the caller can stall instead of clobbering.
        var (pushCode, _, pushErr) = await this.RunGitCaptureAsync(
            wtPath, ct, "push", "origin", branch);
        if (pushCode != 0)
        {
            return new MergeResult(MergeState.MergeFailed, Array.Empty<ConflictedFile>(),
                $"git push exit={pushCode}: {pushErr?.Trim()}");
        }

        this.logger?.LogInformation(
            "Phase 79c: merge of {Branch} committed + pushed; {N} file(s) resolved by the agent.",
            branch, resolvedFiles.Count);
        return new MergeResult(MergeState.Clean, Array.Empty<ConflictedFile>(), null);
    }

    /// <inheritdoc/>
    public async Task AbortMergeAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var wtPath = await this.TryFindExistingWorktreeAsync(root, branch, ct);
        if (string.IsNullOrEmpty(wtPath)) return;
        // Idempotent: `git merge --abort` no-ops with exit 128 when no
        // merge is in progress; capture + ignore.
        await this.RunGitCaptureAsync(wtPath, ct, "merge", "--abort");
    }

    /// <summary>
    /// Like <see cref="RunGitCaptureAsync"/> but with an environment-variable
    /// override. Used by Phase 79c to set <c>GIT_EDITOR=true</c> so
    /// <c>git merge</c> / <c>git commit</c> don't drop into the editor
    /// for the default merge commit message.
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
        // Apply the shared git policy (prompt fail-fast, ADO oauth, and the
        // Forge-private GIT_CONFIG_GLOBAL that avoids ~/.gitconfig contention)
        // FIRST, then layer the caller's explicit overrides (e.g. GIT_EDITOR)
        // on top so they still win. Without this, merge/commit ran without the
        // policy and could hit the shared-config "Permission denied" failure.
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
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
    /// Scrubs build-output directories (<c>obj/</c>, <c>bin/Debug/</c>,
    /// <c>node_modules/</c>, <c>target/</c>, …) from the git index — at any
    /// depth — so a blanket <c>git add -A</c> never lands generated binaries
    /// in a PR. Uses <c>--ignore-unmatch</c> so it's a safe no-op when the
    /// tree is already clean. Best-effort: failures are swallowed. See
    /// <see cref="BuildArtifactPaths.IndexScrubPathspecs"/> for the
    /// (conservative, never-legitimately-tracked) path set.
    /// </summary>
    public async Task ScrubBuildArtifactsFromIndexAsync(string wtPath, CancellationToken ct)
    {
        try
        {
            var args = new List<string> { "rm", "-r", "--cached", "--ignore-unmatch", "--" };
            args.AddRange(BuildArtifactPaths.IndexScrubPathspecs());
            await this.RunGitCaptureAsync(wtPath, ct, args.ToArray());
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The ONE blanket-staging chokepoint: <c>git add -A</c> followed by
    /// BOTH index scrubs (<see cref="ScrubForgeScratchFromIndexAsync"/> +
    /// <see cref="ScrubBuildArtifactsFromIndexAsync"/>). Every commit path
    /// that stages the whole worktree must go through here so a generated
    /// artifact (a Python sidecar's <c>__pycache__/*.pyc</c>, a `node_modules/`,
    /// a Rust `target/`, …) can never reach a commit.
    ///
    /// <para>This closes the leak that produced the add→remove "flip-flop"
    /// seen on code-intelligence:CLEAN-CODE qa-e2e: <see cref="CommitFilesAsync"/>
    /// scrubbed build artifacts, but the separate <c>git add -A</c> in
    /// <see cref="PushBranchAsync"/> scrubbed only <c>.forge/</c>, so it
    /// re-staged + committed the monorepo's untracked
    /// <c>services/agent-memory/cmd/reranker-sidecar/__pycache__/</c> bytecode.
    /// Routing both sites through this helper keeps the scrub set in lockstep
    /// and prevents the same drift from reappearing in any future
    /// stage-all path.</para>
    /// </summary>
    private async Task StageAllAndScrubAsync(string wtPath, CancellationToken ct)
    {
        await this.GitAsync(wtPath, ct, "add", "-A");
        await this.ScrubForgeScratchFromIndexAsync(wtPath, ct);
        await this.ScrubBuildArtifactsFromIndexAsync(wtPath, ct);
    }

    /// <summary>
    /// Removes Forge scratch files from <c>.forge/</c> while preserving
    /// <c>.forge/memory/</c>, which is the worktree-local Copilot session
    /// store. Best-effort: locked files are logged at debug level and left
    /// for the next cleanup pass.
    /// </summary>
    private void DeleteForgeScratchExceptMemory(string wtPath)
    {
        var forgeDir = Path.Combine(wtPath, ".forge");
        if (!Directory.Exists(forgeDir)) return;

        try
        {
            var dir = new DirectoryInfo(forgeDir);
            foreach (var file in dir.EnumerateFiles())
            {
                try { file.Delete(); }
                catch (Exception ex)
                {
                    this.logger?.LogDebug(ex,
                        "DeleteForgeScratchExceptMemory: could not delete Forge scratch file {Path}.",
                        file.FullName);
                }
            }

            foreach (var child in dir.EnumerateDirectories())
            {
                if (string.Equals(child.Name, "memory", StringComparison.OrdinalIgnoreCase)) continue;
                try { child.Delete(recursive: true); }
                catch (Exception ex)
                {
                    this.logger?.LogDebug(ex,
                        "DeleteForgeScratchExceptMemory: could not delete Forge scratch dir {Path}.",
                        child.FullName);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(forgeDir).Any())
            {
                try { Directory.Delete(forgeDir); }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex,
                "DeleteForgeScratchExceptMemory: best-effort scrub failed at {WtPath}.",
                wtPath);
        }
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

            // Remove scratch from disk so a subsequent `git add -A` or merge
            // commit doesn't re-introduce it. Preserve .forge/memory because
            // Copilot session state is intentionally worktree-local there.
            this.DeleteForgeScratchExceptMemory(wtPath);

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

            // Delete scratch from disk too, but keep .forge/memory for
            // provider session continuity.
            this.DeleteForgeScratchExceptMemory(wtPath);

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

                // A spawn that crashed mid-merge/rebase (or left a stale
                // index.lock) wedges the worktree: the plain `checkout` above
                // fails with "you need to resolve your current index first" /
                // "index.lock: File exists", and the forced `checkout -B`
                // would fail the SAME way — throwing out of GitAsync so the
                // producer never starts and the workstream silently stops
                // progressing (observed 2026-06-03 on three PR-INGESTER
                // stage-5 worktrees stuck 6–10h). Make the worktree pristine
                // first so the forced checkout can always proceed.
                await this.ResetWorktreeToPristineAsync(wtPath, ct);
                await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
            }
            return;
        }

        // Step 3: directory exists on disk but isn't a registered worktree
        // (orphaned from a prior aborted run, or pinned by an orphaned
        // testhost/editor holding the worktree as its working directory).
        // Force-remove via git first so internal refs are cleaned, then
        // robustly delete the directory.
        if (Directory.Exists(wtPath))
        {
            this.logger?.LogInformation("Removing orphaned worktree directory at {Path}", wtPath);
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "remove", "--force", wtPath);
            await this.ForceDeleteWorktreeDirAsync(wtPath, ct);
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");
        }

        // Step 4: now add the worktree fresh.
        //
        // 4a — branch reportedly on origin → fetch it into the LOCAL tracking
        // ref and check it out. A bare `fetch origin {branch}` only advances
        // FETCH_HEAD, leaving `origin/{branch}` unresolvable for `worktree add`,
        // so fetch with an explicit refspec. If the add STILL fails — `ls-remote`
        // saw the branch but it vanished before the add (a Restart's
        // `push origin --delete` racing the re-drive), or the tracking ref can't
        // be materialised — DO NOT hard-fail the producer: clean any partial
        // worktree and fall through to creating the branch from base (4b). This
        // is the robustness fix for the "fatal: invalid reference: origin/{branch}"
        // stall observed on code-intelligence:REPO-SCANNER 2.4-e2e (2026-05-31).
        var addedFromOrigin = false;
        var (existsCode, existsOut, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "ls-remote", "--heads", "origin", branch);
        if (existsCode == 0 && !string.IsNullOrWhiteSpace(existsOut))
        {
            await this.RunGitCaptureAsync(cloneRoot, ct, "fetch", "origin", $"{branch}:refs/remotes/origin/{branch}");
            var (addCode, _, addErr) = await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "add", wtPath, $"origin/{branch}");
            if (addCode == 0)
            {
                await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
                // The branch on origin may have been cut BEFORE an upstream/dependent
                // workstream merged into the base. Bring it up to the current base so
                // the picked-up worktree is a fresh view of all merged dependencies
                // (see MergeBaseIntoFreshWorktreeAsync).
                await this.MergeBaseIntoFreshWorktreeAsync(wtPath, branch, baseBranch, ct);
                addedFromOrigin = true;
            }
            else
            {
                this.logger?.LogWarning(
                    "EnsureWorktree: ls-remote reported origin/{Branch} but `worktree add origin/{Branch}` failed "
                    + "({Err}); falling back to creating {Branch} from base {Base}.",
                    branch, branch, Trim(addErr, 200), branch, baseBranch);
                // Clean any partial worktree dir/registration the failed add left
                // so the from-base add below doesn't trip "already exists".
                await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "remove", "--force", wtPath);
                await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");
                if (Directory.Exists(wtPath))
                {
                    try { Directory.Delete(wtPath, recursive: true); }
                    catch { /* locked — `worktree prune` reclaims it on a later tick */ }
                }
            }
        }

        // 4b — branch absent on origin (or 4a fell through): create it. A prior
        // drive may have left a LOCAL branch ref (step 3 cleans the worktree dir
        // + registration, not the branch ref). Probe with rev-parse and attach to
        // the existing ref instead of failing "a branch named '…' already exists"
        // (mirrors CrewQueueWorker.cs:1006-1011). Otherwise cut a brand-new branch
        // straight off the current base tip — already a fresh view of every merged
        // dependency, so no base sync is needed.
        if (!addedFromOrigin)
        {
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", baseBranch);
            var (localCode, _, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "rev-parse", "--verify", $"refs/heads/{branch}");
            if (localCode == 0)
            {
                await this.GitAsync(cloneRoot, ct, "worktree", "add", wtPath, branch);
                await this.MergeBaseIntoFreshWorktreeAsync(wtPath, branch, baseBranch, ct);
            }
            else
            {
                await this.GitAsync(cloneRoot, ct, "worktree", "add", "-b", branch, wtPath, $"origin/{baseBranch}");
            }
        }
    }

    /// <summary>
    /// Bring a freshly-added worktree's EXISTING branch up to the current base
    /// by merging <c>origin/{baseBranch}</c> into it. Enforces the invariant
    /// that a picked-up workstream's worktree is a fresh view of the current
    /// base: upstream/dependent workstreams that merged into the base AFTER
    /// this branch was first cut flow in here, so a workstream never runs (and
    /// its qa-e2e never compiles) against a stale base missing a dependency it
    /// was DAG-gated on.
    /// <para>
    /// Root cause of the 2026-05-30 <c>repo-walker-e2e</c> base-staleness stall:
    /// the e2e branch existed on origin from before its impl sibling merged into
    /// <c>feature/clean-code</c>, and <see cref="EnsureWorktreeAsync(string,string,string,string,CancellationToken)"/>
    /// checked it out as-is (Step 4a) — never re-merging the base — so the
    /// merged <c>internal/cli/walk</c> package was absent and every iteration
    /// failed the e2e test gate identically until the 4-iter cap stalled it.
    /// </para>
    /// <para>
    /// MERGE (not rebase) mirrors the P79c conflict-recovery choice so reviewer
    /// comments stay anchored to reachable SHAs. The caller has just
    /// <c>worktree add</c>-ed a clean working tree, so a clean merge never
    /// disturbs in-flight agent work. On conflict (or any non-clean merge) we
    /// <c>merge --abort</c> and leave the branch as-is — degrading to the prior
    /// behaviour rather than wedging pickup; genuine conflicts are resolved at
    /// delivery time by <see cref="MergeBaseIntoBranchAndDetectConflictsAsync"/>
    /// (P79c) with an agent. Routine non-conflicting upstream merges — the
    /// common case this fixes — flow in cleanly.
    /// </para>
    /// </summary>
    private async Task MergeBaseIntoFreshWorktreeAsync(string wtPath, string branch, string baseBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseBranch)) return;

        var (fetchCode, _, fetchErr) = await this.RunGitCaptureAsync(wtPath, ct, "fetch", "origin", baseBranch);
        if (fetchCode != 0)
        {
            this.logger?.LogWarning(
                "EnsureWorktree base-sync: `fetch origin {Base}` failed in {WtPath} (exit {Code}: {Err}); "
                + "branch {Branch} may run against a stale base.",
                baseBranch, wtPath, fetchCode, Trim(fetchErr, 200), branch);
            return;
        }

        var (mergeCode, mergeOut, mergeErr) = await this.RunGitCaptureAsync(
            wtPath, ct, "merge", "--no-edit", $"origin/{baseBranch}");
        if (mergeCode == 0)
        {
            this.logger?.LogInformation(
                "EnsureWorktree base-sync: merged origin/{Base} into {Branch}; worktree is now a fresh view of the current base.",
                baseBranch, branch);
            return;
        }

        // Conflict / non-clean merge — abort so the worktree is left clean and
        // checked out, then degrade to prior behaviour. Delivery-time P79c
        // resolves real conflicts with an agent; pickup must not wedge on them.
        await this.RunGitCaptureAsync(wtPath, ct, "merge", "--abort");
        this.logger?.LogWarning(
            "EnsureWorktree base-sync: merge of origin/{Base} into {Branch} did not apply cleanly "
            + "(exit {Code}: {Out} {Err}); aborted and left branch as-is. Upstream/base changes are NOT "
            + "in this worktree; conflicts will be resolved at delivery via the P79c merge path.",
            baseBranch, branch, mergeCode, Trim(mergeOut, 150), Trim(mergeErr, 150));
    }

    /// <summary>
    /// 2026-05-21 — robustly delete an orphaned worktree directory. Clears
    /// read-only attributes (git pack files + build outputs are frequently
    /// read-only) and retries a few times to ride out TRANSIENT locks (the
    /// 1 Hz <see cref="Forge.Api.Services.WorktreeChangeFeed"/> <c>git status</c>
    /// poll, a just-killed testhost releasing its working directory). If the
    /// directory still exists after the retries it is PINNED by a live
    /// process — almost always an orphaned <c>testhost</c>/<c>dotnet</c> from a
    /// timed-out test gate that kept the worktree as its CWD (observed
    /// 2026-05-21 on <c>stage-4.2-migrate-cachecontrollerintegrationtests</c>),
    /// or an editor holding <c>.vs</c>. Throw a CLEAR, actionable error rather
    /// than swallowing it and letting the caller fall through to the opaque
    /// <c>git worktree add: '&lt;path&gt;' already exists</c> (exit 128) loop.
    /// </summary>
    private async Task ForceDeleteWorktreeDirAsync(string wtPath, CancellationToken ct)
    {
        var aclResetTried = false;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            if (!Directory.Exists(wtPath)) return;
            try
            {
                ClearReadOnlyRecursive(wtPath);
                Directory.Delete(wtPath, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // After the first transient retry didn't clear it, the lock is
                // not transient — a process holds a handle inside the worktree
                // (almost always the shared copilot.exe server pinning
                // `.forge/memory/session-store.db`, which has the SAME lifespan
                // as this workstream and MUST go when the worktree is cleared;
                // or an orphaned testhost/dotnet from a timed-out test gate).
                // Restart Manager names the exact holders; kill the agent-infra
                // ones, then keep retrying. (2026-05-30 directive.)
                if (attempt >= 2)
                {
                    var killed = WorktreeLockBreaker.BreakLocks(wtPath, this.logger);
                    if (killed > 0)
                    {
                        this.logger?.LogWarning(
                            "Broke {Killed} lock-holder(s) on worktree {Path} (attempt {Attempt}/6) so it can be cleared.",
                            killed, wtPath, attempt);
                    }
                }

                // ACCESS_DENIED is often not a lock at all but a hardened,
                // non-inherited ACL a build/test gate stamped on a subdir
                // (observed 2026-06-30: out\...\Logs granting only SYSTEM +
                // Administrators, no ACE for the interactive user) — so
                // Directory.Delete throws UnauthorizedAccessException even though
                // we own the tree and it is otherwise empty. Re-take ownership +
                // grant the current user FullControl across the subtree so the
                // next attempt can delete it. Best-effort, once.
                if (ex is UnauthorizedAccessException && !aclResetTried)
                {
                    aclResetTried = true;
                    TryGrantCurrentUserFullControl(wtPath, this.logger);
                }

                if (attempt == 6)
                {
                    throw new InvalidOperationException(
                        $"Worktree directory '{wtPath}' could not be removed after {attempt} attempts — it is locked by "
                        + "another process that the Restart Manager lock-breaker could not release (e.g. an editor such as "
                        + "Visual Studio holding it open, or an elevated process a non-elevated Forge.Api cannot kill), or a "
                        + "hardened ACL on a subdirectory that the ACL re-grant could not reset. "
                        + "Close any editor pointed at this path, then Restart the workstream. "
                        + $"Underlying error: {ex.Message}", ex);
                }
                this.logger?.LogWarning(
                    "Worktree dir {Path} still locked (attempt {Attempt}/6): {Err} — retrying.", wtPath, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }

    /// <summary>
    /// Best-effort: take ownership and grant the current user FullControl over
    /// <paramref name="root"/> and everything under it via <c>icacls</c>. Used
    /// when <see cref="ForceDeleteWorktreeDirAsync"/> hits ACCESS_DENIED caused
    /// not by a lock but by a hardened, non-inherited ACL a build/test gate
    /// stamped on a subdirectory (e.g. <c>out\...\Logs</c> granting only
    /// SYSTEM + Administrators). Windows-only; never throws.
    /// </summary>
    private static void TryGrantCurrentUserFullControl(string root, ILogger? log)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        string user;
        try { user = System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
        catch { return; }
        if (string.IsNullOrEmpty(user)) return;

        // /setowner needs us to already own it (we do for worktrees we created)
        // or hold the privilege; /C makes both continue past any entry that
        // can't be updated so a single stubborn node never aborts the sweep.
        RunIcacls(new[] { root, "/setowner", user, "/T", "/C", "/Q" }, root, log);
        RunIcacls(new[] { root, "/grant", $"{user}:(OI)(CI)F", "/T", "/C", "/Q" }, root, log);
    }

    private static void RunIcacls(string[] args, string root, ILogger? log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return;

            // Drain both pipes concurrently to avoid a full-buffer deadlock,
            // then bound the wait so a wedged icacls can never hang the sweep.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            try { Task.WaitAll(new Task[] { stdout, stderr }, 2000); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "icacls invocation failed for {Path} (non-fatal).", root);
        }
    }

    /// <summary>
    /// Clear the read-only flag across a directory tree so
    /// <see cref="Directory.Delete(string,bool)"/> can remove git pack files
    /// and build outputs that are marked read-only. Best-effort per file —
    /// enumeration may race with the locking process, which is fine (the
    /// caller retries).
    /// </summary>
    private static void ClearReadOnlyRecursive(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch { /* best-effort per file */ }
            }
        }
        catch { /* best-effort — directory may be mutating under us */ }
    }

    /// <summary>
    /// Max combined argv byte budget for a single <c>git check-ignore</c>
    /// invocation. Windows' <c>CreateProcess</c> caps the full command line
    /// (resolved binary path + per-arg quoting + the <c>cmd.exe</c> shim
    /// some hosts inject) at <c>32767</c> chars; once exceeded, the
    /// kernel returns <c>ERROR_FILENAME_EXCED_RANGE</c> which surfaces as
    /// the generic <see cref="System.ComponentModel.Win32Exception"/>
    /// <c>"The filename or extension is too long."</c>. The 8 KB ceiling
    /// leaves roughly 24 KB headroom for the prefix so we never trip the
    /// limit even for the verbose re-run (which adds <c>-v</c> and
    /// re-emits the same path subset).
    /// <para>
    /// 2026-05-28 incident: a polluted qa-e2e scratch workspace for
    /// <c>code-intelligence:AST-PARSER 1.5-e2e</c> held 825 files
    /// (~152 KB of argv). The single-shot invocation blew CreateProcess
    /// and bubbled a Win32Exception that the orchestrator's
    /// <c>GitIgnoredPathException</c>-only catch missed; the workstream
    /// looped on <c>spawn-finished{success:false}</c> every 30 minutes
    /// for 10+ hours without opening a PR.
    /// </para>
    /// </summary>
    internal const int CheckIgnoreArgvByteBudget = 8 * 1024;

    /// <summary>
    /// Phase 81 preflight — throw <see cref="GitIgnoredPathException"/> when any
    /// of <paramref name="paths"/> is excluded by the worktree's <c>.gitignore</c>.
    ///
    /// <para>The authoritative decision uses the <b>non-verbose</b>
    /// <c>git check-ignore --</c> form: it exits 0 when at least one path is
    /// genuinely ignored, 1 when none are, and 128 on a tooling error (which is
    /// logged and treated as non-blocking — the guard must never wedge an
    /// otherwise-valid commit). <b>The verbose <c>-v</c> form must NOT drive the
    /// decision:</b> it also prints a line and exits 0 when the <em>winning</em>
    /// rule is a negation (<c>!pattern</c>) that <em>re-includes</em> the file —
    /// e.g. <c>.gitignore:421:!docs/**/*.md</c> — even though that file is NOT
    /// ignored and commits cleanly. Keying off the verbose exit code caused
    /// false-positive halts (a repo with a <c>!docs/**/*.md</c> re-include rule
    /// stalling the plan phase). We therefore use the verbose form only to
    /// describe the rule for the operator, restricted to the paths the
    /// non-verbose form already confirmed are ignored.</para>
    ///
    /// <para>The path list is chunked into batches of at most
    /// <see cref="CheckIgnoreArgvByteBudget"/> argv bytes so each invocation
    /// stays under the Windows <c>CreateProcess</c> command-line cap; see
    /// the constant's doc-comment for the 2026-05-28 incident that motivated
    /// the chunking.</para>
    /// </summary>
    private async Task AssertPathsNotIgnoredAsync(
        string wtPath, string branch, IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (pathList.Length == 0) return;

        // Chunk the path list so each `git check-ignore --` invocation stays
        // under the Windows CreateProcess argv limit. See
        // <see cref="CheckIgnoreArgvByteBudget"/> for the budget rationale.
        var ignored = new List<string>();
        var chunkFailed = false;
        foreach (var chunk in ChunkPathsByArgvBudget(pathList, CheckIgnoreArgvByteBudget))
        {
            var args = new List<string>(chunk.Count + 2) { "check-ignore", "--" };
            args.AddRange(chunk);
            var (code, stdout, stderr) = await this.RunGitCaptureAsync(wtPath, ct, args.ToArray());

            // Exit 1 = nothing in this chunk is genuinely ignored (the happy
            // path, which includes files re-included by a `!pattern` negation
            // rule); skip to the next chunk.
            if (code == 1) continue;

            // Exit != 0 and != 1 means git itself failed (bad invocation,
            // corrupt index, etc.). Don't block the commit on a tooling
            // failure — log and skip this chunk. We continue with remaining
            // chunks because the rest of the path set is still worth
            // checking (one tooling hiccup shouldn't disable the whole
            // ignore guard).
            if (code != 0)
            {
                chunkFailed = true;
                this.logger?.LogWarning(
                    "git check-ignore preflight returned exit {Code} in '{Path}' (branch {Branch}) for chunk of {ChunkCount} path(s); skipping this chunk.\nstdout: {StdOut}\nstderr: {StdErr}",
                    code, wtPath, branch, chunk.Count, stdout, stderr);
                continue;
            }

            // Exit 0 = one or more paths in this chunk are genuinely ignored.
            // Non-verbose stdout lists exactly those paths, one per line.
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0) ignored.Add(trimmed);
            }
        }

        // Dedupe (a single path can appear at most once per chunk; this
        // protects against unexpected duplicate output from git too).
        var ignoredDedup = ignored.Distinct().ToArray();

        // Defensive: every-chunk-clean OR every-chunk-tooling-failed means
        // we have nothing to throw on. If at least one chunk's tooling
        // failed and no other chunk flagged anything, the previous behaviour
        // was to skip the guard — preserve that.
        if (ignoredDedup.Length == 0) return;

        // Re-run verbose ONLY on the confirmed-ignored subset to capture the
        // matching rule (`<source>:<linenum>:<pattern>\t<path>`) for the
        // operator-facing message. Restricting to the ignored subset keeps the
        // detail free of misleading negation lines, AND keeps the verbose
        // invocation small enough that single-chunk is almost always
        // sufficient — but we still chunk defensively for the pathological
        // case where every input was ignored.
        var verboseBuilder = new System.Text.StringBuilder();
        foreach (var chunk in ChunkPathsByArgvBudget(ignoredDedup, CheckIgnoreArgvByteBudget))
        {
            var verboseArgs = new List<string>(chunk.Count + 3) { "check-ignore", "-v", "--" };
            verboseArgs.AddRange(chunk);
            var (_, verbose, _) = await this.RunGitCaptureAsync(wtPath, ct, verboseArgs.ToArray());
            if (!string.IsNullOrEmpty(verbose))
            {
                if (verboseBuilder.Length > 0 && verboseBuilder[verboseBuilder.Length - 1] != '\n')
                {
                    verboseBuilder.Append('\n');
                }
                verboseBuilder.Append(verbose);
            }
        }

        var detail = verboseBuilder.Length > 0
            ? verboseBuilder.ToString()
            : string.Join('\n', ignoredDedup);
        if (chunkFailed)
        {
            detail += "\n\n(note: at least one check-ignore chunk failed with a non-zero git exit; "
                + "the ignored-path list above may be incomplete — see warnings in the log.)";
        }

        throw new GitIgnoredPathException(branch, ignoredDedup, detail);
    }

    /// <summary>
    /// Partition <paramref name="paths"/> into consecutive chunks whose
    /// combined UTF-8 byte length stays under <paramref name="budgetBytes"/>.
    /// Each individual path always lands in its own chunk even if it
    /// exceeds the budget (a single oversized path can still blow
    /// CreateProcess, but we surface a meaningful git exit code in that
    /// degenerate case instead of pre-emptively giving up). Order is
    /// preserved so the operator-facing error mirrors input order.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> ChunkPathsByArgvBudget(
        IReadOnlyList<string> paths, int budgetBytes)
    {
        if (paths.Count == 0) yield break;
        var current = new List<string>();
        var currentBytes = 0;
        foreach (var p in paths)
        {
            // +1 for the inter-arg space the spawner inserts.
            var pBytes = System.Text.Encoding.UTF8.GetByteCount(p) + 1;
            if (current.Count > 0 && currentBytes + pBytes > budgetBytes)
            {
                yield return current;
                current = new List<string>();
                currentBytes = 0;
            }
            current.Add(p);
            currentBytes += pBytes;
        }
        if (current.Count > 0) yield return current;
    }

    /// <summary>
    /// Push <paramref name="branch"/> to <c>origin</c>; on a non-fast-forward
    /// rejection, fetch + rebase the local commits onto <c>origin/{branch}</c>
    /// and retry the push once. Recovers the common case where a prior
    /// orchestrator run advanced the remote branch but the dedicated
    /// worktree's local ref didn't catch up — without this, the spawn halts
    /// and the operator has to click Retry, which fails with the same error.
    ///
    /// <para>2026-05-28 — when rebase fallback itself fails on conflicts
    /// (add/add on identical files written by two consecutive pair attempts
    /// of the same workstream — the classic
    /// <c>SloppyReassignmentReconciler</c> aftermath), abort the rebase and
    /// then <c>git push --force-with-lease origin {branch}</c> to overwrite
    /// the stale remote tip with the worktree's current HEAD. Safe because
    /// Forge work branches are written exclusively by Forge itself
    /// (no third-party writer), and <c>--force-with-lease</c> still rejects
    /// the push if a concurrent Forge writer raced us between the fetch
    /// above and the push (the local remote-tracking ref we just refreshed
    /// is the lease). This unblocks rows that fell through the
    /// <c>SloppyReassignmentReconciler.TryResetWorktreeAsync</c> safety
    /// net (e.g. its republish hit a network blip, or the divergence was
    /// created before that fix landed). Root cause:
    /// <c>code-intelligence:CLEAN-CODE</c> stage 9.1 burning ~$4,500 in
    /// SD2 → E9 pair rotations before operator unstick.</para>
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
            // Rebase produced conflicts (typical: two pair attempts each
            // created the same file from scratch → add/add conflict on
            // every new file). Abort the rebase to leave the worktree
            // clean, then force-republish — see method-level docstring
            // for the safety argument. The lease is the remote-tracking
            // ref we refreshed via the `fetch` two statements above, so
            // a concurrent writer racing us still gets a clean refusal
            // instead of a silent overwrite.
            await this.RunGitCaptureAsync(wtPath, ct, "rebase", "--abort");

            this.logger?.LogWarning(
                "Auto-rebase onto origin/{Branch} hit conflicts; force-with-lease push to overwrite stale remote tip "
                + "(prior-pair divergence aftermath). rebase stdout: {RebaseOut} stderr: {RebaseErr}",
                branch, rebaseOut, rebaseErr);

            var (forceCode, forceOut, forceErr) = await this.RunGitCaptureAsync(
                wtPath, ct, "push", "--force-with-lease", "origin", branch);
            if (forceCode != 0)
            {
                throw new InvalidOperationException(
                    $"git push origin {branch} failed (exit {code}) in '{wtPath}', auto-rebase onto origin/{branch} hit conflicts, "
                    + $"and force-with-lease recovery also failed (exit {forceCode}) — likely a concurrent Forge writer raced us "
                    + "between the fetch and the force-push. Operator intervention required.\n"
                    + $"push stderr: {stderr}\nrebase stdout: {rebaseOut}\nrebase stderr: {rebaseErr}\n"
                    + $"force-push stdout: {forceOut}\nforce-push stderr: {forceErr}");
            }

            this.logger?.LogInformation(
                "Push to origin/{Branch} succeeded after rebase-conflict force-with-lease recovery.", branch);
            return;
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

    /// <summary>
    /// Make a linked worktree pristine so a forced <c>checkout -B</c> can't be
    /// blocked by leftover git state. A spawn that crashed mid-merge/rebase
    /// (or left a stale <c>index.lock</c>) wedges the worktree: every
    /// subsequent <c>git checkout</c> fails with "you need to resolve your
    /// current index first" / "index.lock: File exists", so the producer
    /// never starts an iteration and the workstream silently stops
    /// progressing (observed 2026-06-03 on three PR-INGESTER stage-5
    /// worktrees stuck 6–10h). Clears the stale lock FIRST (so the aborts
    /// and reset below can take the index lock), then best-effort aborts any
    /// in-progress merge/rebase/cherry-pick/revert, then hard-resets to drop
    /// any residual unmerged index entries. Best-effort throughout: a no-op
    /// abort exits non-zero and is ignored. Only called on the destructive
    /// <c>checkout -B</c> fallback path, where discarding uncommitted work is
    /// already the documented behaviour.
    /// </summary>
    private async Task ResetWorktreeToPristineAsync(string wtPath, CancellationToken ct)
    {
        // Resolve and remove a stale index.lock via the per-worktree git dir.
        // `rev-parse --git-path` needs no index lock, so it works even while
        // the tree is wedged. Removing the lock first is required — otherwise
        // the aborts and reset below can't acquire the index lock either.
        var (lockCode, lockOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "rev-parse", "--git-path", "index.lock");
        if (lockCode == 0 && !string.IsNullOrWhiteSpace(lockOut))
        {
            var lockPath = lockOut.Trim();
            if (!Path.IsPathRooted(lockPath))
            {
                lockPath = Path.Combine(wtPath, lockPath);
            }
            try
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                    this.logger?.LogWarning("ResetWorktreeToPristine removed stale index.lock at {Path} in {WtPath}.", lockPath, wtPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this.logger?.LogWarning(ex, "ResetWorktreeToPristine could not delete index.lock at {Path}; continuing.", lockPath);
            }
        }

        // Abort any in-progress sequencer operation. Each is best-effort:
        // when there is nothing to abort git exits non-zero, which we ignore.
        await this.RunGitCaptureAsync(wtPath, ct, "merge", "--abort");
        await this.RunGitCaptureAsync(wtPath, ct, "rebase", "--abort");
        await this.RunGitCaptureAsync(wtPath, ct, "cherry-pick", "--abort");
        await this.RunGitCaptureAsync(wtPath, ct, "revert", "--abort");

        // Final safety net — drop any residual unmerged/staged entries so the
        // subsequent `checkout -B` starts from a clean index.
        await this.RunGitCaptureAsync(wtPath, ct, "reset", "--hard");
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

    /// <summary>
    /// Extra retries granted to a network git op (fetch/push/clone/ls-remote)
    /// that trips the per-call timeout in <see cref="RunGitCaptureOnceAsync"/>.
    /// A timeout there is THROWN (not returned), so without this it escapes the
    /// lock-race retry loop and HALTS the whole orchestrator. A broker / DNS
    /// stall is transient — a retry typically reconnects within seconds — so we
    /// give the timeout its own small budget. Kept tiny and separate from
    /// <see cref="Forge.Api.Services.Git.SharedRepoGitGate.MaxRetryAttempts"/>
    /// because each attempt can cost up to FORGE_GIT_NETWORK_TIMEOUT_SECONDS.
    /// </summary>
    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureAsync(string cwd, CancellationToken ct, params string[] args)
    {
        // A transient credential-broker / DNS stall can trip the per-call
        // timeout in RunGitCaptureOnceAsync, which throws TimeoutException.
        // That escapes the lock-race retry loop below and would HALT the
        // orchestrator on a one-off hiccup (observed 2026-06-24: a single
        // `git fetch origin <branch>` stalled 180s in C:\forge\asz-common and
        // halted the workstream, yet a manual retry reconnected in ~15s).
        // Give the timeout a small retry budget so it self-heals; only a
        // sustained outage exhausts it and rethrows to the existing halt path.
        return await Forge.Api.Services.Git.SharedRepoGitGate.RunWithNetworkTimeoutRetryAsync(
            () => this.RunGitCaptureWithLockRetryAsync(cwd, ct, args),
            Forge.Api.Services.Git.SharedRepoGitGate.NetworkTimeoutRetryAttempts,
            Forge.Api.Services.Git.SharedRepoGitGate.NetworkTimeoutRetryDelayMs,
            ct,
            (tex, attempt) => this.logger?.LogWarning(
                tex,
                "git {Args} timed out (network attempt {Attempt}/{Max}) in '{Cwd}'; retrying.",
                string.Join(' ', args), attempt + 1, Forge.Api.Services.Git.SharedRepoGitGate.NetworkTimeoutRetryAttempts + 1, cwd))
            .ConfigureAwait(false);
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureWithLockRetryAsync(string cwd, CancellationToken ct, params string[] args)
    {
        // Per-clone-root serialization + transient-lock retry. Solves the
        // Windows packed-refs / index.lock race that halted two spawn
        // orchestrators on 2026-05-28 (see SharedRepoGitGate docstring).
        var repoKey = Forge.Api.Services.Git.SharedRepoGitGate.ResolveCloneRootKey(cwd);
        // Network ops (fetch/push/ls-remote/clone) are idempotent + safe to
        // re-attempt, so they additionally retry on a non-zero exit with EMPTY
        // stderr — the fingerprint of a Windows-side abort (e.g. a config-write
        // race) that IsTransientLockError can't match. See ShouldRetryNetworkOp.
        var isNetworkOp = IsNetworkGitOp(args);
        (int Code, string StdOut, string StdErr) lastResult = (0, string.Empty, string.Empty);

        for (var attempt = 0; attempt < Forge.Api.Services.Git.SharedRepoGitGate.MaxRetryAttempts; attempt++)
        {
            var delayMs = Forge.Api.Services.Git.SharedRepoGitGate.RetryDelayMs(attempt);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            var sem = await Forge.Api.Services.Git.SharedRepoGitGate.AcquireAsync(repoKey, ct).ConfigureAwait(false);
            try
            {
                lastResult = await this.RunGitCaptureOnceAsync(cwd, ct, args).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }

            if (!Forge.Api.Services.Git.SharedRepoGitGate.ShouldRetryNetworkOp(lastResult.Code, lastResult.StdErr, isNetworkOp))
            {
                return lastResult;
            }

            this.logger?.LogWarning(
                "git {Args} hit a transient failure (lock race or empty-stderr network abort; exit {Code}) "
                + "(attempt {Attempt}/{Max}) in '{Cwd}'. stderr: {StdErr}",
                string.Join(' ', args), lastResult.Code, attempt + 1, Forge.Api.Services.Git.SharedRepoGitGate.MaxRetryAttempts, cwd, lastResult.StdErr);
        }

        // Exhausted all retries. Return the last (still-failing) result
        // so the caller's existing error-reporting path runs unchanged.
        return lastResult;
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureOnceAsync(string cwd, CancellationToken ct, string[] args)
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

        // Windows MAX_PATH guard (durable). Worktrees live under
        // `{cloneRoot}\.worktree\{slug}\…`, and a base branch can carry
        // deeply-nested, long-named files (observed: code-intelligence
        // PR-INGESTER stage 6.6, whose checkout path hit 261 chars — one
        // over the 260 limit — because the base tree held AST-PARSER e2e
        // `.feature`/`_test.go` files named after long feature titles).
        // `git worktree add` then died with "Filename too long" / "Could
        // not reset index file to revision 'HEAD'", which (before the
        // RealStoryGitOps.cs:2829 log fix) was masked by a logging
        // FormatException and stalled the workstream indefinitely.
        // `-c core.longpaths=true` makes git use the `\\?\` extended-length
        // prefix for its own file ops, lifting the 260-char ceiling for
        // EVERY git invocation here (existing + freshly-cloned repos),
        // without depending on a one-time `git config` write per clone.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.longpaths=true");

        // Non-interactive GitHub credential. The extraheader is HOST-SCOPED to
        // https://github.com/, so git only sends it on github.com requests —
        // it is a transparent no-op for ADO remotes (which keep using the
        // GCM_AZREPOS_CREDENTIALTYPE=oauth path) and for local ops. Added to
        // ArgumentList ONLY — never to `args` — so the token never reaches the
        // Debug/transient-lock log lines that print string.Join(' ', args).
        // Mirrors the clone-time injection in RepoCloneService.InjectPat.
        if (IsNetworkGitOp(args))
        {
            var ghToken = await this.ResolveGitHubTokenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ghToken))
            {
                var basic = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"x-access-token:{ghToken}"));
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add($"http.https://github.com/.extraheader=AUTHORIZATION: basic {basic}");
            }
        }

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
