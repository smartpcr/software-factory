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
    /// Write each <c>files</c> entry into the working tree at the given branch
    /// and commit them with <paramref name="message"/>. Returns the synthetic
    /// commit sha. Tests use this to verify that a merged PR actually carries
    /// the expected files.
    /// </summary>
    Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default);

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
    /// </summary>
    Task<string> EnsureWorktreeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default);

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
}
