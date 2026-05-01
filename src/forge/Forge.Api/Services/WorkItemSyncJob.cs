// -----------------------------------------------------------------------
// <copyright file="WorkItemSyncJob.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using System.Text.Json;
using Forge.Api.Contracts;
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
/// next tick. Phase 23.4 / 23.5 drive the providers' streaming API and
/// commit each batch in its own transaction so a mid-walk failure
/// resumes from <c>repo.LastSyncCursor</c> on the following run.
/// </summary>
public class WorkItemSyncJob : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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

        // Skip the seed placeholder repo — it satisfies the FK for seeded
        // cards/PRs but its URL points at a fictional org. Hammering ADO with
        // it produces a 401 every tick and the failure ends up in the UI.
        // The operator is expected to add their real repo via /products.
        var placeholders = repos.Where(IsSeedPlaceholder).ToList();
        foreach (var ph in placeholders)
        {
            await this.MarkPlaceholderAsync(ph.Id, ct);
        }
        repos = repos.Where(r => !IsSeedPlaceholder(r)).ToList();

        if (repos.Count == 0) return;

        using var semaphore = new SemaphoreSlim(SyncConstants.WorkItemSyncMaxParallel);
        var tasks = repos.Select(repo => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await this.SyncRepoAsync(repo.Id, kind: null, ct: ct);
            }
            finally
            {
                semaphore.Release();
            }
        }, ct));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Phase 23.4 / 23.5 — sync a single repo via the streaming batch API.
    /// Each yielded batch is committed in its own transaction; cursor
    /// advances only after the batch's items + repo cursor write together.
    /// On exception the cursor stays at the last successfully-committed
    /// batch so the next run resumes from the next batch.
    /// </summary>
    /// <param name="kind">Optional override; otherwise inferred from <c>LastSyncAt</c>.</param>
    internal async Task SyncRepoAsync(string repoId, string? kind, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var runId = $"sr-{Guid.NewGuid():N}";

        using var scope = this.scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null) return;

        // Mark as running.
        repo.LastSyncStatus = "running";
        repo.IsRunningSince = startedAt.ToString("o");
        await db.SaveChangesAsync(ct);

        var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IForgeProvider>>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ForgeHub>>();

        var resolvedKind = kind ?? (string.IsNullOrEmpty(repo.LastSyncAt) ? "full" : "delta");

        var provider = providers.FirstOrDefault(
            p => string.Equals(p.Kind, repo.Kind, StringComparison.OrdinalIgnoreCase));
        var providerLabel = repo.Kind ?? "unknown";

        // Build filters from the repo's sync configuration.
        DateTimeOffset? updatedFrom = null;
        if (!string.IsNullOrEmpty(repo.LastSyncAt)
            && DateTimeOffset.TryParse(repo.LastSyncAt, out var lastSync))
        {
            updatedFrom = lastSync;
        }
        else
        {
            // First sync: use the initial window (per-repo override or default).
            var windowDays = repo.InitialSyncWindowDays
                ?? (string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase)
                    ? (int?)null
                    : SyncConstants.AdoInitialWindowDays);
            if (windowDays.HasValue)
            {
                updatedFrom = DateTimeOffset.UtcNow.AddDays(-windowDays.Value);
            }
        }

        var areaPaths = ParseAreaPaths(repo.AreaPathsJson);
        // Resolve per-repo provider scope so this repo syncs against its own
        // ADO project / GitHub repo even when other repos use a different
        // global config.
        var adoOrgUrl = !string.IsNullOrWhiteSpace(repo.AdoOrg) ? BuildAdoOrgUrl(repo.AdoOrg!) : null;
        var ghRepo = !string.IsNullOrWhiteSpace(repo.GhOwner) && !string.IsNullOrWhiteSpace(repo.GhRepo)
            ? $"{repo.GhOwner}/{repo.GhRepo}"
            : null;
        var filters = new WorkItemSyncFilters(
            UpdatedFrom: updatedFrom,
            AreaPaths: areaPaths,
            StartCursor: repo.LastSyncCursor,
            AdoOrgUrl: adoOrgUrl,
            AdoProject: repo.AdoProject,
            GitHubRepo: ghRepo);

        var totalAdded = 0;
        var totalUpdated = 0;
        var totalConflicted = 0;
        var batchCount = 0;
        Exception? terminalEx = null;

        if (provider is null || !provider.IsConfigured)
        {
            terminalEx = new InvalidOperationException(
                provider is null
                    ? $"No provider registered for kind '{repo.Kind}'."
                    : $"Provider '{repo.Kind}' is not configured.");
        }
        else
        {
            try
            {
                await foreach (var batch in provider.StreamWorkItemBatchesAsync(filters, ct))
                {
                    batchCount++;
                    WorkItemSyncMetrics.PaginationBatches.Add(1, KeyVal("provider", providerLabel));

                    var (added, updated, conflicted) = await this.CommitBatchAsync(
                        db, repo, batch, hub, scope, runId, ct);
                    totalAdded += added;
                    totalUpdated += updated;
                    totalConflicted += conflicted;

                    // Per-batch cursor commit (Phase 23.6 resume guarantee).
                    repo.LastSyncCursor = batch.OpaqueCursor;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                terminalEx = ex;
            }
        }

        stopwatch.Stop();

        if (terminalEx is null)
        {
            // Successful run — advance LastSyncAt to the run START time so
            // a subsequent delta run doesn't miss items written DURING this
            // run; clear the cursor + error.
            repo.LastSyncAt = startedAt.ToString("o");
            repo.LastSyncStatus = "idle";
            repo.LastSyncCursor = null;
            repo.LastSyncError = null;
        }
        else
        {
            repo.LastSyncStatus = "failed";
            repo.LastSyncError = terminalEx.Message;
            // LastSyncAt + LastSyncCursor stay at their prior values so the
            // next run resumes the same window from the last committed batch.
        }
        repo.IsRunningSince = null;

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = runId,
            RepoId = repoId,
            Kind = resolvedKind,
            StartedAt = startedAt.ToString("o"),
            FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
            ItemsAdded = totalAdded,
            ItemsUpdated = totalUpdated,
            ItemsConflicted = totalConflicted,
            ErrorMessage = terminalEx?.Message,
            CompletedSuccessfully = terminalEx is null,
        });
        await db.SaveChangesAsync(ct);

        // Telemetry — duration histogram + counters.
        var outcome = terminalEx is null ? "success" : "failed";
        WorkItemSyncMetrics.DurationMs.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            KeyVal("provider", providerLabel),
            KeyVal("kind", resolvedKind),
            KeyVal("outcome", outcome));
        WorkItemSyncMetrics.Items.Add(totalAdded, KeyVal("provider", providerLabel), KeyVal("kind", "added"));
        WorkItemSyncMetrics.Items.Add(totalUpdated, KeyVal("provider", providerLabel), KeyVal("kind", "updated"));
        WorkItemSyncMetrics.Items.Add(totalConflicted, KeyVal("provider", providerLabel), KeyVal("kind", "conflicted"));
        if (terminalEx is not null)
        {
            WorkItemSyncMetrics.Errors.Add(1,
                KeyVal("provider", providerLabel),
                KeyVal("errorClass", WorkItemSyncMetrics.ClassifyError(terminalEx)));
            this.logger.LogWarning(terminalEx,
                "WorkItemSync {RepoId}: {Kind} failed after {Batches} batch(es)",
                repoId, resolvedKind, batchCount);
        }
        else
        {
            this.logger.LogInformation(
                "WorkItemSync {RepoId}: {Kind} completed — {Batches} batches, {Added} added, {Updated} updated, {Conflicted} conflicts",
                repoId, resolvedKind, batchCount, totalAdded, totalUpdated, totalConflicted);
        }

        // Single fan-out for the UI.
        await hub.Clients.All.SendAsync(
            "workitem.changed",
            new
            {
                kind = "synced",
                repoId,
                pulled = totalAdded + totalUpdated,
                inserted = totalAdded,
                updated = totalUpdated,
                conflicts = totalConflicted,
                outcome,
            },
            ct);
    }

    /// <summary>
    /// Phase 23.4 / 23.5 — commit a single batch. Counts are returned so
    /// the caller can roll them up across the whole run.
    /// </summary>
    private async Task<(int Added, int Updated, int Conflicted)> CommitBatchAsync(
        ForgeDbContext db,
        RepoRow repo,
        WorkItemBatch batch,
        IHubContext<ForgeHub> hub,
        IServiceScope scope,
        string runId,
        CancellationToken ct)
    {
        var added = 0;
        var updated = 0;
        var conflicted = 0;

        foreach (var w in batch.Items)
        {
            var withRepo = w with { RepoId = repo.Id };
            var (didInsert, didUpdate, didConflict) = await UpsertAsync(db, withRepo, ct);
            if (didInsert)
            {
                added++;
                db.SyncRunItems.Add(new SyncRunItemRow
                {
                    RunId = runId,
                    EntityKind = "workitem",
                    EntityId = withRepo.Id ?? $"{withRepo.Provider}:{withRepo.ProviderRef}",
                    Title = withRepo.Title ?? string.Empty,
                    Action = "added",
                    ProviderRef = withRepo.ProviderRef,
                });
            }
            else if (didUpdate)
            {
                updated++;
                db.SyncRunItems.Add(new SyncRunItemRow
                {
                    RunId = runId,
                    EntityKind = "workitem",
                    EntityId = withRepo.Id ?? $"{withRepo.Provider}:{withRepo.ProviderRef}",
                    Title = withRepo.Title ?? string.Empty,
                    Action = "updated",
                    ProviderRef = withRepo.ProviderRef,
                });
            }
            if (didConflict) conflicted++;

            // Phase 23.6 / 23.8 — apply agent-ready auto-team-assign after
            // the upsert so the row exists. The resolver returns the result
            // but does not write itself; we persist + audit + fan-out here.
            var existing = await db.WorkItems.FirstOrDefaultAsync(
                x => x.Provider == withRepo.Provider && x.ProviderRef == withRepo.ProviderRef, ct);
            if (existing is null) continue;

            var resolverResult = AgentReadyResolver.Apply(
                existing.AssignedAgentJson,
                withRepo.Tags ?? Array.Empty<string>(),
                repo.AgentReadyDefaultTeam);
            if (!resolverResult.WasAssigned) continue;

            existing.AssignedToJson = resolverResult.NewAssignedToJson;
            // Move to 'active' so the queue worker can claim it, and enqueue.
            if (existing.State is "proposed" or "new")
            {
                existing.State = "active";
            }
            existing.ExecutionStatus = Persistence.ExecutionStatuses.Queued;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            db.Audit.Add(new AuditRow
            {
                At = DateTimeOffset.UtcNow,
                User = "sync",
                Method = "AUTO",
                Path = $"/api/repos/{repo.Id}/auto-assigned-by-flag",
                Status = 200,
                IdempotencyKey = null,
                RequestBodySnippet = JsonSerializer.Serialize(new
                {
                    repoId = repo.Id,
                    workItemId = existing.Id,
                    team = resolverResult.TeamName,
                }),
                Supervising = false,
            });
            WorkItemSyncMetrics.AgentReadyAssignments.Add(1, KeyVal("team", resolverResult.TeamName ?? "unknown"));

            await hub.Clients.All.SendAsync(
                "workitem.changed",
                new
                {
                    kind = "auto-assigned-by-flag",
                    workItemId = existing.Id,
                    repoId = repo.Id,
                    team = resolverResult.TeamName,
                },
                ct);

            // Enqueue into the task queue so the queue worker claims it.
            await db.SaveChangesAsync(ct);
            try
            {
                var queue = scope.ServiceProvider.GetService<ITaskQueueService>();
                if (queue is not null)
                {
                    await queue.EnqueueAsync(
                        existing.Id,
                        "team",
                        resolverResult.TeamName ?? SyncConstants.AgentReadyDefaultTeam,
                        ct);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "agent-ready enqueue failed for {WiId}", existing.Id);
            }
        }

        return (added, updated, conflicted);
    }

    private static IReadOnlyList<string>? ParseAreaPaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            return arr is { Count: > 0 } ? arr : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Build the canonical ADO org URL from the repo row's <c>AdoOrg</c>.
    /// Accepts either a bare org name (<c>microsoft</c>) or a full URL
    /// (<c>https://dev.azure.com/microsoft</c>) so older rows keep working.
    /// </summary>
    private static string BuildAdoOrgUrl(string adoOrg)
    {
        var trimmed = adoOrg.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.TrimEnd('/');
        }
        return $"https://dev.azure.com/{trimmed.Trim('/')}";
    }

    /// <summary>
    /// True when the repo is the canonical seed placeholder created by
    /// <c>ForgeSeed.EnsureDefaultProductAndRepoAsync</c>. The URL targets a
    /// fictional <c>dev.azure.com/atlas</c> org used only to satisfy FK
    /// constraints for seeded cards/PRs; auto-syncing it produces a 401
    /// every tick that ends up in the products UI as a red error strip.
    /// </summary>
    internal static bool IsSeedPlaceholder(RepoRow repo) =>
        string.Equals(repo.Id, ForgeSeed.DefaultRepoId, StringComparison.Ordinal)
        && string.Equals(repo.AdoOrg, "atlas", StringComparison.OrdinalIgnoreCase)
        && string.Equals(repo.AdoProject, "atlas-platform", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mark the placeholder so the UI shows a friendly "edit me" message
    /// instead of a stale 401. Idle status keeps it from being re-picked
    /// next tick (the query also matches "failed", but the friendly error
    /// message tells the operator what to do).
    /// </summary>
    private async Task MarkPlaceholderAsync(string repoId, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
            if (repo is null) return;
            repo.LastSyncStatus = "idle";
            repo.LastSyncError = "Demo placeholder — click Edit to point at your real ADO project, or attach a new repo.";
            repo.IsRunningSince = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "WorkItemSyncJob: failed to mark placeholder repo {RepoId}", repoId);
        }
    }

    /// <summary>
    /// Insert when no <c>(provider, providerRef)</c> row exists; otherwise
    /// update only the upstream-owned fields. <c>didConflict</c> fires when
    /// the local <c>RemoteRevision</c> differs from the upstream value
    /// (last-writer-wins, but the divergence is surfaced so Phase 5.9's
    /// conflict policy lights up the divergence pill in the UI).
    /// </summary>
    private static async Task<(bool Inserted, bool Updated, bool Conflicted)> UpsertAsync(
        ForgeDbContext db, WorkItem remote, CancellationToken ct)
    {
        var existing = await db.WorkItems.FirstOrDefaultAsync(
            w => w.Provider == remote.Provider && w.ProviderRef == remote.ProviderRef, ct);

        if (existing is null)
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = remote.Id,
                RepoId = remote.RepoId,
                Provider = remote.Provider,
                ProviderRef = remote.ProviderRef,
                Track = remote.Track,
                Type = remote.Type,
                State = remote.State,
                Title = remote.Title,
                DescriptionMd = remote.DescriptionMd,
                ParentId = remote.ParentId,
                FeatureRef = remote.FeatureRef,
                IterationRef = remote.IterationRef,
                AreaPath = remote.AreaPath,
                AssignedToJson = remote.AssignedTo is null
                    ? null
                    : JsonSerializer.Serialize(remote.AssignedTo, JsonOpts),
                Points = remote.Points,
                Priority = remote.Priority,
                TagsJson = JsonSerializer.Serialize(remote.Tags, JsonOpts),
                CreatedAt = remote.CreatedAt,
                UpdatedAt = remote.UpdatedAt,
                RemoteRevision = remote.RemoteRevision,
            });
            return (true, false, false);
        }

        // Skip when the revision matches.
        if (!string.IsNullOrEmpty(remote.RemoteRevision)
            && existing.RemoteRevision == remote.RemoteRevision)
        {
            return (false, false, false);
        }

        // Skip when upstream ChangedDate is not newer than our last update.
        // This prevents a stale sync response from overwriting local edits
        // (e.g. operator changed points/state via the detail panel between
        // two sync ticks).
        if (remote.UpdatedAt > DateTimeOffset.MinValue
            && existing.UpdatedAt >= remote.UpdatedAt)
        {
            return (false, false, false);
        }

        var hadDifferentRevision = !string.IsNullOrEmpty(existing.RemoteRevision)
            && !string.IsNullOrEmpty(remote.RemoteRevision)
            && existing.RemoteRevision != remote.RemoteRevision;

        // Once Forge has touched the item (AutoDispatchedAt is set OR
        // executionStatus is not idle), it owns the runtime-lifecycle fields.
        // Sync only updates upstream-sourced metadata; Forge-owned fields
        // (state, assignedTo, points, priority) are protected.
        var forgeOwned = existing.AutoDispatchedAt.HasValue
            || existing.ExecutionStatus != Persistence.ExecutionStatuses.Idle;

        // Always sync upstream-sourced metadata.
        existing.Track = remote.Track;
        existing.Type = remote.Type;
        existing.Title = remote.Title;
        existing.DescriptionMd = remote.DescriptionMd;
        if (!string.IsNullOrEmpty(remote.ParentId)) existing.ParentId = remote.ParentId;
        existing.IterationRef = remote.IterationRef;
        existing.AreaPath = remote.AreaPath;
        existing.TagsJson = JsonSerializer.Serialize(remote.Tags, JsonOpts);
        existing.RemoteRevision = remote.RemoteRevision;

        // Only overwrite Forge-owned fields when Forge hasn't touched the item.
        if (!forgeOwned)
        {
            existing.State = remote.State;
            if (remote.AssignedTo is not null)
            {
                existing.AssignedToJson = JsonSerializer.Serialize(remote.AssignedTo, JsonOpts);
            }
            existing.Points = remote.Points > 0 ? remote.Points : 2;
            existing.Priority = remote.Priority;
        }

        existing.UpdatedAt = remote.UpdatedAt;
        return (false, true, hadDifferentRevision);
    }

    private static KeyValuePair<string, object?> KeyVal(string k, object? v) => new(k, v);
}
