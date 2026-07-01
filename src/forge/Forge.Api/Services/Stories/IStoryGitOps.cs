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

    /// <summary>
    /// 2026-05-27 — Per-iter durability for the gen↔eval loop: stage all
    /// worktree changes (excluding <c>.forge/</c> scratch + build artifacts
    /// via the same scrub pipeline as <see cref="CommitFilesAsync"/>),
    /// commit them with the forge-engineer identity, and push to
    /// <c>origin/{branch}</c>. Branch is resolved from the worktree's HEAD
    /// (<c>git rev-parse --abbrev-ref HEAD</c>) so callers don't need to
    /// thread it through.
    ///
    /// <para>Returns <c>true</c> if a new commit was created and pushed;
    /// <c>false</c> when nothing was staged (clean worktree) OR a git step
    /// failed transiently. <b>Best-effort:</b> every failure logs a warning
    /// and the method returns <c>false</c> rather than throwing — the agent
    /// loop must keep running even when a transient git glitch blocks a
    /// per-iter push.</para>
    ///
    /// <para>Without this, every generator iter leaves edits uncommitted;
    /// a Copilot SDK 24-min timeout (or any premature termination) loses
    /// the iter's work entirely because <see cref="EnsureWorktreeAsync"/>'s
    /// <c>git reset --hard origin/{branch}</c> wipes the uncommitted state
    /// at the start of iter N+1. The same fix has been live in
    /// <c>CrewQueueWorker</c> since 2026-05-13 (XRAFT stage 3.1 root cause);
    /// this method exposes it for the story-spawn impl path, which routes
    /// through <c>CrewStoryArtifactStrategy.GeneratorDelegate</c> and was
    /// previously unprotected.</para>
    /// </summary>
    Task<bool> CommitAllAndPushAsync(
        string worktreePath, string commitMessage, CancellationToken ct = default);

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
    /// Returns <c>true</c> when <paramref name="branch"/> carries any file diff
    /// on top of <paramref name="baseBranch"/> (three-dot, from the merge-base
    /// to the branch tip). The qa-e2e / impl drivers call this after committing
    /// to decide whether opening a PR is worthwhile: when the produced files all
    /// matched a repo <c>.gitignore</c> rule, <see cref="CommitFilesAsync"/>
    /// stages nothing and the branch ends up identical to base — opening a PR
    /// then just creates empty review noise. Production runs
    /// <c>git diff --quiet origin/{base}...origin/{branch}</c>; on any git error
    /// the implementation returns <c>true</c> (fail-open, so a legitimate PR is
    /// never suppressed by a transient check failure).
    /// <para>
    /// NOTE: returns <c>false</c> for BOTH "branch never committed" AND
    /// "branch already fully merged into base" — three-dot diff is empty in
    /// both cases (merge-base equals branch tip in the merged case, so the
    /// walk merge-base→branch.tip is a single commit). Callers that need to
    /// distinguish the two must follow up with
    /// <see cref="IsBranchAlreadyMergedIntoBaseAsync"/>.
    /// </para>
    /// </summary>
    Task<bool> BranchHasChangesVsBaseAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="branch"/>'s tip is reachable
    /// from <paramref name="baseBranch"/> on origin — i.e. the branch has
    /// ALREADY been merged into base (typically because the operator hit the
    /// Merge button on GitHub/ADO out of band, or a sibling spawn merged via
    /// the provider's auto-merge before our local review crew reached the
    /// merge step). Distinct from <see cref="BranchHasChangesVsBaseAsync"/>:
    /// both return "no changes / no diff" for an already-merged branch, but
    /// only this method reports the underlying reason.
    /// <para>
    /// Used by <c>StorySpawnOrchestrator.ApproveAndMergeAsync</c> to
    /// disambiguate the two cases hidden behind a "no diff" result so the
    /// orchestrator can advance to "merging" (the PR was effectively merged
    /// externally) instead of stalling with the misleading "empty PR — fix
    /// your .gitignore" error.
    /// </para>
    /// <para>
    /// Production runs
    /// <c>git merge-base --is-ancestor origin/{branch} origin/{base}</c>
    /// (exit 0 = ancestor = already merged). FAILS CLOSED — returns
    /// <c>false</c> on any git error so a transient probe failure cannot
    /// falsely short-circuit a legitimate empty-PR refusal.
    /// </para>
    /// </summary>
    Task<bool> IsBranchAlreadyMergedIntoBaseAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

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
    /// Best-effort removal of an ORPHANED per-workstream worktree directory —
    /// one left on disk under <c>{cloneRoot}\.worktree\</c> that git no longer
    /// tracks (absent from <c>git worktree list</c>, no <c>.git</c> pointer).
    /// These arise when <see cref="RemoveWorktreeAsync"/>'s
    /// <c>git worktree remove --force</c> dropped the registration but the
    /// follow-up directory delete failed on a locked <c>out\</c> / <c>.forge\</c>
    /// subtree; once the registration is gone, nothing else re-scans the
    /// directory (prune sees no dangling entry, RemoveWorktreeAsync early-returns,
    /// the reaper's git-based loops don't see it), so it survives forever.
    /// <paramref name="worktreeDir"/> MUST be an absolute path directly under the
    /// repo's <c>.worktree\</c> folder; the implementation refuses the cloneRoot /
    /// main worktree and anything outside <c>.worktree\</c>. Returns <c>true</c>
    /// when the directory no longer exists on return. Never throws.
    /// </summary>
    Task<bool> TryRemoveOrphanWorktreeDirAsync(RepoRow? repo, string worktreeDir, CancellationToken ct = default);

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
    /// Phase 81 / 83 preflight — throw <see cref="GitIgnoredPathException"/>
    /// when any of <paramref name="repoRelativePaths"/> is excluded by the
    /// repo's <c>.gitignore</c> on <paramref name="branch"/>'s worktree.
    /// Lets the orchestrator surface a configuration error BEFORE running the
    /// gen-eval loop, so misconfigured repos (e.g. OneBranch <c>/*</c> +
    /// <c>!/src/</c> allowlists routing plan docs into the default
    /// <c>docs/stories/...</c> path) halt with $0 LLM cost instead of burning
    /// N iterations of agent work that can never be committed.
    ///
    /// <para>Requires the worktree to already exist — call
    /// <see cref="EnsureWorktreeAsync"/> first; otherwise <c>git
    /// check-ignore</c> has no working directory to evaluate against.</para>
    ///
    /// <para>Mirrors the per-commit preflight already wired into
    /// <see cref="CommitFilesAsync"/> and <see cref="CommitPathInWorktreeAsync"/>,
    /// just exposed at the interface for callers that want to fail fast.</para>
    /// </summary>
    Task AssertPlanDocPathsNotIgnoredAsync(
        RepoRow? repo, string branch, IEnumerable<string> repoRelativePaths, CancellationToken ct = default);

    /// <summary>
    /// Phase 85 — recoverable variant of
    /// <see cref="AssertPlanDocPathsNotIgnoredAsync"/>: when the path(s)
    /// would be excluded by the repo's <c>.gitignore</c>, append a narrow
    /// allowlist exception (<c>!/{baseDir}/</c> with a parent-allow chain) as
    /// a prep commit on <paramref name="branch"/> and push it BEFORE the
    /// plan-doc work begins. Re-runs the ignore check after applying the
    /// fix; if the path is still ignored (e.g. a nested <c>.gitignore</c>
    /// blocks it), throws <see cref="GitIgnoredPathException"/> with an
    /// augmented message pointing the operator at the nested file.
    ///
    /// <para>Scope per operator-confirmed design: narrow allowlist (only
    /// the <paramref name="planDocBaseDir"/> subtree, not all of /docs/),
    /// prep commit on the same branch (one PR), placement at the
    /// orchestrator preflight (so the fix is applied at $0 LLM cost).
    /// Plan docs only — other ignored paths (e.g. impl-phase deliverables
    /// outside /src/) still fail loud via the per-commit preflight in
    /// <see cref="CommitFilesAsync"/>.</para>
    ///
    /// <para>Idempotent: when preflight already passes (rules already on
    /// branch from a prior run, or path was never ignored), this is a
    /// no-op. Same branch is safe to call repeatedly.</para>
    ///
    /// <para>Requires the worktree to already exist — call
    /// <see cref="EnsureWorktreeAsync"/> first.</para>
    /// </summary>
    Task EnsurePlanDocPathsCommittableAsync(
        RepoRow? repo,
        string branch,
        IEnumerable<string> repoRelativePaths,
        string planDocBaseDir,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 86 — generalised counterpart to
    /// <see cref="EnsurePlanDocPathsCommittableAsync"/> for non-plan-doc paths
    /// (qa-e2e <c>tests/...</c> artifacts, future configurable output trees).
    /// Same auto-fix semantics as the plan-doc variant: when the path(s)
    /// would be excluded by the repo's <c>.gitignore</c>, append a narrow
    /// allowlist exception for <paramref name="baseDir"/> as a prep commit
    /// on <paramref name="branch"/>, then re-verify. If the path is still
    /// ignored (nested <c>.gitignore</c> blocker), throws
    /// <see cref="GitIgnoredPathException"/> with operator guidance.
    ///
    /// <para>Use this when an orchestrator path commits files under a
    /// configurable / non-plan-doc base directory (e.g. qa-e2e writes
    /// <c>tests/...</c> by default — OneBranch repos' <c>.gitignore</c>
    /// <c>/*/</c> rule would silently swallow those without preflight).</para>
    ///
    /// <para>Idempotent + worktree-required like the plan-doc variant.
    /// Implementation may delegate to the same underlying narrow-allowlist
    /// helper.</para>
    /// </summary>
    Task EnsurePathsCommittableAsync(
        RepoRow? repo,
        string branch,
        IEnumerable<string> repoRelativePaths,
        string baseDir,
        CancellationToken ct = default);

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
    /// Returns repo-relative paths changed by an already-merged PR, using the
    /// provider-captured merge range when available. This is the historical
    /// diff used when the live branch-vs-base diff is empty because the PR has
    /// already landed on the target branch.
    /// </summary>
    Task<IReadOnlyList<string>> GetChangedFilesFromMergedPrAsync(
        RepoRow? repo,
        string worktreePath,
        string? mergeCommitSha,
        string? mergeBaseSha = null,
        string? mergeHeadSha = null,
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
    /// Phase 79c — fetch <paramref name="baseBranch"/> and attempt
    /// <c>git merge origin/{baseBranch}</c> in the workstream worktree.
    /// Returns a discriminated result the orchestrator routes on:
    /// <list type="bullet">
    ///   <item><see cref="MergeState.Clean"/> — merge succeeded with no
    ///         conflicts (fast-forward or new merge commit). Caller still
    ///         calls <see cref="ContinueMergeAndPushAsync"/> with an empty
    ///         dict so the resulting commit(s) get pushed to origin.</item>
    ///   <item><see cref="MergeState.Conflicted"/> — merge paused with
    ///         conflict markers in the working tree;
    ///         <see cref="MergeResult.ConflictedFiles"/> carries each
    ///         file's path + on-disk content for the agent resolver to
    ///         consume.</item>
    ///   <item><see cref="MergeState.MergeFailed"/> — merge aborted for
    ///         non-conflict reasons (missing upstream, dirty working
    ///         tree, fatal error, etc.); <see cref="MergeResult.Detail"/>
    ///         carries git stderr for the caller's stall reason.</item>
    /// </list>
    ///
    /// <para>Always leaves the worktree in a recoverable state: on
    /// <c>Conflicted</c> the merge is in progress (caller resolves +
    /// <see cref="ContinueMergeAndPushAsync"/>); on <c>MergeFailed</c>
    /// the merge is aborted (<c>git merge --abort</c>) so the worktree
    /// is back at the pre-merge tip.</para>
    ///
    /// <para><b>Why merge rather than rebase?</b> The merge approach
    /// preserves the ws branch's commit history and does not require
    /// force-push, which keeps reviewer comments anchored to the lines
    /// they were posted against. Force-pushed history (the old rebase
    /// path) routinely orphaned late-arriving reviewer comments.</para>
    /// </summary>
    Task<MergeResult> MergeBaseIntoBranchAndDetectConflictsAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Phase 79c — finish an in-progress merge: write each entry in
    /// <paramref name="resolvedFiles"/> back into the worktree, stage
    /// them, run <c>git commit</c> (no editor) to finalize the merge,
    /// then <c>git push origin {branch}</c> (no <c>--force</c>).
    ///
    /// <para>Unlike the prior rebase flow, a single conflict round is
    /// expected per call: <c>git merge</c> surfaces ALL conflicts up
    /// front, so once the agent resolves them and we commit, there is
    /// no follow-up "another conflict commit" state. If the working
    /// tree is already clean (caller passed an empty dict because
    /// <see cref="MergeBaseIntoBranchAndDetectConflictsAsync"/> returned
    /// <see cref="MergeState.Clean"/>), this method short-circuits to
    /// the push step.</para>
    ///
    /// <para>Push is a plain (non-force) push. If origin's tip moved
    /// (concurrent operator push, etc.) the push fails and the method
    /// returns <see cref="MergeState.MergeFailed"/> with the rejection
    /// detail so the caller can stall.</para>
    /// </summary>
    Task<MergeResult> ContinueMergeAndPushAsync(
        RepoRow? repo,
        string branch,
        IReadOnlyDictionary<string, string> resolvedFiles,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 79c — best-effort cleanup helper. Called by the orchestrator
    /// when the resolver loop gives up (max attempts reached or
    /// <see cref="MergeState.MergeFailed"/>) so the worktree is back at
    /// a clean tip for the next iter. Idempotent; no-op when no merge
    /// is in progress.
    /// </summary>
    Task AbortMergeAsync(RepoRow? repo, string branch, CancellationToken ct = default);
}

/// <summary>
/// Phase 79c — outcome of <see cref="IStoryGitOps.MergeBaseIntoBranchAndDetectConflictsAsync"/>
/// or <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/>. The state
/// drives the orchestrator's resolver-loop control flow.
/// </summary>
public enum MergeState
{
    /// <summary>Merge succeeded (fast-forward or merge commit); caller pushes via <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/> with an empty dict.</summary>
    Clean,

    /// <summary>Merge paused on conflict markers; agent must resolve.</summary>
    Conflicted,

    /// <summary>Merge or push aborted for non-conflict reasons; caller stalls.</summary>
    MergeFailed,
}

/// <summary>
/// Phase 79c — return shape of <see cref="IStoryGitOps.MergeBaseIntoBranchAndDetectConflictsAsync"/>
/// and <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/>.
/// </summary>
public sealed record MergeResult(
    MergeState State,
    IReadOnlyList<ConflictedFile> ConflictedFiles,
    string? Detail);

/// <summary>
/// Phase 79 — a single conflicted file as seen during an in-progress
/// merge. <see cref="Content"/> is the raw on-disk text with
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
