// -----------------------------------------------------------------------
// <copyright file="SpawnAutoMergeResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves the <c>AutoMerge</c> flag for a work item by climbing up the
/// <see cref="WorkItemRow.ParentId"/> chain to the story root and reading
/// <see cref="StorySpawnRow.AutoMerge"/>. Used by PR-creation paths that
/// don't already get the flag through <see cref="OpenPrRequest"/>
/// (notably <c>CrewQueueWorker</c>) so a story-level "auto-merge me through"
/// pre-authorisation propagates to every PR opened for any task in any
/// workstream of that story.
/// </summary>
public static class SpawnAutoMergeResolver
{
    /// <summary>Cap on the parent-chain walk to defend against cycles.</summary>
    private const int MaxParentDepth = 16;

    /// <summary>
    /// Returns the spawn's <c>AutoMerge</c> flag for the given work item,
    /// or <c>false</c> when the work item has no parent spawn (ad-hoc
    /// items, items outside the story system, missing ids).
    /// </summary>
    public static async Task<bool> ResolveAsync(
        ControlDbContext control, ProductDbContext db, string? workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return false;

        // Pull every spawn's StoryId once — typically a handful of rows;
        // saves N round-trips during the parent walk.
        var storyIds = await db.StorySpawns
            .AsNoTracking()
            .ToDictionaryAsync(s => s.StoryId, s => s.AutoMerge, ct);
        if (storyIds.Count == 0) return false;

        var current = workItemId;
        for (var depth = 0; depth < MaxParentDepth && !string.IsNullOrEmpty(current); depth++)
        {
            if (storyIds.TryGetValue(current, out var autoMerge)) return autoMerge;

            var parent = await db.WorkItems
                .AsNoTracking()
                .Where(w => w.Id == current)
                .Select(w => w.ParentId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(parent)) return false;
            if (parent == current) return false; // self-loop guard
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Boot-time backfill: for every active PR whose parent spawn has
    /// <c>AutoMerge=true</c>, set <c>PrRow.AutoMerge=true</c>. Repairs PRs
    /// created before the propagation chain was wired through every PR-creation
    /// path; without this, existing rows sit on Col=review forever because
    /// <c>AutoMergeWatcher</c> only iterates <c>WHERE AutoMerge=true</c>.
    /// Returns the count of rows touched.
    /// </summary>
    public static async Task<int> BackfillPrsAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        // Build a set of story ids whose spawn has AutoMerge=true.
        var autoMergeStoryIds = await db.StorySpawns
            .AsNoTracking()
            .Where(s => s.AutoMerge)
            .Select(s => s.StoryId)
            .ToListAsync(ct);
        if (autoMergeStoryIds.Count == 0) return 0;

        var autoMergeSet = new HashSet<string>(autoMergeStoryIds, StringComparer.Ordinal);

        // Pull every still-active PR whose AutoMerge isn't set yet.
        var candidates = await db.Prs
            .Where(p => p.IsActive && !p.AutoMerge && p.WorkItemId != null)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var touched = 0;
        foreach (var pr in candidates)
        {
            // Walk parent chain, but using the cached set to avoid re-loading
            // story spawns per PR.
            var current = pr.WorkItemId;
            for (var depth = 0; depth < MaxParentDepth && !string.IsNullOrEmpty(current); depth++)
            {
                if (autoMergeSet.Contains(current))
                {
                    pr.AutoMerge = true;
                    touched++;
                    break;
                }
                var parent = await db.WorkItems
                    .AsNoTracking()
                    .Where(w => w.Id == current)
                    .Select(w => w.ParentId)
                    .FirstOrDefaultAsync(ct);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
        }
        if (touched > 0) await db.SaveChangesAsync(ct);
        return touched;
    }
}
