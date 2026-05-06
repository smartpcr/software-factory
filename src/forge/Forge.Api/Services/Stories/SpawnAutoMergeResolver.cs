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
        ForgeDbContext db, string? workItemId, CancellationToken ct = default)
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
}
