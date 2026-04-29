// -----------------------------------------------------------------------
// <copyright file="PrSyncJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using Forge.Api.Persistence;
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
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromMinutes(SyncConstants.RunningTimeoutMinutes);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IEnumerable<IPrCommentSyncClient> syncClients;
    private readonly IGitWorktreeService worktreeService;
    private readonly WorktreeChangeFeed worktreeFeed;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ForgeMetrics metrics;
    private readonly ILogger<PrSyncJob> logger;

    public PrSyncJob(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IPrCommentSyncClient> syncClients,
        IGitWorktreeService worktreeService,
        WorktreeChangeFeed worktreeFeed,
        IHubContext<ForgeHub> hub,
        ForgeMetrics metrics,
        ILogger<PrSyncJob> logger)
    {
        this.scopeFactory = scopeFactory;
        this.syncClients = syncClients;
        this.worktreeService = worktreeService;
        this.worktreeFeed = worktreeFeed;
        this.hub = hub;
        this.metrics = metrics;
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

    /// <summary>
    /// Run one tick. Public so an ad-hoc operator endpoint or a test can
    /// invoke it without spinning the BackgroundService loop.
    /// </summary>
    public async Task RunSweepAsync(CancellationToken ct)
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
                .OrderBy(p => p.LastSyncAt ?? string.Empty)
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

    /// <summary>
    /// Run sync for a single PR id. Public so the resync-this-PR endpoint
    /// can call it on demand (Q24.14 operator override).
    /// </summary>
    public async Task RunSyncForAsync(string prId, CancellationToken ct)
    {
        PrSyncCandidate? candidate;
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            candidate = await db.Prs.AsNoTracking()
                .Where(p => p.Id == prId)
                .Select(p => new PrSyncCandidate(p.Id, p.Provider, p.ProviderRef, p.Col, p.WorkItemId, p.RepoId))
                .FirstOrDefaultAsync(ct);
        }
        if (candidate is null) return;
        await this.RunSyncAsync(candidate, ct);
    }

    private async Task RunSyncAsync(PrSyncCandidate candidate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var provider = candidate.Provider ?? string.Empty;
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
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(candidate.ProviderRef))
            {
                await this.MarkSyncResultAsync(candidate.PrId, success: true, error: null, ct);
                this.metrics.RecordPrSync(provider, "no-provider", sw.Elapsed.TotalMilliseconds);
                return;
            }

            var client = this.syncClients.FirstOrDefault(c =>
                string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase) && c.IsConfigured);
            if (client is null)
            {
                await this.MarkSyncResultAsync(candidate.PrId, success: false,
                    error: $"Sync client for provider '{provider}' not configured", ct);
                this.metrics.RecordPrSync(provider, "unconfigured", sw.Elapsed.TotalMilliseconds);
                return;
            }

            // -- Fetch upstream snapshot --
            PrSyncSnapshot snapshot;
            using (var scopeRead = this.scopeFactory.CreateScope())
            {
                var dbRead = scopeRead.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var prFresh = await dbRead.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == candidate.PrId, ct);
                if (prFresh is null) return;
                snapshot = await client.FetchAsync(prFresh, ct);
            }

            // -- Apply state + upsert comments --
            await this.ApplySnapshotAsync(candidate, snapshot, ct);

            this.metrics.RecordPrSync(provider, "success", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "PrSyncJob: sync failed for PR {PrId}", candidate.PrId);
            await this.MarkSyncResultAsync(candidate.PrId, success: false, error: ex.Message, ct);
            this.metrics.RecordPrSync(provider, "failed", sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task ApplySnapshotAsync(PrSyncCandidate candidate, PrSyncSnapshot snapshot, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var prRow = await db.Prs.FirstOrDefaultAsync(p => p.Id == candidate.PrId, ct);
        if (prRow is null) return;

        var oldState = prRow.Col;
        var newState = snapshot.CanonicalState;
        var stateChanged = !string.Equals(oldState, newState, StringComparison.OrdinalIgnoreCase);
        var isTerminal = newState is "completed" or "abandoned";

        if (stateChanged)
        {
            prRow.Col = newState;
            this.metrics.StateTransition(oldState, newState);
            await this.hub.Clients.All.SendAsync("pr.changed",
                new { kind = "state", prId = candidate.PrId, from = oldState, to = newState }, ct);
            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = 0,
                Kind = "pr-state-transition",
                FromValue = oldState,
                ToValue = newState,
                ByUserId = "system:pr-sync",
                At = DateTimeOffset.UtcNow,
                PayloadJson = $"{{\"prId\":\"{candidate.PrId}\"}}",
            });
        }

        // -- Upsert comments --
        if (snapshot.CommentsFetched && snapshot.Comments.Count > 0)
        {
            await this.UpsertCommentsAsync(db, prRow, snapshot.Comments, ct);
        }

        prRow.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
        prRow.LastSyncStatus = "idle";
        prRow.LastSyncError = null;
        prRow.IsRunningSince = null;
        if (!string.IsNullOrEmpty(snapshot.UpstreamUpdatedAt))
        {
            prRow.LastUpstreamUpdatedAt = snapshot.UpstreamUpdatedAt;
        }

        if (isTerminal)
        {
            prRow.IsActive = false;
            await this.hub.Clients.All.SendAsync("pr.closed", new { prId = candidate.PrId, state = newState }, ct);
        }

        await db.SaveChangesAsync(ct);

        // -- Run linker (best effort) --
        if (snapshot.CommentsFetched)
        {
            try
            {
                var linker = new PrCommentLinker(db, this.logger as ILogger<PrCommentLinker> ?? new LoggerWrapper<PrCommentLinker>(this.logger), this.metrics);
                var worktreeRoot = await this.ResolveWorktreeRootAsync(db, candidate, ct);
                await linker.LinkAsync(candidate.PrId, worktreeRoot, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "PrSyncJob: linker failed for {PrId}", candidate.PrId);
            }
        }

        // -- Queue hook for un-addressed comments (Q24.8) --
        if (!isTerminal)
        {
            await this.MaybeEnqueueCommentResponseAsync(db, candidate, ct);
        }

        // -- Worktree cleanup on terminal --
        if (isTerminal)
        {
            await this.CleanupWorktreeAsync(candidate, db, newState, ct);
        }
    }

    private async Task UpsertCommentsAsync(ForgeDbContext db, PrRow pr, IReadOnlyList<PrSyncComment> upstream, CancellationToken ct)
    {
        var existing = await db.PrComments
            .Where(c => c.PrId == pr.Id)
            .ToListAsync(ct);
        var byProviderId = existing.ToDictionary(c => c.ProviderCommentId, c => c, StringComparer.Ordinal);
        var idByProviderId = existing.ToDictionary(c => c.ProviderCommentId, c => c.Id, StringComparer.Ordinal);

        // Insert pass — assign provisional ids via SaveChanges to resolve parents.
        foreach (var u in upstream)
        {
            if (byProviderId.TryGetValue(u.ProviderCommentId, out var local))
            {
                this.UpdateExisting(db, local, u, pr);
            }
            else
            {
                var row = new PrCommentRow
                {
                    PrId = pr.Id,
                    ProviderCommentId = u.ProviderCommentId,
                    ThreadId = u.ThreadId,
                    AuthorId = u.AuthorId,
                    AuthorKind = u.AuthorKind,
                    Body = u.Body,
                    FilePath = u.FilePath,
                    Line = u.Line,
                    State = u.State,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt,
                    ResolvedByUserId = u.ResolvedByUserId,
                    ResolvedAt = u.ResolvedAt,
                    RequiresAttention = u.AuthorKind != "agent" && u.State == "active",
                };
                db.PrComments.Add(row);
                this.metrics.Comments("added");
                if (row.RequiresAttention) this.metrics.AttentionFlip(toTrue: true);
            }
        }
        await db.SaveChangesAsync(ct);

        // Resolve parent links + opened history rows now that ids exist.
        foreach (var u in upstream)
        {
            if (string.IsNullOrEmpty(u.ParentProviderCommentId)) continue;
            var child = await db.PrComments.FirstOrDefaultAsync(c => c.PrId == pr.Id && c.ProviderCommentId == u.ProviderCommentId, ct);
            if (child is null || child.ParentCommentId.HasValue) continue;
            var parent = await db.PrComments.AsNoTracking().FirstOrDefaultAsync(c => c.PrId == pr.Id && c.ProviderCommentId == u.ParentProviderCommentId, ct);
            if (parent is not null) child.ParentCommentId = parent.Id;
        }

        // Opened-history for new rows (those without any history yet).
        var newRows = await db.PrComments
            .Where(c => c.PrId == pr.Id && !db.PrCommentHistory.Any(h => h.CommentId == c.Id))
            .ToListAsync(ct);
        foreach (var row in newRows)
        {
            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = row.Id,
                Kind = "opened",
                ToValue = row.State,
                ByUserId = row.AuthorId,
                At = row.CreatedAt,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private void UpdateExisting(ForgeDbContext db, PrCommentRow local, PrSyncComment u, PrRow pr)
    {
        if (u.UpdatedAt <= local.UpdatedAt && local.State == u.State && local.Body == u.Body) return;
        var prevState = local.State;
        var prevBody = local.Body;
        var prevAttention = local.RequiresAttention;

        if (!string.Equals(local.Body, u.Body, StringComparison.Ordinal))
        {
            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = local.Id,
                Kind = "edited",
                FromValue = prevBody.Length > 200 ? prevBody[..200] : prevBody,
                ToValue = u.Body.Length > 200 ? u.Body[..200] : u.Body,
                ByUserId = u.AuthorId,
                At = u.UpdatedAt,
            });
            this.metrics.Comments("updated");
        }

        if (!string.Equals(local.State, u.State, StringComparison.Ordinal))
        {
            string kind = u.State == "active" ? "reopened" : (u.State == "resolved" || u.State == "wont-fix" || u.State == "closed" ? "resolved" : "edited");
            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = local.Id,
                Kind = kind,
                FromValue = prevState,
                ToValue = u.State,
                ByUserId = u.ResolvedByUserId ?? u.AuthorId,
                At = u.UpdatedAt,
            });
            this.metrics.Comments(kind);
        }

        local.Body = u.Body;
        local.State = u.State;
        local.UpdatedAt = u.UpdatedAt;
        local.ResolvedByUserId = u.ResolvedByUserId;
        local.ResolvedAt = u.ResolvedAt;
        local.AuthorKind = u.AuthorKind;

        // Recompute RequiresAttention. Edits/reopens after a link invalidate it.
        var hadLink = local.LinkedCommitSha is not null || local.LinkedAgentResponseId.HasValue;
        if (u.State != "active")
        {
            local.RequiresAttention = false;
        }
        else
        {
            var bumped = u.UpdatedAt > local.UpdatedAt;
            local.RequiresAttention = local.AuthorKind != "agent" && (!hadLink || bumped);
        }

        if (prevAttention != local.RequiresAttention)
        {
            this.metrics.AttentionFlip(toTrue: local.RequiresAttention);
            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = local.Id,
                Kind = "attention-flipped",
                FromValue = prevAttention ? "true" : "false",
                ToValue = local.RequiresAttention ? "true" : "false",
                ByUserId = "system:pr-sync",
                At = DateTimeOffset.UtcNow,
            });
        }
    }

    private async Task MaybeEnqueueCommentResponseAsync(ForgeDbContext db, PrSyncCandidate candidate, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate.WorkItemId)) return;

        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == candidate.WorkItemId, ct);
        if (wi is null) return;

        // Q24.8 — enqueue only when the work item is in-review.
        var executionStatus = wi.ExecutionStatus ?? string.Empty;
        if (!string.Equals(wi.State, "in-review", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(executionStatus, "in-review", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hasAttention = await db.PrComments.AnyAsync(c => c.PrId == candidate.PrId && c.RequiresAttention, ct);
        if (!hasAttention) return;

        // Resolve crew id from AssignedAgentJson.
        var crewId = ResolveCrewId(wi.AssignedAgentJson);
        if (string.IsNullOrEmpty(crewId)) return;

        // Idempotency: skip when an open comment-response row already exists.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var existing = await db.TaskQueue
            .AnyAsync(r => r.WorkItemId == candidate.WorkItemId
                          && r.Kind == TaskQueueKinds.CommentResponse
                          && openStates.Contains(r.State), ct);
        if (existing) return;

        using var s = this.scopeFactory.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ITaskQueueService>();
        await queue.EnqueueAsync(candidate.WorkItemId!, "crew", crewId, ct, kind: TaskQueueKinds.CommentResponse);

        await this.hub.Clients.All.SendAsync("task.comment-needs-response",
            new { workItemId = candidate.WorkItemId, prId = candidate.PrId }, ct);
    }

    private static string? ResolveCrewId(string? assignedAgentJson)
    {
        if (string.IsNullOrEmpty(assignedAgentJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignedAgentJson);
            if (doc.RootElement.TryGetProperty("crewId", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                return c.GetString();
            if (doc.RootElement.TryGetProperty("CrewId", out var c2) && c2.ValueKind == System.Text.Json.JsonValueKind.String)
                return c2.GetString();
        }
        catch { /* swallow */ }
        return null;
    }

    private async Task<string?> ResolveWorktreeRootAsync(ForgeDbContext db, PrSyncCandidate candidate, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate.RepoId) || string.IsNullOrEmpty(candidate.WorkItemId)) return null;
        var repo = await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == candidate.RepoId, ct);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return null;
        return Path.Combine(repo.CloneRoot, ".worktree", $"ws-{candidate.WorkItemId}");
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

    private async Task CleanupWorktreeAsync(PrSyncCandidate candidate, ForgeDbContext db, string prState, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate.WorkItemId) || string.IsNullOrEmpty(candidate.RepoId))
            return;

        try
        {
            var repo = await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == candidate.RepoId, ct);
            if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return;

            var worktreePath = Path.Combine(repo.CloneRoot, ".worktree", $"ws-{candidate.WorkItemId}");
            this.worktreeFeed.Unsubscribe(candidate.WorkItemId);

            try
            {
                await this.worktreeService.RemoveAsync(worktreePath, force: true, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PrSyncJob: worktree remove failed for {Path}", worktreePath);
                this.metrics.Worktree("failed", prState);
            }

            try
            {
                var branchName = $"ws/{candidate.WorkItemId}";
                var psi = new System.Diagnostics.ProcessStartInfo("git", $"branch -D {branchName}")
                {
                    WorkingDirectory = repo.CloneRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync(ct);
            }
            catch
            {
                // Branch may already be deleted.
            }

            await this.hub.Clients.All.SendAsync(
                "task.worktree-removed",
                new { workItemId = candidate.WorkItemId, prId = candidate.PrId, prState },
                ct);

            db.PrCommentHistory.Add(new PrCommentHistoryRow
            {
                CommentId = 0,
                Kind = "worktree-cleanup",
                FromValue = worktreePath,
                ToValue = prState,
                ByUserId = "system:pr-sync",
                At = DateTimeOffset.UtcNow,
                PayloadJson = $"{{\"prId\":\"{candidate.PrId}\",\"workItemId\":\"{candidate.WorkItemId}\"}}",
            });
            await db.SaveChangesAsync(ct);
            this.metrics.Worktree("success", prState);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "PrSyncJob: worktree cleanup failed for PR {PrId}", candidate.PrId);
            this.metrics.Worktree("failed", prState);
        }
    }

    /// <summary>
    /// Maps provider-specific PR state strings to canonical states.
    /// ADO: Active→open, Completed→completed, Abandoned→abandoned.
    /// GitHub: open→open, closed+merged→completed, closed+!merged→abandoned.
    /// </summary>
    public static string MapUpstreamState(string provider, string upstreamCol)
    {
        var col = upstreamCol.ToLowerInvariant();
        return provider.ToLowerInvariant() switch
        {
            "ado" => col switch
            {
                "active" => "open",
                "completed" => "completed",
                "abandoned" => "abandoned",
                _ => col,
            },
            "github" => col switch
            {
                "open" => "open",
                "merged" or "completed" => "completed",
                "closed" => "abandoned",
                _ => col,
            },
            _ => col,
        };
    }

    public sealed record PrSyncCandidate(
        string PrId,
        string? Provider,
        string? ProviderRef,
        string CurrentCol,
        string? WorkItemId,
        string? RepoId);
}

internal sealed class LoggerWrapper<T> : ILogger<T>
{
    private readonly ILogger inner;

    public LoggerWrapper(ILogger inner) => this.inner = inner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this.inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => this.inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        this.inner.Log(logLevel, eventId, state, exception, formatter);
}
