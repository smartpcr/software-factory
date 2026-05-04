// -----------------------------------------------------------------------
// <copyright file="SpawnDependencyScheduler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// forge_implementation.md Phase 28 — hosted service that ticks the
/// <see cref="StorySpawnOrchestrator"/> for every active spawn at a slow
/// cadence. Two responsibilities:
///
/// <list type="bullet">
/// <item>Resume in-flight spawns after server restart (CC.5).</item>
/// <item>Auto-advance downstream workstreams when an upstream PR merges (28.5.7 / 28.11.6).</item>
/// </list>
///
/// Tick is also reachable directly via <see cref="TickOnceAsync"/> so tests
/// don't need to wait for the timer.
/// </summary>
public sealed class SpawnDependencyScheduler : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly TimeSpan interval;
    private readonly ILogger<SpawnDependencyScheduler>? logger;

    public SpawnDependencyScheduler(IServiceProvider services, ILogger<SpawnDependencyScheduler>? logger = null)
    {
        this.services = services;
        this.logger = logger;
        this.interval = TimeSpan.FromSeconds(15);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial delay so we don't race with EnsureCreated / migrations on
        // the very first boot.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await this.TickOnceAsync(ct);
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "SpawnDependencyScheduler tick failed");
            }
            try { await Task.Delay(this.interval, ct); } catch { return; }
        }
    }

    /// <summary>One-shot tick — exposed for tests and the admin endpoint.</summary>
    public async Task TickOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> activeStoryIds;
        using (var scope = this.services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            // Include Done-phase spawns: when the QA pass advances the phase
            // to "done", the WI/ProductStory closure work happens on the
            // *next* drive (DriveDoneAsync). Filtering them out here would
            // strand the spawn at done with the work item still active.
            // DriveDoneAsync short-circuits once the WI state is already
            // closed, so this remains effectively a one-shot per spawn.
            activeStoryIds = await db.StorySpawns
                .AsNoTracking()
                .Select(s => s.StoryId)
                .ToListAsync(ct);
        }
        var orchestrator = this.services.GetRequiredService<StorySpawnOrchestrator>();
        foreach (var sid in activeStoryIds)
        {
            await orchestrator.DriveAsync(sid, ct);
        }
    }
}
