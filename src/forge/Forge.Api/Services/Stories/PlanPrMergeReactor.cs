// -----------------------------------------------------------------------
// <copyright file="PlanPrMergeReactor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 45 follow-up — when a story spawn's plan PR transitions to
/// <c>merged</c>, every still-running plan-doc gen↔eval loop becomes
/// wasted work: the operator's intent is "this plan ships", and the
/// architect's job is done. Without a stop signal those loops keep
/// burning tokens (any re-entry path — Resume click, scheduler tick,
/// boot recovery via <c>StoryResumeRecoveryService</c>, base-branch-change
/// replay — re-fires the strategy and produces a fresh iteration).
///
/// <para>
/// Called from both upstream paths that flip <see cref="PrRow.Col"/> to
/// <c>merged</c>: <c>ProviderStoryProviderOps.MergePrAsync</c> (Forge-issued
/// merge during ACCEPT) and <c>PrSyncJob.RunSyncAsync</c> (when ADO/GitHub
/// reports the upstream PR is now merged). Both observe the transition,
/// both should stop the loops.
/// </para>
///
/// <para>
/// Idempotent: re-firing on an already-handled spawn is a no-op (every
/// plan-doc work item is already <c>idle</c> + has no in-flight invocations).
/// </para>
/// </summary>
internal static class PlanPrMergeReactor
{
    /// <summary>
    /// Stop every in-flight architect loop for the spawn whose plan PR is
    /// <paramref name="prId"/>. Returns the number of plan-doc work items
    /// touched so callers can log a meaningful summary.
    /// </summary>
    public static async Task<int> OnPlanPrMergedAsync(
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub>? hub,
        string prId,
        ILogger? log,
        CancellationToken ct)
    {
        // Phase 53 — locate the spawn owning this PR using the helper that
        // covers both the legacy single-PR column (PlanPrId) and the new
        // sequential-mode JSON column (PlanDocPrsJson[*].PrId).
        var match = await PlanDocStateHelpers.FindByPrIdAsync(db, prId, ct);
        if (match is null) return 0;
        var spawn = match.Value.Spawn;
        var matchedDocKey = match.Value.DocKey;

        // For legacy spawns (matchedDocKey == null) cancel every plan-doc
        // loop. For sequential spawns cancel only the merged doc's loop —
        // upstream merges must not interrupt downstream docs that may
        // already be running.
        var prefix = $"plan-doc:{spawn.Id}:";
        IQueryable<WorkItemRow> q = db.WorkItems.Where(w => w.Id.StartsWith(prefix));
        if (matchedDocKey is not null
            && PlanDocStateHelpers.PlanDocFileName.TryGetValue(matchedDocKey, out var fileName))
        {
            var docId = $"plan-doc:{spawn.Id}:{fileName}";
            q = db.WorkItems.Where(w => w.Id == docId);
        }
        var docWorkItems = await q.ToListAsync(ct);
        if (docWorkItems.Count == 0) return 0;

        var cancelled = 0;
        foreach (var wi in docWorkItems)
        {
            try
            {
                await runner.CancelByTaskAsync(wi.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogWarning(ex,
                    "PlanPrMergeReactor: CancelByTaskAsync failed for {WorkItem}; the row will still be flipped to idle.",
                    wi.Id);
            }
            if (wi.ExecutionStatus is "live" or "queued")
            {
                wi.ExecutionStatus = "idle";
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // Also cancel any TaskQueue rows so the queue worker doesn't
            // fire one final iteration after we already cancelled the
            // running invocation.
            var queueRows = await db.TaskQueue
                .Where(qr => qr.WorkItemId == wi.Id && qr.State != "completed" && qr.State != "cancelled")
                .ToListAsync(ct);
            foreach (var qrow in queueRows)
            {
                qrow.State = "cancelled";
                qrow.CompletedAt = DateTimeOffset.UtcNow;
                qrow.LastUpdatedAt = DateTimeOffset.UtcNow;
            }
            cancelled++;
        }

        // Drop any pending Resume markers — boot recovery would otherwise
        // re-fire the loops on next startup even though they're now done.
        if (!string.IsNullOrEmpty(spawn.ResumePendingJson))
        {
            spawn.ResumePendingJson = null;
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("story-spawn.changed",
                new { kind = "plan-pr-merged-cancelled", storyId = spawn.StoryId, cancelledCount = cancelled }, ct);
        }

        log?.LogInformation(
            "PlanPrMergeReactor: plan PR {PrId} merged; cancelled {Count} plan-doc loop(s) for spawn {SpawnId}.",
            prId, cancelled, spawn.Id);

        return cancelled;
    }
}
