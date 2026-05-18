// -----------------------------------------------------------------------
// <copyright file="IStoryGitOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — git surface used by the
/// <c>StorySpawnOrchestrator</c>. Production binds this to the real git
/// CLI; tests bind an in-memory implementation that records the commit
/// graph without touching a working tree. Either way the orchestrator's
/// flow is identical.
/// </summary>
public interface IStoryGitOps
{
    /// <summary>Create a new branch off <paramref name="baseBranch"/>. No-op when the branch already exists.</summary>
    Task EnsureBranchAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Phase 50 — like <see cref="EnsureBranchAsync"/> but ALWAYS force-resets
    /// the branch tip to <paramref name="baseBranch"/>'s tip on origin, even
    /// when the branch already exists locally or on origin. Used by the
    /// orchestrator on the FIRST drive of a fresh spawn to auto-heal orphan
    /// branches that a previously-deleted spawn left behind on origin (the
    /// <c>git push origin --delete</c> in <c>DeleteSpawn</c> can fail silently
    /// when the operator's git credential cache is missing; observed
    /// 2026-05-11 on <c>local:HIGH-MEM-CONSUMPTION</c> — re-spawning the same
    /// storyId inherited the stale plan-doc commits because
    /// <see cref="EnsureBranchAsync"/> silently <c>git fetch</c>-ed the
    /// orphan branch tip). Atomic: a force-push of
    /// <c>refs/remotes/origin/{base}:refs/heads/{branch}</c> replaces any
    /// existing tip in one shot under the operator's fresh auth context.
    /// Also drops the local branch + worktree so downstream
    /// <see cref="EnsureWorktreeAsync"/> doesn't reuse a stale checkout.
    /// </summary>
    Task EnsureBranchFreshAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Write each <c>files</c> entry into the working tree at the given branch
    /// and commit them with <paramref name="message"/>. Returns the synthetic
    /// commit sha. Tests use this to verify that a merged PR actually carries
    /// the expected files. Materialises the worktree at
    /// <c>{cloneRoot}/.worktree/{worktreeSlug}</c> when missing — the slug is
    /// the canonical name from <see cref="PlanDocStateHelpers"/> (plan / QA
    /// / QA-steps) or <see cref="WorkstreamWorktreeSlug"/> (engineer
    /// workstreams).
    /// </summary>
    Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, string worktreeSlug,
        IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default);

    /// <summary>Returns true when <paramref name="filePath"/> exists at the branch tip.</summary>
    Task<bool> FileExistsAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default);

    /// <summary>Reads a file from the branch tip; null when missing.</summary>
    Task<string?> ReadFileAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default);

    /// <summary>Merges <paramref name="branch"/> into <paramref name="baseBranch"/>; tests just copy the tree.</summary>
    Task MergeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Phase 45 — bring <paramref name="baseBranch"/> up to date with
    /// <paramref name="upstreamBranch"/> (typically <c>main</c>). Production runs
    /// <c>git fetch origin {upstream}</c> followed by <c>git rebase origin/{upstream}</c>
    /// against the worktree for the base branch. Tests just fast-forward the
    /// in-memory branch pointer to the upstream tip when no divergence has
    /// happened locally. No-op when <paramref name="baseBranch"/> already
    /// contains every commit on <paramref name="upstreamBranch"/>.
    /// </summary>
    Task RebaseBaseBranchAsync(RepoRow? repo, string baseBranch, string upstreamBranch, CancellationToken ct = default);

    /// <summary>
    /// Phase 46 — merge latest from <paramref name="upstreamBranch"/> (typically
    /// <c>"main"</c>) into the feature/worktree <paramref name="branch"/> before
    /// creating a PR. This ensures the PR source branch contains the latest
    /// upstream changes and reduces merge conflicts. Returns <c>true</c> when the
    /// merge succeeded (including the no-op case); <c>false</c> when there were
    /// unresolvable conflicts. Production runs <c>git merge --no-edit
    /// origin/{upstream}</c> in the branch's worktree. Tests merge the in-memory
    /// branch pointers.
    /// <para>
    /// When <paramref name="force"/> is true (default), conflicts are
    /// auto-resolved with <c>-X ours</c> (keep source-side hunks, discard
    /// upstream-side). When false, the function aborts the merge on conflict
    /// and returns false so the caller can surface a 409 to the user.
    /// </para>
    /// </summary>
    Task<bool> MergeUpstreamIntoBranchAsync(RepoRow? repo, string branch, string upstreamBranch, bool force = true, CancellationToken ct = default);

    /// <summary>
    /// Phase 42 — ensure a git worktree exists for <paramref name="branch"/>
    /// and return its absolute disk path. Use as the agent's working
    /// directory when running plan / coding tasks so the agent's tools see
    /// the actual repo layout instead of an isolated scratch dir. Idempotent;
    /// concurrent callers within the same process get the same path.
    ///
    /// <para><paramref name="worktreeSlug"/> is REQUIRED — the worktree
    /// directory is always rooted at <c>{cloneRoot}/.worktree/{worktreeSlug}</c>.
    /// Use <see cref="PlanDocStateHelpers.CanonicalDocWorktreeSlug"/> /
    /// <see cref="PlanDocStateHelpers.CanonicalQaWorktreeSlug"/> /
    /// <see cref="PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug"/> for
    /// plan and QA gates, and <see cref="WorkstreamWorktreeSlug.Compute"/>
    /// for engineer workstreams.</para>
    /// </summary>
    Task<string> EnsureWorktreeAsync(
        RepoRow? repo,
        string branch,
        string baseBranch,
        CancellationToken ct = default,
        string? worktreeSlug = null);

    /// <summary>
    /// Phase 49 follow-up — best-effort cleanup of the worktree associated
    /// with <paramref name="branch"/>. Runs <c>git worktree remove --force</c>
    /// and removes any leftover directory. The branch ref itself is left
    /// alone (cheap on disk, useful for audit). Used to free disk after a
    /// spawn's plan PR merges (plan worktree becomes stale) and after each
    /// workstream's PR merges (engineer worktree becomes stale). No-op when
    /// no worktree currently maps to the branch; never throws.
    /// </summary>
    Task RemoveWorktreeAsync(RepoRow? repo, string branch, CancellationToken ct = default);

    /// <summary>
    /// Phase 59 — returns <c>true</c> iff a worktree for <paramref name="branch"/>
    /// is currently registered with git AND its on-disk directory exists.
    /// Used by <c>StorySpawnOrchestrator.DriveReviewAsync</c> to gate the
    /// review-fix loop: when a workstream's PR merges, Phase 49's cleanup
    /// runs <see cref="RemoveWorktreeAsync"/>, after which any subsequent
    /// review-fix attempt (e.g. for a late-arriving reviewer comment) would
    /// fail in <c>CommitFilesAsync</c> because the working tree is gone.
    /// Probing up front lets the orchestrator skip the fix loop and
    /// auto-resolve straggler comments instead of burning engineer tokens
    /// on commits that will throw.
    /// <para>Best-effort: on any probe failure (transient git invocation
    /// error, permissions issue) returns <c>true</c> so callers fall back
    /// to their existing try/catch-around-CommitFilesAsync path. Never
    /// throws.</para>
    /// </summary>
    Task<bool> WorktreeExistsAsync(RepoRow? repo, string branch, CancellationToken ct = default);

    /// <summary>
    /// Phase 42 — stage one repo-relative path in <paramref name="branch"/>'s
    /// worktree and commit. Used by per-iteration plan-doc commits so the
    /// Worktree tab shows real history. Serialized per-branch so concurrent
    /// architect agents don't race the git index. Returns the new HEAD sha
    /// (or empty when there was nothing to commit — the file content was
    /// identical to what's already on the branch).
    /// </summary>
    Task<string> CommitPathInWorktreeAsync(
        RepoRow? repo, string branch, string repoRelativePath, string message, CancellationToken ct = default);

    /// <summary>
    /// Push the local branch to <c>origin</c>. Stages and commits any
    /// uncommitted changes in the worktree first so local-only per-iteration
    /// commits are included. No-op when nothing to push.
    /// </summary>
    Task PushBranchAsync(RepoRow? repo, string branch, CancellationToken ct = default);

    /// <summary>
    /// Scrubs <c>.forge/</c> scratch paths from the git index so they
    /// never land in a commit — handles both freshly-staged and
    /// already-tracked files from prior iterations. Best-effort no-op
    /// when the directory doesn't exist.
    /// </summary>
    Task ScrubForgeScratchFromIndexAsync(string wtPath, CancellationToken ct);

    /// <summary>
    /// Phase 45 follow-up — list repo-relative paths of files the agent
    /// touched in <paramref name="worktreePath"/> but hasn't yet committed,
    /// PLUS files contributed by local commits that aren't yet on the
    /// reference branch. Includes added / modified / renamed paths
    /// (parsed from <c>git status --porcelain</c>); deleted paths are
    /// omitted because the orchestrator's <see cref="CommitFilesAsync"/>
    /// dictionary contract can't represent deletions today.
    ///
    /// <para>Phase 49 follow-up — when <paramref name="baseBranch"/> is
    /// supplied, the committed-but-unpushed source diffs against
    /// <c>origin/{baseBranch}</c> instead of <c>origin/{HEAD branch}</c>.
    /// This is the correct frame for the post-pass commit decision: the
    /// workstream's contribution to the PR is the cumulative diff
    /// against the merge target, not against the workstream's own
    /// remote tracking branch (which may be at a stale state, missing
    /// entirely for a never-pushed branch, or — as observed
    /// 2026-05-10 on failover-cluster:XRAFT — pinned to an upstream
    /// shared branch like the plan PR base, masking the agent's
    /// commits from the diff scope).</para>
    ///
    /// <para>When <paramref name="baseBranch"/> is null/empty, falls back
    /// to the legacy <c>origin/{HEAD branch}</c> diff so per-iter
    /// no-progress detection still resets on uncommitted edits each
    /// iter without permanently latching to "always non-zero" after the
    /// agent's first self-commit.</para>
    /// </summary>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(
        RepoRow? repo,
        string worktreePath,
        string? baseBranch = null,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 65 — snapshot a branch's current HEAD SHA + commit subject +
    /// diff numstat against a baseline. Used by the gen/eval loop's
    /// <c>OnIterationCommit</c> hook to record deterministic per-iter
    /// file-change counts on the <c>DeliverableRow</c> — deterministic
    /// alternative to the pre-existing <c>task.worktree-tick</c> event
    /// aggregation in <c>ConversationInsightQueries</c>, which misses
    /// iter windows when the worktree is removed mid-flight or when
    /// commits land in the last 1-second tick slice.
    ///
    /// <para>Returns <c>null</c> when the branch doesn't resolve to a
    /// commit (never-pushed branch, missing repo, etc.). When
    /// <paramref name="baselineSha"/> is null or equals the current
    /// HEAD, the numstat fields are all zero (no commit since the
    /// caller's baseline) but the returned SHA + subject still reflect
    /// HEAD so the caller can record "we observed this SHA at iter
    /// boundary".</para>
    /// </summary>
    Task<IterCommitSnapshot?> SnapshotBranchHeadAsync(
        RepoRow? repo,
        string branch,
        string? baselineSha,
        CancellationToken ct = default);

    /// <summary>
    /// 2026-05-16 — alternative snapshot for the workstream path where the
    /// engineer agent edits files in the worktree across iters but the
    /// actual commit lands ONCE at pass time (via
    /// <c>ReadCachedWorkstreamFilesAsync</c> → <c>CommitFilesAsync</c>).
    /// In that flow <see cref="SnapshotBranchHeadAsync"/> reports zero
    /// counts on every iter because HEAD never advances during the
    /// gen-eval loop — the operator sees `0f` on every iter chip while
    /// the final PR opens with N files. This method instead diffs the
    /// LIVE WORKTREE (committed + uncommitted + untracked) against
    /// <paramref name="baseBranch"/> so each iter chip shows the
    /// cumulative WIP delta. The architect / per-iter-commit path stays
    /// on <see cref="SnapshotBranchHeadAsync"/> because there HEAD does
    /// advance per iter.
    ///
    /// <para>Returns <c>null</c> when the worktree path doesn't exist or
    /// the diff commands all fail. On success: <c>Sha</c> is the
    /// worktree HEAD (may equal baseBranch if no commits yet),
    /// <c>Subject</c> is HEAD's commit subject (empty when at base),
    /// <c>Added/Modified/Deleted</c> sum committed-since-base +
    /// uncommitted-vs-HEAD + untracked into a single per-status tally.</para>
    /// </summary>
    Task<IterCommitSnapshot?> SnapshotWorktreeChangesAsync(
        RepoRow? repo,
        string worktreePath,
        string baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 79 — fetch <paramref name="baseBranch"/> and attempt
    /// <c>git rebase origin/{baseBranch}</c> in the workstream worktree.
    /// Returns a discriminated result the orchestrator routes on:
    /// <list type="bullet">
    ///   <item><see cref="RebaseState.Clean"/> — rebase succeeded, branch is
    ///         ahead of base, caller force-pushes + retries the merge.</item>
    ///   <item><see cref="RebaseState.Conflicted"/> — rebase paused on
    ///         conflict markers; <see cref="RebaseResult.ConflictedFiles"/>
    ///         carries each file's path + on-disk content (markers and all)
    ///         for the agent resolver to consume.</item>
    ///   <item><see cref="RebaseState.RebaseFailed"/> — rebase aborted for
    ///         non-conflict reasons (missing upstream, detached HEAD, locked
    ///         index, etc.); <see cref="RebaseResult.Detail"/> carries git
    ///         stderr for the caller's stall reason.</item>
    /// </list>
    ///
    /// <para>Always leaves the worktree in a recoverable state: on
    /// <c>Conflicted</c> the rebase is in progress (caller resolves +
    /// <see cref="ContinueRebaseAndPushAsync"/>); on <c>RebaseFailed</c>
    /// the rebase is aborted (<c>git rebase --abort</c>) so the worktree
    /// is back at the pre-rebase tip.</para>
    /// </summary>
    Task<RebaseResult> RebaseAndDetectConflictsAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Phase 79 — apply agent-resolved file contents to the in-progress
    /// rebase, stage them, <c>git rebase --continue</c>, and on a clean
    /// finish <c>git push --force-with-lease</c> the branch.
    ///
    /// <para>If <c>git rebase --continue</c> stops at ANOTHER conflict
    /// commit (multi-commit rebase), the method returns
    /// <see cref="RebaseState.Conflicted"/> with the new conflict set —
    /// the orchestrator loops back through resolution. The full rebase
    /// is bounded by the orchestrator's <c>MaxResolveAttempts</c> cap.</para>
    ///
    /// <para>Force-push uses <c>--force-with-lease=origin/{branch}:{old-tip}</c>
    /// captured before the rebase started; this rejects the push if
    /// origin moved (e.g., concurrent operator push) instead of
    /// clobbering. On rejection the method returns <c>RebaseFailed</c>
    /// with the rejection detail so the caller can stall.</para>
    /// </summary>
    Task<RebaseResult> ContinueRebaseAndPushAsync(
        RepoRow? repo,
        string branch,
        IReadOnlyDictionary<string, string> resolvedFiles,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 79 — best-effort cleanup helper. Called by the orchestrator
    /// when the resolver loop gives up (max attempts reached or
    /// <see cref="RebaseState.RebaseFailed"/>) so the worktree is back at
    /// a clean tip for the next iter. Idempotent; no-op when no rebase
    /// is in progress.
    /// </summary>
    Task AbortRebaseAsync(RepoRow? repo, string branch, CancellationToken ct = default);
}

/// <summary>
/// Phase 79 — outcome of <see cref="IStoryGitOps.RebaseAndDetectConflictsAsync"/>
/// or <see cref="IStoryGitOps.ContinueRebaseAndPushAsync"/>. The state
/// drives the orchestrator's resolver-loop control flow.
/// </summary>
public enum RebaseState
{
    /// <summary>Rebase succeeded; branch advanced past base. Caller force-pushes + retries merge.</summary>
    Clean,

    /// <summary>Rebase paused on conflict markers; agent must resolve.</summary>
    Conflicted,

    /// <summary>Rebase or force-push aborted for non-conflict reasons; caller stalls.</summary>
    RebaseFailed,
}

/// <summary>
/// Phase 79 — return shape of the two rebase methods.
/// </summary>
public sealed record RebaseResult(
    RebaseState State,
    IReadOnlyList<ConflictedFile> ConflictedFiles,
    string? Detail);

/// <summary>
/// Phase 79 — a single conflicted file as seen during an in-progress
/// rebase. <see cref="Content"/> is the raw on-disk text with
/// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> markers preserved
/// so the agent has full context for resolution.
/// </summary>
public sealed record ConflictedFile(string Path, string Content);

/// <summary>
/// Phase 65 — return shape of <see cref="IStoryGitOps.SnapshotBranchHeadAsync"/>.
/// Lives in this file (the <c>Stories</c> namespace) so the git surface
/// stays self-contained; the consumer in <c>DeliverableLoop</c>
/// translates this into <c>IterCommitInfo</c>.
/// </summary>
public sealed record IterCommitSnapshot(
    string Sha,
    string Subject,
    int Added,
    int Modified,
    int Deleted);
