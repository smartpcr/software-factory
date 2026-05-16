// -----------------------------------------------------------------------
// <copyright file="Phase38Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 38 (Layer B.4) — backfill <see cref="WorkItemRow"/> rows for the
/// plan-docs and workstreams that existed before the orchestrator started
/// creating them inline. Idempotent: each row is inserted only when no row
/// with the same id already exists.
///
/// Plan docs:
///   id = "plan-doc:{spawn.Id}:{filename}", track=design, type=step,
///   parentId=spawn.StoryId, points = max(3, spawn.StoryPoints / 4).
///   The 3-point floor was added 2026-05-13 (Phase 60) so plan-docs
///   always get ≥90 turns / ≥15 min wall-clock; see
///   <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator"/>
///   for the matching live-create path floor.
///
/// Workstreams:
///   id = workstream.Id, track=dev, type=task, parentId=spawn.StoryId,
///   points = max(1, spawn.StoryPoints / max(1, sibling-count)) — best-effort
///   when the original decomposition's <c>estimatedEffort</c> string isn't
///   accessible from this migration. New workstreams flow through
///   <c>StorySpawnOrchestrator.PersistDecompositionAsync</c> which parses
///   the field; the migration only patches up the historical rows.
/// </summary>
public static class Phase38Migration
{
    private static readonly string[] PlanDocFileNames = new[]
    {
        "tech-spec.md",
        "architecture.md",
        "implementation-plan.md",
        "e2e-scenarios.md",
    };

    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var spawns = await db.StorySpawns.AsNoTracking().ToListAsync(ct);
        var workItems = await db.WorkItems.AsNoTracking().Select(w => w.Id).ToListAsync(ct);
        var existing = new HashSet<string>(workItems, StringComparer.Ordinal);
        var inserts = new List<WorkItemRow>();

        // Plan docs.
        foreach (var spawn in spawns)
        {
            var docPoints = Math.Max(3, spawn.StoryPoints / 4);
            foreach (var fileName in PlanDocFileNames)
            {
                var id = $"plan-doc:{spawn.Id}:{fileName}";
                if (existing.Contains(id)) continue;
                inserts.Add(new WorkItemRow
                {
                    Id = id,
                    ParentId = spawn.StoryId,
                    RepoId = spawn.RepoId,
                    Provider = "local",
                    ProviderRef = null,
                    Track = "design",
                    Type = "step",
                    State = "active",
                    Title = $"plan: {fileName}",
                    DescriptionMd = $"Plan-phase artifact `{fileName}` for story `{spawn.StoryTitle}`.",
                    Points = docPoints,
                    Priority = "med",
                    CreatedAt = spawn.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                existing.Add(id);
            }
        }

        // Workstreams. Group by spawn so the per-task points share story
        // budget proportionally when no estimatedEffort hint is available.
        var workstreams = await db.Workstreams.AsNoTracking().ToListAsync(ct);
        var byStorySpawn = workstreams
            .Where(w => !string.IsNullOrEmpty(w.StorySpawnId))
            .GroupBy(w => w.StorySpawnId!, StringComparer.Ordinal);
        var spawnsById = spawns.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var grp in byStorySpawn)
        {
            if (!spawnsById.TryGetValue(grp.Key, out var spawn)) continue;
            var perWs = Math.Max(1, spawn.StoryPoints / Math.Max(1, grp.Count()));
            foreach (var w in grp)
            {
                if (existing.Contains(w.Id)) continue;
                inserts.Add(new WorkItemRow
                {
                    Id = w.Id,
                    ParentId = spawn.StoryId,
                    RepoId = spawn.RepoId,
                    Provider = "local",
                    ProviderRef = null,
                    Track = "dev",
                    Type = "task",
                    State = w.Status switch
                    {
                        "complete" => "closed",
                        "merging" or "review" => "resolved",
                        _ => "active",
                    },
                    Title = w.Name,
                    DescriptionMd = w.Summary ?? string.Empty,
                    Points = perWs,
                    Priority = "med",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                existing.Add(w.Id);
            }
        }

        if (inserts.Count > 0)
        {
            db.WorkItems.AddRange(inserts);
            await db.SaveChangesAsync(ct);
        }
    }
}
