// -----------------------------------------------------------------------
// <copyright file="WorktreePathResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 49 follow-up — single source of truth for "given a work-item id,
/// where on disk is its worktree?". Production was carrying THREE distinct
/// conventions wired into different code paths:
/// <list type="number">
/// <item><b>New (Phase 49):</b>
///   <c>{cloneRoot}/.worktree/stage-{phaseNum}.{stageNum}-{anchorSlug}</c>
///   — the short, semantic name introduced to dodge Windows MAX_PATH.</item>
/// <item><b>Legacy story-spawn:</b>
///   <c>{cloneRoot}/.worktree/spawn-{sanitizedBranch}</c> — the
///   <see cref="RealStoryGitOps"/> default before Phase 49.</item>
/// <item><b>Legacy crew-queue:</b>
///   <c>{cloneRoot}/.worktree/ws-{sanitizedId}</c> — the
///   <c>CrewQueueWorker</c> convention for ad-hoc work items.</item>
/// </list>
///
/// <para>Multiple endpoints (Worktree tab, commits API, snapshot snapshot)
/// independently re-derived a path and routinely hit the wrong one
/// (observed 2026-05-10: PR #2 had 35 files but the Worktree tab showed
/// "+0 ~0 -0" because it was computing <c>ws-{id}</c> and the orchestrator
/// had written to <c>stage-1.1-…</c>). This helper enumerates the candidate
/// paths in priority order and returns the first one that actually exists
/// on disk, so display always agrees with reality regardless of when the
/// workstream was created.</para>
///
/// <para>The candidate list is intentionally narrow — only existing
/// directories are returned, never a "would-be" path. Callers that need to
/// CREATE a worktree must continue to use
/// <see cref="WorkstreamWorktreeSlug.Compute"/> directly; this helper only
/// looks at what's already on disk.</para>
/// </summary>
public static class WorktreePathResolver
{
    /// <summary>
    /// Try to find the on-disk worktree for an engineer workstream
    /// (<c>workstreams.Id == workItemId</c>). Returns null when no
    /// candidate directory exists on disk OR the workstream row is
    /// missing / has no repo.
    /// </summary>
    /// <param name="db">DB context (caller owns its lifetime).</param>
    /// <param name="workItemId">Work-item id; usually equals
    ///   <see cref="WorkstreamRow.Id"/> for engineer workstreams.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<WorktreePathResult> ResolveForWorkstreamAsync(
        ForgeDbContext db, string workItemId, CancellationToken ct = default)
    {
        var ws = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is null) return WorktreePathResult.Empty;

        var repo = string.IsNullOrEmpty(ws.RepoId)
            ? null
            : await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ws.RepoId, ct);

        return await ResolveForWorkstreamAsync(db, ws, repo, ct);
    }

    /// <summary>
    /// Overload for callers that already loaded the workstream + repo —
    /// avoids the duplicate DB query. <paramref name="repo"/> may be null
    /// (legacy rows without a repo bind); in that case the helper has
    /// nothing to look at and returns
    /// <see cref="WorktreePathResult.Empty"/>.
    /// </summary>
    public static async Task<WorktreePathResult> ResolveForWorkstreamAsync(
        ForgeDbContext db, WorkstreamRow ws, RepoRow? repo, CancellationToken ct = default)
    {
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot))
        {
            return new WorktreePathResult(WorktreePath: null, Branch: ws.Branch ?? string.Empty, RepoCloneRoot: null);
        }

        var cloneRoot = repo.CloneRoot;
        var branch = ws.Branch ?? string.Empty;

        // Candidate 1 — Phase 49 short name. Only attempt when the row
        // has a StorySpawnId; without a spawn we have no sibling list to
        // compute position numbers from.
        if (!string.IsNullOrEmpty(ws.StorySpawnId))
        {
            var siblings = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == ws.StorySpawnId)
                .ToListAsync(ct);
            if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
            var newSlug = WorkstreamWorktreeSlug.Compute(ws, siblings);
            var newPath = Path.Combine(cloneRoot, ".worktree", newSlug);
            if (Directory.Exists(newPath))
            {
                return new WorktreePathResult(newPath, branch, cloneRoot);
            }
        }

        // Candidate 2 — legacy story-spawn convention.
        if (!string.IsNullOrEmpty(branch))
        {
            var legacyPath = Path.Combine(cloneRoot, ".worktree", $"spawn-{SanitizeBranchSlug(branch)}");
            if (Directory.Exists(legacyPath))
            {
                return new WorktreePathResult(legacyPath, branch, cloneRoot);
            }
        }

        // Candidate 3 — legacy CrewQueueWorker convention.
        var sanitizedId = ws.Id.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
        var crewQueuePath = Path.Combine(cloneRoot, ".worktree", $"ws-{sanitizedId}");
        if (Directory.Exists(crewQueuePath))
        {
            return new WorktreePathResult(crewQueuePath, branch, cloneRoot);
        }

        // None on disk — the workstream's worktree was likely cleaned up
        // (Phase 49 post-merge cleanup) or never created yet. Return the
        // branch + cloneRoot so the caller can still surface "no worktree
        // attached" with useful context.
        return new WorktreePathResult(WorktreePath: null, Branch: branch, RepoCloneRoot: cloneRoot);
    }

    /// <summary>
    /// Mirrors <c>RealStoryGitOps.SanitizeBranchSlug</c> — replaces every
    /// non-alphanumeric / non-<c>-_.</c> character with <c>-</c> and trims
    /// edge dashes. Duplicated so this helper has no I/O dependency on
    /// <see cref="RealStoryGitOps"/>.
    /// </summary>
    private static string SanitizeBranchSlug(string branch)
    {
        var sb = new StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }
}

/// <summary>
/// Result of <see cref="WorktreePathResolver.ResolveForWorkstreamAsync(ForgeDbContext, string, CancellationToken)"/>.
/// <see cref="WorktreePath"/> is null when no on-disk candidate matched —
/// either the workstream was never claimed (no worktree created yet) or
/// the worktree was cleaned up after merge.
/// </summary>
public sealed record WorktreePathResult(
    string? WorktreePath,
    string Branch,
    string? RepoCloneRoot)
{
    public static WorktreePathResult Empty { get; } = new(null, string.Empty, null);
}
