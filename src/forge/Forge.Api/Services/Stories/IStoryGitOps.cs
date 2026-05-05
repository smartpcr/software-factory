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
}
