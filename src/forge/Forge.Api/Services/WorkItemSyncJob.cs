// -----------------------------------------------------------------------
// <copyright file="WorkItemSyncJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 23.3 — background hosted service that syncs work items from
/// configured repos on a 20-minute cadence. Each tick dispatches up to
/// <see cref="SyncConstants.WorkItemSyncMaxParallel"/> repos in parallel
/// via a <see cref="SemaphoreSlim"/>; repos beyond the cap wait for the
/// next tick.
/// </summary>
public class WorkItemSyncJob : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<WorkItemSyncJob> logger;
    private readonly Random jitterRng = new();

    public WorkItemSyncJob(
        IServiceScopeFactory scopes,
        ILogger<WorkItemSyncJob> logger)
    {
        this.scopes = scopes;
        this.logger = logger;
    }

    /// <summary>Generates a jittered delay in [interval-60s, interval+60s].</summary>
    public TimeSpan NextJitteredInterval()
    {
        var baseMs = SyncConstants.WorkItemSyncIntervalSeconds * 1000;
        var jitterMs = this.jitterRng.Next(-60_000, 60_001);
        return TimeSpan.FromMilliseconds(Math.Max(1000, baseMs + jitterMs));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a short bit so the rest of the app can finish starting.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "WorkItemSyncJob tick failed");
            }

            await Task.Delay(this.NextJitteredInterval(), stoppingToken);
        }
    }

    internal async Task RunTickAsync(CancellationToken ct)
    {
        List<RepoRow> repos;
        using (var scope = this.scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var now = DateTimeOffset.UtcNow;
            var staleThreshold = now.AddMinutes(-SyncConstants.RunningTimeoutMinutes).ToString("o");

            repos = await db.Repos
                .Where(r =>
                    r.LastSyncStatus == "idle"
                    || r.LastSyncStatus == "failed"
                    || (r.LastSyncStatus == "running" && r.IsRunningSince != null
                        && string.Compare(r.IsRunningSince, staleThreshold) < 0))
                .OrderBy(r => r.LastSyncAt ?? string.Empty)
                .Take(SyncConstants.WorkItemSyncMaxParallel)
                .ToListAsync(ct);
        }

        if (repos.Count == 0) return;

        using var semaphore = new SemaphoreSlim(SyncConstants.WorkItemSyncMaxParallel);
        var tasks = repos.Select(repo => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await this.SyncRepoAsync(repo.Id, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }, ct));

        await Task.WhenAll(tasks);
    }

    internal async Task SyncRepoAsync(string repoId, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"sr-{Guid.NewGuid():N}";

        using var scope = this.scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null) return;

        // Mark as running.
        repo.LastSyncStatus = "running";
        repo.IsRunningSince = startedAt.ToString("o");
        await db.SaveChangesAsync(ct);

        var syncService = scope.ServiceProvider.GetRequiredService<WorkItemSyncService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ForgeHub>>();

        var kind = string.IsNullOrEmpty(repo.LastSyncAt) ? "full" : "delta";

        // Build filters from the repo's sync configuration.
        DateTimeOffset? updatedFrom = null;
        if (!string.IsNullOrEmpty(repo.LastSyncAt)
            && DateTimeOffset.TryParse(repo.LastSyncAt, out var lastSync))
        {
            updatedFrom = lastSync;
        }
        else
        {
            // First sync: use the initial window.
            var windowDays = repo.InitialSyncWindowDays
                ?? (string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase)
                    ? (int?)null
                    : SyncConstants.AdoInitialWindowDays);
            if (windowDays.HasValue)
            {
                updatedFrom = DateTimeOffset.UtcNow.AddDays(-windowDays.Value);
            }
        }

        var filters = new WorkItemSyncFilters(UpdatedFrom: updatedFrom);

        try
        {
            var result = await syncService.SyncAsync(repo, filters, ignoreThrottle: true, ct);

            if (result.Error is not null)
            {
                repo.LastSyncStatus = "failed";
                repo.LastSyncError = result.Error;
            }
            else
            {
                repo.LastSyncAt = startedAt.ToString("o");
                repo.LastSyncStatus = "idle";
                repo.LastSyncCursor = null;
                repo.LastSyncError = null;
            }

            repo.IsRunningSince = null;

            db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
            {
                Id = runId,
                RepoId = repoId,
                Kind = kind,
                StartedAt = startedAt.ToString("o"),
                FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
                ItemsAdded = result.Inserted,
                ItemsUpdated = result.Updated,
                ItemsConflicted = 0,
                ErrorMessage = result.Error,
                CompletedSuccessfully = result.Error is null,
            });

            await db.SaveChangesAsync(ct);

            this.logger.LogInformation(
                "WorkItemSync {RepoId}: {Kind} completed — {Pulled} pulled, {Inserted} inserted, {Updated} updated",
                repoId, kind, result.Pulled, result.Inserted, result.Updated);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "WorkItemSync {RepoId}: {Kind} failed", repoId, kind);

            // Reload in case tracking was lost.
            var freshRepo = await db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
            if (freshRepo is not null)
            {
                freshRepo.LastSyncStatus = "failed";
                freshRepo.LastSyncError = ex.Message;
                freshRepo.IsRunningSince = null;
            }

            db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
            {
                Id = runId,
                RepoId = repoId,
                Kind = kind,
                StartedAt = startedAt.ToString("o"),
                FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
                ItemsAdded = 0,
                ItemsUpdated = 0,
                ItemsConflicted = 0,
                ErrorMessage = ex.Message,
                CompletedSuccessfully = false,
            });

            await db.SaveChangesAsync(ct);
        }
    }
}
