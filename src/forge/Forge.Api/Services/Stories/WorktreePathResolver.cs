// -----------------------------------------------------------------------
// <copyright file="WorktreePathResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Single source of truth for "given a work-item id, where on disk is
/// its worktree?". Probes the two surviving conventions in priority order
/// and returns the first one that actually exists:
/// <list type="number">
/// <item><b>Canonical engineer workstream:</b>
///   <c>{cloneRoot}/.worktree/stage-{phaseNum}.{stageNum}-{stageTitle}</c>
///   — produced by <see cref="WorkstreamWorktreeSlug.Compute"/>.</item>
/// <item><b>Crew-queue ad-hoc:</b>
///   <c>{cloneRoot}/.worktree/ws-{sanitizedId}</c> — produced by
///   <c>CrewQueueWorker</c> for non-spawn work items.</item>
/// </list>
/// Returns null when no candidate exists on disk. Callers that need to
/// CREATE a worktree must use <see cref="WorkstreamWorktreeSlug.Compute"/>
/// directly; this helper only looks at what's already on disk.
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

        // Candidate 1 — canonical engineer-workstream slug. Only attempt
        // when the row has a StorySpawnId; without a spawn we have no
        // sibling list to compute position numbers from.
        if (!string.IsNullOrEmpty(ws.StorySpawnId))
        {
            var siblings = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == ws.StorySpawnId)
                .ToListAsync(ct);
            if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
            var canonicalSlug = WorkstreamWorktreeSlug.Compute(ws, siblings);
            var canonicalPath = Path.Combine(cloneRoot, ".worktree", canonicalSlug);
            if (Directory.Exists(canonicalPath))
            {
                return new WorktreePathResult(canonicalPath, branch, cloneRoot);
            }
        }

        // Candidate 2 — crew-queue ad-hoc convention.
        var sanitizedId = ws.Id.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
        var crewQueuePath = Path.Combine(cloneRoot, ".worktree", $"ws-{sanitizedId}");
        if (Directory.Exists(crewQueuePath))
        {
            return new WorktreePathResult(crewQueuePath, branch, cloneRoot);
        }

        // Nothing on disk — worktree was cleaned up post-merge or never
        // claimed yet. Surface the branch + cloneRoot so the caller can
        // still render "no worktree attached" with useful context.
        return new WorktreePathResult(WorktreePath: null, Branch: branch, RepoCloneRoot: cloneRoot);
    }

    /// <summary>
    /// Phase 53 — try to find the on-disk worktree for a sequential plan
    /// doc. Returns the per-doc worktree path
    /// (<c>{cloneRoot}/.worktree/plan-{docKey}</c>) when the spawn's repo
    /// is bound and the directory exists; otherwise returns
    /// <see cref="WorktreePathResult.Empty"/>. Callers pass the
    /// <paramref name="docKey"/> (one of
    /// <see cref="PlanDocStateHelpers.PlanDocOrder"/>) — the helper
    /// resolves the slug via
    /// <see cref="PlanDocStateHelpers.CanonicalDocWorktreeSlug(string)"/>
    /// so the resolution stays in lock-step with the orchestrator.
    /// </summary>
    /// <param name="db">DB context.</param>
    /// <param name="spawnId">Story spawn id (<see cref="StorySpawnRow.Id"/>).</param>
    /// <param name="docKey">Plan-doc key — e.g. "architecture",
    ///   "tech-spec". Must match an entry in
    ///   <see cref="PlanDocStateHelpers.PlanDocOrder"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<WorktreePathResult> ResolveForPlanDocAsync(
        ForgeDbContext db, string spawnId, string docKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(spawnId) || string.IsNullOrEmpty(docKey))
        {
            return WorktreePathResult.Empty;
        }

        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (spawn is null) return WorktreePathResult.Empty;

        if (string.IsNullOrEmpty(spawn.RepoId)) return WorktreePathResult.Empty;
        var repo = await db.Repos.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot))
        {
            return WorktreePathResult.Empty;
        }

        // The branch we report for the doc is whatever the orchestrator
        // recorded (or its canonical value if the doc hasn't been started
        // yet). FindDocBranch handles both legacy + sequential paths.
        string branch;
        try
        {
            branch = PlanDocStateHelpers.FindDocBranch(spawn, docKey);
        }
        catch (ArgumentException)
        {
            // Unknown docKey — no worktree to resolve.
            return WorktreePathResult.Empty;
        }

        // Probe story-scoped slug first (new convention), then legacy
        // slug (no story prefix) for backward compatibility.
        var scopedSlug = PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, docKey);
        var scopedPath = Path.Combine(repo.CloneRoot, ".worktree", scopedSlug);
        if (Directory.Exists(scopedPath))
        {
            return new WorktreePathResult(scopedPath, branch, repo.CloneRoot);
        }

        var legacySlug = PlanDocStateHelpers.CanonicalDocWorktreeSlug(docKey);
        var legacyPath = Path.Combine(repo.CloneRoot, ".worktree", legacySlug);
        if (Directory.Exists(legacyPath))
        {
            return new WorktreePathResult(legacyPath, branch, repo.CloneRoot);
        }

        // Worktree not on disk yet (pre-claim) or cleaned up post-merge.
        // Surface the branch + cloneRoot so callers can still render
        // "no worktree attached" with useful context.
        return new WorktreePathResult(WorktreePath: null, Branch: branch, RepoCloneRoot: repo.CloneRoot);
    }

    /// <summary>
    /// 2026-05-14 — write-side canonical spec for an engineer workstream.
    /// Returns the branch, slug, and absolute worktree path the
    /// orchestrator + worker MUST use; null when <paramref name="workItemId"/>
    /// is not a spawn-bound workstream (ad-hoc work items take the legacy
    /// <c>ws-{sanitizedId}</c> path at the caller).
    /// <para>Single source of truth: every code path that creates an
    /// engineer worktree (StorySpawnOrchestrator's spawn-flow,
    /// CrewQueueWorker's standard pipeline, the boot-crash-recovery
    /// sweep) routes through this so paths and branches stay in
    /// lock-step.</para>
    /// </summary>
    public static async Task<CanonicalWorkstreamPathSpec?> GetCanonicalForWorkstreamAsync(
        ForgeDbContext db, string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return null;

        var ws = await db.Workstreams.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is null) return null;

        if (string.IsNullOrEmpty(ws.RepoId)) return null;
        var repo = await db.Repos.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == ws.RepoId, ct);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return null;

        // Sibling list drives WorkstreamWorktreeSlug.Compute's phase/stage
        // numbering. Standalone workstreams (no StorySpawnId) get a
        // single-element list so the helper takes its legacy fallback.
        List<WorkstreamRow> siblings;
        if (string.IsNullOrEmpty(ws.StorySpawnId))
        {
            siblings = new List<WorkstreamRow> { ws };
        }
        else
        {
            siblings = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == ws.StorySpawnId)
                .ToListAsync(ct);
            if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
        }

        var slug = WorkstreamWorktreeSlug.Compute(ws, siblings);
        var path = Path.Combine(repo.CloneRoot, ".worktree", slug);
        var branch = !string.IsNullOrEmpty(ws.Branch) ? ws.Branch! : $"ws/{workItemId}";

        // 2026-05-14 — route the base-branch resolution through the shared
        // WorkItemBaseBranchResolver so engineer workstreams open PRs
        // against the spawn's integration branch (e.g. `feature/teams`)
        // rather than the repo's default (e.g. `main`). The previous code
        // hard-coded `repo.DefaultBranch` here, which silently bypassed
        // the architect's chosen feature branch for every spawn-bound
        // workstream — observed on PR smartpcr/qq#14 (ASP.NET Core Bot
        // Host): spawn.BaseBranch=`feature/teams`, ws.Base=`feature/teams`,
        // but the PR opened with base=`main` and auto-merge landed
        // engineer work straight onto trunk.
        var baseBranch = await WorkItemBaseBranchResolver.ResolveAsync(db, workItemId, ct);

        return new CanonicalWorkstreamPathSpec(branch, slug, path, repo.CloneRoot!, baseBranch, ws, repo);
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

/// <summary>
/// 2026-05-14 — canonical write-side spec for an engineer workstream.
/// Unlike <see cref="WorktreePathResult"/> (which probes disk and may
/// return null when nothing is created yet), this bundle is the
/// authoritative "where SHOULD the worktree live and what branch SHOULD
/// it host" answer. <see cref="CrewQueueWorker"/>'s boot-crash-recovery
/// path consumes it so a recovery attempt reuses the spawn-flow's
/// canonical directory + branch instead of synthesising
/// <c>ws/{workItemId}</c> + <c>.worktree/ws-{workItemId}</c> (which
/// double-prefixed to <c>ws/ws-…</c> / <c>.worktree/ws-ws-…</c> for any
/// engineer workstream whose id already started with <c>ws-</c>,
/// observed 2026-05-14 on <c>qq:MICROSOFT-TEAMS-MESS</c> stage 2.1).
/// </summary>
public sealed record CanonicalWorkstreamPathSpec(
    string Branch,
    string Slug,
    string WorktreePath,
    string RepoCloneRoot,
    string BaseBranch,
    WorkstreamRow Workstream,
    RepoRow Repo);
