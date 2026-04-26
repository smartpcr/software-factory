// -----------------------------------------------------------------------
// <copyright file="IGitWorktreeService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

/// <summary>
/// Thin wrapper over <c>git worktree</c> + a few read-only stat queries
/// (ahead/behind/conflicts/conflict-paths). All write paths must keep new
/// worktrees inside <see cref="GitWorktreeOptions.WorktreeRoot"/>.
/// </summary>
public interface IGitWorktreeService
{
    /// <summary>Lists worktrees registered with the repo (one per line of <c>git worktree list --porcelain</c>).</summary>
    Task<IReadOnlyList<WorktreeEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>Creates a worktree at <paramref name="path"/> tracking <paramref name="branch"/> off <paramref name="baseBranch"/>.</summary>
    Task CreateAsync(string path, string branch, string baseBranch, CancellationToken ct = default);

    /// <summary>Removes a worktree (force when needed).</summary>
    Task RemoveAsync(string path, bool force, CancellationToken ct = default);

    /// <summary>Returns ahead/behind counts via <c>git rev-list --left-right --count base...branch</c>.</summary>
    Task<AheadBehind> AheadBehindAsync(string baseBranch, string branch, CancellationToken ct = default);

    /// <summary>Returns the list of paths with merge conflicts via <c>git merge-tree</c>; empty when clean.</summary>
    Task<IReadOnlyList<string>> ConflictPathsAsync(string baseBranch, string branch, CancellationToken ct = default);
}

public sealed record WorktreeEntry(string Path, string Branch, string Head);

public sealed record AheadBehind(int Ahead, int Behind);
