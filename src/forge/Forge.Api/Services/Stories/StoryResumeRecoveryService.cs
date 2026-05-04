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
    /// </summary>
    public async Task SweepAsync(bool awaitCompletion, CancellationToken ct)
    {
        List<(string SpawnId, string StoryId, string Key)> pending;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var rows = await db.StorySpawns.AsNoTracking()
                .Where(s => s.ResumePendingJson != null && s.ResumePendingJson != string.Empty)
                .ToListAsync(ct);
            pending = new List<(string, string, string)>();
            foreach (var row in rows)
            {
                foreach (var key in StoriesEndpoints.ParseResumePending(row.ResumePendingJson))
                {
                    pending.Add((row.Id, row.StoryId, key));
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
