// -----------------------------------------------------------------------
// <copyright file="Phase40Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 40 — backfill <see cref="WorkItemRow"/> rows for every
/// <see cref="StorySpawnRow"/> whose StoryId has no matching WorkItemRow.
/// Without this, the Layer B plan-doc / workstream rows that set
/// <c>ParentId = spawn.StoryId</c> resolve to a non-existent parent and
/// "list all work items for this story" queries miss the children.
///
/// Idempotent: skipped when the row already exists. Newly-spawned stories
/// are persisted inline by <see cref="StorySpawnOrchestrator.RunOneStepAsync"/>;
/// this migration patches up legacy spawns from before that change.
/// </summary>
public static class Phase40Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var spawns = await db.StorySpawns.AsNoTracking().ToListAsync(ct);
        if (spawns.Count == 0) return;

        var existingIds = await db.WorkItems.AsNoTracking()
            .Where(w => spawns.Select(s => s.StoryId).Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(ct);
        var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);

        var inserts = new List<WorkItemRow>();
        foreach (var spawn in spawns)
        {
            if (existing.Contains(spawn.StoryId)) continue;
            inserts.Add(new WorkItemRow
            {
                Id = spawn.StoryId,
                Title = spawn.StoryTitle,
                DescriptionMd = spawn.StoryDescription ?? string.Empty,
                RepoId = spawn.RepoId,
                Provider = "local",
                ProviderRef = null,
                Track = "dev",
                Type = "feature",
                State = "active",
                Points = spawn.StoryPoints,
                Priority = "med",
                CreatedAt = spawn.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            existing.Add(spawn.StoryId);
        }

        if (inserts.Count > 0)
        {
            db.WorkItems.AddRange(inserts);
            await db.SaveChangesAsync(ct);
        }
    }
}
