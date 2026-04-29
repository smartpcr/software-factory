// -----------------------------------------------------------------------
// <copyright file="PrSyncJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Security.Cryptography;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services.Git;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Phase 24 — background service that periodically sweeps active PRs,
/// fetches upstream state from the provider (ADO / GitHub), detects state
/// transitions, syncs comments, and marks terminal PRs inactive.
/// </summary>
public sealed class PrSyncJob : BackgroundService
{
    /// <summary>Max allowed age before a "running" row is considered stale.</summary>
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEnumerable<IForgeProvider> providers;
    private readonly IGitWorktreeService worktreeService;
    private readonly WorktreeChangeFeed worktreeFeed;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<PrSyncJob> logger;

    public PrSyncJob(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IForgeProvider> providers,
        IGitWorktreeService worktreeService,
        WorktreeChangeFeed worktreeFeed,
        IHubContext<ForgeHub> hub,
        ILogger<PrSyncJob> logger)
    {
        this.scopeFactory = scopeFactory;
        this.providers = providers;
        this.worktreeService = worktreeService;
        this.worktreeFeed = worktreeFeed;
        this.hub = hub;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PrSyncJob sweep failed");
            }

            // 20 min ± 60s jitter
            var jitter = RandomNumberGenerator.GetInt32(-60, 61);
            var delay = TimeSpan.FromSeconds(SyncConstants.PrSyncIntervalSeconds + jitter);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        List<PrSyncCandidate> candidates;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var now = DateTimeOffset.UtcNow;
            var staleThreshold = now.Subtract(StaleRunningThreshold).ToString("o");

            candidates = await db.Prs
                .AsNoTracking()
                .Where(p => p.IsActive &&
                    (p.LastSyncStatus == "idle" ||
                     p.LastSyncStatus == "failed" ||
                     (p.LastSyncStatus == "running" && p.IsRunningSince != null && string.Compare(p.IsRunningSince, staleThreshold) < 0)))
                .Select(p => new PrSyncCandidate(p.Id, p.Provider, p.ProviderRef, p.Col, p.WorkItemId, p.RepoId))
                .ToListAsync(ct);
        }

        if (candidates.Count == 0) return;

        this.logger.LogInformation("PrSyncJob: {Count} PRs to sync", candidates.Count);

        using var semaphore = new SemaphoreSlim(SyncConstants.PrSyncMaxParallel);
        var tasks = candidates.Select(async c =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await this.RunSyncAsync(c, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task RunSyncAsync(PrSyncCandidate candidate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Mark running
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == candidate.PrId, ct);
            if (row is null) return;
            row.LastSyncStatus = "running";
            row.IsRunningSince = now.ToString("o");
            await db.SaveChangesAsync(ct);
        }

        try
        {
            if (string.IsNullOrEmpty(candidate.Provider) || string.IsNullOrEmpty(candidate.ProviderRef))
            {
                // No provider configured — mark idle
                await this.MarkSyncResultAsync(candidate.PrId, success: true, error: null, ct);
                return;
            }

            var provider = this.providers.FirstOrDefault(p => p.Kind == candidate.Provider && p.IsConfigured);
            if (provider is null)
            {
                await this.MarkSyncResultAsync(candidate.PrId, success: false, error: $"Provider '{candidate.Provider}' not configured", ct);
                return;
            }

            // Fetch upstream PR state
            var upstreamPr = await provider.GetPullRequestAsync(candidate.ProviderRef, ct);
            if (upstreamPr is null)
            {
                await this.MarkSyncResultAsync(candidate.PrId, success: false, error: $"Upstream PR {candidate.ProviderRef} not found", ct);
                return;
            }

            // Map upstream state → canonical state
            var mappedState = MapUpstreamState(candidate.Provider, upstreamPr.Col);
            var oldState = candidate.CurrentCol;
            var stateChanged = !string.Equals(oldState, mappedState, StringComparison.OrdinalIgnoreCase);
            var isTerminal = mappedState is "completed" or "abandoned";

            using var scope2 = this.scopeFactory.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var prRow = await db2.Prs.FirstOrDefaultAsync(p => p.Id == candidate.PrId, ct);
            if (prRow is null) return;

            if (stateChanged)
            {
                prRow.Col = mappedState;
                await this.hub.Clients.All.SendAsync("pr.changed", new { kind = "state", prId = candidate.PrId, from = oldState, to = mappedState }, ct);

                // Write audit row
                db2.Add(new PrCommentHistoryRow
                {
                    CommentId = 0,
                    Kind = "state-transition",
                    FromValue = oldState,
                    ToValue = mappedState,
                    ByUserId = "system:pr-sync",
                    At = now,
                    PayloadJson = $"{{\"prId\":\"{candidate.PrId}\"}}"
                });
            }

            if (isTerminal)
            {
                prRow.IsActive = false;
                await this.hub.Clients.All.SendAsync("pr.closed", new { prId = candidate.PrId, state = mappedState }, ct);

                // Worktree cleanup
                await this.CleanupWorktreeAsync(candidate, db2, ct);
            }

            // Success
            prRow.LastSyncAt = now.ToString("o");
            prRow.LastSyncStatus = "idle";
            prRow.LastSyncError = null;
            prRow.IsRunningSince = null;
            prRow.LastUpstreamUpdatedAt = upstreamPr.Age;
            await db2.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "PrSyncJob: sync failed for PR {PrId}", candidate.PrId);
            await this.MarkSyncResultAsync(candidate.PrId, success: false, error: ex.Message, ct);
        }
    }

    private async Task MarkSyncResultAsync(string prId, bool success, string? error, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
        if (row is null) return;

        if (success)
        {
            row.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
            row.LastSyncStatus = "idle";
            row.LastSyncError = null;
        }
        else
        {
            row.LastSyncStatus = "failed";
            row.LastSyncError = error;
        }

        row.IsRunningSince = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task CleanupWorktreeAsync(PrSyncCandidate candidate, ForgeDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate.WorkItemId) || string.IsNullOrEmpty(candidate.RepoId))
            return;

        try
        {
            // Resolve repo → CloneRoot
            var repo = await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == candidate.RepoId, ct);
            if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return;

            var worktreePath = Path.Combine(repo.CloneRoot, ".worktree", $"ws-{candidate.WorkItemId}");

            // Unsubscribe from change feed
            this.worktreeFeed.Unsubscribe(candidate.WorkItemId);

            // Remove worktree
            try
            {
                await this.worktreeService.RemoveAsync(worktreePath, force: true, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PrSyncJob: worktree remove failed for {Path}", worktreePath);
            }

            // Delete branch
            try
            {
                var branchName = $"ws/{candidate.WorkItemId}";
                var psi = new System.Diagnostics.ProcessStartInfo("git", $"branch -D {branchName}")
                {
                    WorkingDirectory = repo.CloneRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync(ct);
            }
            catch
            {
                // Swallow — branch may already be deleted
            }

            // Emit SignalR event
            await this.hub.Clients.All.SendAsync("task.worktree-removed", new { workItemId = candidate.WorkItemId, prId = candidate.PrId }, ct);

            // Write audit row
            db.Add(new PrCommentHistoryRow
            {
                CommentId = 0,
                Kind = "worktree-cleanup",
                FromValue = worktreePath,
                ToValue = null,
                ByUserId = "system:pr-sync",
                At = DateTimeOffset.UtcNow,
                PayloadJson = $"{{\"prId\":\"{candidate.PrId}\",\"workItemId\":\"{candidate.WorkItemId}\"}}"
            });
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "PrSyncJob: worktree cleanup failed for PR {PrId}", candidate.PrId);
        }
    }

    /// <summary>
    /// Maps provider-specific PR state strings to canonical states.
    /// ADO: Active→open, Completed→completed, Abandoned→abandoned.
    /// GitHub: open→open, closed+merged→completed, closed+!merged→abandoned.
    /// </summary>
    internal static string MapUpstreamState(string provider, string upstreamCol)
    {
        var col = upstreamCol.ToLowerInvariant();
        return provider.ToLowerInvariant() switch
        {
            "ado" => col switch
            {
                "active" => "open",
                "completed" => "completed",
                "abandoned" => "abandoned",
                _ => col
            },
            "github" => col switch
            {
                "open" => "open",
                "merged" or "completed" => "completed",
                "closed" => "abandoned",
                _ => col
            },
            _ => col
        };
    }

    private sealed record PrSyncCandidate(
        string PrId,
        string? Provider,
        string? ProviderRef,
        string CurrentCol,
        string? WorkItemId,
        string? RepoId);
}
