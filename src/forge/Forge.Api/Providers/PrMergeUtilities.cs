// -----------------------------------------------------------------------
// <copyright file="PrMergeUtilities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Helpers shared between the human-driven merge endpoint
/// (<c>PrsEndpoints.Merge</c>) and the background auto-merge watcher
/// (<see cref="AutoMergeWatcher"/>) so conflict detection and the
/// destructive <c>-X ours</c> resolution behave identically across both
/// paths.
/// </summary>
public static class PrMergeUtilities
{
    /// <summary>
    /// Narrow conflict detector. The provider exception message must contain
    /// an actual conflict signal — the literal word "merge" appears in every
    /// ADO error message ("ADO PR merge failed (...)") so a broader match
    /// would fire the destructive <c>-X ours</c> path on unrelated 4xx
    /// errors (404 wrong-repo, 405 method-not-allowed, 403 PAT, 401 auth).
    /// </summary>
    public static bool LooksLikeConflict(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        // ADO: "Cannot complete pull request because there are merge conflicts."
        // GitHub: "Pull Request is not mergeable" / "merge conflict"
        return message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not mergeable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("GitPullRequestNotMergeable", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pre-merge step: rebase upstream into the PR source branch and push.
    /// Returns <c>true</c> when the PR is now ready for the provider's
    /// complete-PR step (no conflict, or conflict resolved with
    /// <c>-X ours</c> when <paramref name="force"/> is true).
    /// Returns <c>false</c> when there's a conflict and force is off, when
    /// the PR is missing branch metadata, or when the local merge fails.
    /// </summary>
    /// <param name="force">
    /// <c>true</c> = auto-resolve conflicts with <c>-X ours</c> (destructive,
    /// drops upstream-side hunks). <c>false</c> = surface the conflict by
    /// returning false; caller decides what to do.
    /// </param>
    public static async Task<bool> RebaseSourceOnBaseAsync(
        ControlDbContext control, ProductDbContext db, IStoryGitOps git, PrRow pr, bool force, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pr.Branch) || string.IsNullOrEmpty(pr.Base)) return false;
        var repo = !string.IsNullOrEmpty(pr.RepoId)
            ? await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == pr.RepoId, ct)
            : null;
        repo ??= await db.Repos.AsNoTracking().FirstOrDefaultAsync(ct);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return false;
        return await git.MergeUpstreamIntoBranchAsync(repo, pr.Branch, pr.Base, force, ct);
    }
}
