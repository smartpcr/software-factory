// -----------------------------------------------------------------------
// <copyright file="StoryResumeRecoveryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 41 — boot-time sweep that re-fires plan-doc Resume loops killed by
/// a previous process exit. The <c>POST /api/stories/{id}/spawn/artifacts/{key}/resume</c>
/// endpoint runs the gen↔eval loop as fire-and-forget <c>Task.Run</c>; before
/// this service the in-flight task died with the process and nothing
/// recovered it (no <c>TaskQueue</c> row, no <c>ExecutionStatus</c> flip,
/// scheduler tick only routes <c>phase=plan</c>). The endpoint now persists
/// each pending key into <see cref="StorySpawnRow.ResumePendingJson"/> before
/// kicking off the task, with a finally block that removes the key on
/// completion. On startup this service drains any keys still present and
/// re-fires <see cref="StoriesEndpoints.RunResumeWithCleanupAsync"/> per key
/// — same finally block, same cleanup contract.
/// </summary>
public sealed class StoryResumeRecoveryService : IHostedService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<StoryResumeRecoveryService> logger;

    public StoryResumeRecoveryService(IServiceScopeFactory scopeFactory, ILogger<StoryResumeRecoveryService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken ct) => this.SweepAsync(awaitCompletion: false, ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Drain pending Resume markers and re-fire the gen↔eval loop for each.
    /// When <paramref name="awaitCompletion"/> is true, awaits all per-key
    /// tasks before returning — used by integration tests so assertions can
    /// run after the recovery completes. Production startup uses false so a
    /// slow recovery doesn't block app boot.
    ///
    /// <para>Phase 68 — skip any pending key whose doc PR is already merged.
    /// A stale resume marker on a merged doc (e.g. left behind by a pre-
    /// Phase-68 Resume click that landed just before the PR auto-merged)
    /// would otherwise re-fire <see cref="StoriesEndpoints.RunResumeWithCleanupAsync"/>
    /// against a worktree that's been cleaned up, producing the iter-2
    /// score=0 phantom-run pattern (observed 2026-05-15 on
    /// code-intelligence:AGENT-MEMORY e2e-scenarios.md). We clear the
    /// stale marker as part of the skip so it doesn't re-fire on
    /// subsequent boots.</para>
    /// </summary>
    public async Task SweepAsync(bool awaitCompletion, CancellationToken ct)
    {
        List<(string SpawnId, string StoryId, string Key)> pending;
        List<(string SpawnId, string Key, string PrId)> skippedMerged;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await db.StorySpawns.AsNoTracking()
                .Where(s => s.ResumePendingJson != null && s.ResumePendingJson != string.Empty)
                .ToListAsync(ct);
            pending = new List<(string, string, string)>();
            skippedMerged = new List<(string, string, string)>();
            foreach (var row in rows)
            {
                foreach (var key in StoriesEndpoints.ParseResumePending(row.ResumePendingJson))
                {
                    // Phase 68 — skip merged plan-doc keys.
                    if (PlanDocStateHelpers.IsSequentialMode(row)
                        && PlanDocStateHelpers.PlanDocFileName.ContainsKey(key.ToLowerInvariant()))
                    {
                        var entry = PlanDocStateHelpers.GetEntry(row, key.ToLowerInvariant());
                        if (!string.IsNullOrEmpty(entry.PrId))
                        {
                            var pr = await db.Prs.AsNoTracking()
                                .FirstOrDefaultAsync(p => p.Id == entry.PrId, ct);
                            if (pr is not null
                                && string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                            {
                                skippedMerged.Add((row.Id, key, entry.PrId));
                                continue;
                            }
                        }
                    }
                    pending.Add((row.Id, row.StoryId, key));
                }
            }
        }

        // Clear stale resume-pending markers for merged docs so they don't
        // re-fire on every boot. Best-effort: log + continue on failure.
        if (skippedMerged.Count > 0)
        {
            using var cleanupScope = this.scopeFactory.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<ProductDbContext>();
            foreach (var (spawnId, key, prId) in skippedMerged)
            {
                this.logger.LogWarning(
                    "Phase 68 boot recovery: skipping stale Resume marker for spawn {SpawnId} key {Key} (PR {PrId} already merged); clearing marker.",
                    spawnId, key, prId);
                try
                {
                    var liveSpawn = await cleanupDb.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
                    if (liveSpawn is not null && StoriesEndpoints.RemoveResumePending(liveSpawn, key))
                    {
                        liveSpawn.UpdatedAt = DateTimeOffset.UtcNow;
                        await cleanupDb.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger.LogWarning(ex,
                        "Phase 68 boot recovery: failed to clear stale Resume marker for spawn {SpawnId} key {Key}; will retry next boot.",
                        spawnId, key);
                }
            }
        }

        if (pending.Count == 0) return;
        this.logger.LogInformation(
            "Boot recovery: re-firing {Count} pending plan-doc Resume task(s).",
            pending.Count);

        var tasks = new List<Task>(pending.Count);
        foreach (var (spawnId, storyId, key) in pending)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var scope = this.scopeFactory.CreateScope();
                var strategy = scope.ServiceProvider.GetRequiredService<IStoryArtifactStrategy>();
                await StoriesEndpoints.RunResumeWithCleanupAsync(
                    this.scopeFactory, strategy, spawnId, storyId, key, CancellationToken.None);
            }, ct));
        }

        if (awaitCompletion) await Task.WhenAll(tasks);
    }
}
