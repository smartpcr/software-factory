// -----------------------------------------------------------------------
// <copyright file="WorkItemBaseBranchResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 49 follow-up — single source of truth for "what branch did this
/// work-item fork from?". Worktree-diff anchoring, ahead/behind
/// computation, PR base targeting, and the live <c>task.worktree-tick</c>
/// feed all need to agree on the same base or the operator sees
/// different file-counts depending on which surface they look at.
///
/// <para>Lookup order:</para>
/// <list type="number">
///   <item><description>If the work-item is an engineer workstream
///   (<see cref="WorkstreamRow"/>) attached to a story spawn, use
///   <see cref="StorySpawnRow.BaseBranch"/>. This is the case the
///   Worktree-tab cared about most (failover-cluster:XRAFT
///   branches off <c>feature/xraft</c>, not <c>main</c>).</description></item>
///   <item><description>Otherwise fall back to the work-item's repo
///   default branch (<see cref="RepoRow.DefaultBranch"/>).</description></item>
///   <item><description>Last resort <c>"main"</c> so callers always get
///   a non-empty string.</description></item>
/// </list>
///
/// <para>Replaces the inline lookup that
/// <see cref="Forge.Api.Endpoints.TaskExecutionEndpoints.GetCommits"/>
/// carried; <see cref="Forge.Api.Services.WorktreeChangeFeed"/> +
/// <see cref="Forge.Api.Services.CrewQueueWorker"/> were both diffing
/// against <c>main</c> regardless of the spawn before this helper
/// existed.</para>
/// </summary>
public static class WorkItemBaseBranchResolver
{
    /// <summary>
    /// Resolve the base branch for a work-item id. Always returns a
    /// non-empty string; defaults to <c>"main"</c> when nothing else
    /// resolves so callers can use the value as a ref name without a
    /// null check.
    /// </summary>
    /// <param name="db">DB context (caller owns its lifetime).</param>
    /// <param name="workItemId">The work-item id. May be a workstream
    ///   id, a plan-doc synthetic id, or a stand-alone work-item id —
    ///   the resolver tries each table in turn.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> ResolveAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return "main";

        // Workstream + spawn first — the most common engineer path.
        var ws = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is not null && !string.IsNullOrEmpty(ws.StorySpawnId))
        {
            var spawn = await db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (spawn is not null && !string.IsNullOrEmpty(spawn.BaseBranch))
            {
                return spawn.BaseBranch;
            }
        }

        // Plan-doc synthetic ids — `plan-doc:{spawnId}:{file}` — also
        // ride the spawn's base branch so the plan worktree diff
        // anchors against the same ref the engineer worktrees use.
        if (workItemId.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            var rest = workItemId["plan-doc:".Length..];
            var lastColon = rest.LastIndexOf(':');
            var spawnId = lastColon > 0 ? rest[..lastColon] : rest;
            var spawn = await db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            if (spawn is not null && !string.IsNullOrEmpty(spawn.BaseBranch))
            {
                return spawn.BaseBranch;
            }
        }

        // Repo default — covers crew-queue work items + workstreams
        // without a spawn binding.
        var wi = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        var repoId = wi?.RepoId ?? ws?.RepoId;
        if (!string.IsNullOrEmpty(repoId))
        {
            var repo = await control.Repos.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == repoId, ct);
            if (!string.IsNullOrEmpty(repo?.DefaultBranch))
            {
                return repo.DefaultBranch;
            }
        }

        return "main";
    }
}
