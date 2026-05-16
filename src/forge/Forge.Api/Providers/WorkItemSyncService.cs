// -----------------------------------------------------------------------
// <copyright file="WorkItemSyncService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Phase 12.4 / 12.10 — pulls work items from a repo's provider and upserts
/// them into the <c>work_items</c> table. Per-repo rate limit (1 sync / 60s)
/// guards against accidental hammering; per-item upsert preserves locally-
/// edited fields when the remote shape doesn't carry them.
/// </summary>
public class WorkItemSyncService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(60);

    private readonly IEnumerable<IForgeProvider> providers;
    private readonly IServiceScopeFactory scopes;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<WorkItemSyncService> logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSyncByRepo = new();

    public WorkItemSyncService(
        IEnumerable<IForgeProvider> providers,
        IServiceScopeFactory scopes,
        IHubContext<ForgeHub> hub,
        ILogger<WorkItemSyncService> logger)
    {
        this.providers = providers;
        this.scopes = scopes;
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>Outcome of a sync request.</summary>
    public sealed record SyncResult(int Pulled, int Inserted, int Updated, int Skipped, string? Error);

    /// <summary>
    /// Run a sync. Returns the upsert summary. Caller is expected to have
    /// already authorized the request.
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        RepoRow repo,
        WorkItemSyncFilters filters,
        bool ignoreThrottle,
        CancellationToken ct)
    {
        if (!ignoreThrottle && this.lastSyncByRepo.TryGetValue(repo.Id, out var last))
        {
            var elapsed = DateTimeOffset.UtcNow - last;
            if (elapsed < ThrottleWindow)
            {
                return new SyncResult(0, 0, 0, 0,
                    $"Rate limit: last sync was {(int)elapsed.TotalSeconds}s ago; window is {(int)ThrottleWindow.TotalSeconds}s.");
            }
        }

        var provider = this.providers.FirstOrDefault(p => string.Equals(p.Kind, repo.Kind, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new SyncResult(0, 0, 0, 0, $"No provider registered for kind '{repo.Kind}'.");
        }
        if (!provider.IsConfigured)
        {
            return new SyncResult(0, 0, 0, 0, $"Provider '{repo.Kind}' is not configured.");
        }

        // Per-repo provider scope (Phase 23 follow-up). Without this each
        // repo's manual sync would hit the global Forge:Ado config — wrong
        // when more than one product/repo is attached.
        var scopedFilters = filters with
        {
            AdoOrgUrl = filters.AdoOrgUrl ?? (string.IsNullOrWhiteSpace(repo.AdoOrg) ? null : BuildAdoOrgUrl(repo.AdoOrg!)),
            AdoProject = filters.AdoProject ?? repo.AdoProject,
            GitHubRepo = filters.GitHubRepo ?? (
                !string.IsNullOrWhiteSpace(repo.GhOwner) && !string.IsNullOrWhiteSpace(repo.GhRepo)
                    ? $"{repo.GhOwner}/{repo.GhRepo}"
                    : null),
        };

        IReadOnlyList<WorkItem> remote;
        try
        {
            remote = await provider.ListWorkItemsForSyncAsync(scopedFilters, ct);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Sync failed for {RepoId}", repo.Id);
            return new SyncResult(0, 0, 0, 0, ex.Message);
        }

        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        // Resolve a fresh DbContext from a per-call scope. Important because
        // this singleton service is invoked from minimal-API handlers whose
        // request-scoped DbContext is unrelated.
        using (var scope = this.scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            foreach (var w in remote)
            {
                // Pin the row to the requested repo regardless of how the
                // provider labelled it — the operator's intent is to sync
                // INTO this repo.
                var withRepo = w with { RepoId = repo.Id };
                var (didInsert, didUpdate) = await UpsertAsync(db, withRepo, ct);
                if (didInsert) inserted++;
                else if (didUpdate) updated++;
                else skipped++;
            }
            await db.SaveChangesAsync(ct);
        }
        this.lastSyncByRepo[repo.Id] = DateTimeOffset.UtcNow;

        // Single fan-out so the UI knows to refresh; per-item events would
        // flood the channel for large syncs.
        await this.hub.Clients.All.SendAsync(
            "workitem.changed",
            new { kind = "synced", repoId = repo.Id, pulled = remote.Count, inserted, updated, skipped },
            ct);

        return new SyncResult(remote.Count, inserted, updated, skipped, null);
    }

    /// <summary>
    /// Insert when no matching <c>(provider, providerRef)</c> row exists,
    /// otherwise update only the provider-owned fields. Locally-edited
    /// fields the remote doesn't track (e.g. <c>FeatureRef</c>, internal
    /// tags) are preserved.
    /// </summary>
    private static async Task<(bool Inserted, bool Updated)> UpsertAsync(ProductDbContext db, WorkItem remote, CancellationToken ct)
    {
        var existing = await db.WorkItems.FirstOrDefaultAsync(
            w => w.Provider == remote.Provider && w.ProviderRef == remote.ProviderRef, ct);

        if (existing is null)
        {
            // First import — store the canonical id (mapper assigns "WI-{id}"
            // for ADO and "WI-GH-{n}" for GitHub).
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
            return (true, false);
        }

        // Skip when the revision matches — saves a write and keeps the audit
        // trail clean.
        if (!string.IsNullOrEmpty(remote.RemoteRevision)
            && existing.RemoteRevision == remote.RemoteRevision)
        {
            return (false, false);
        }

        // Skip when upstream ChangedDate is not newer than our last update.
        if (remote.UpdatedAt > DateTimeOffset.MinValue
            && existing.UpdatedAt >= remote.UpdatedAt)
        {
            return (false, false);
        }

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
        return (false, true);
    }

    /// <summary>Test hook — clear the throttle window for a repo.</summary>
    public void ResetThrottle(string repoId) => this.lastSyncByRepo.TryRemove(repoId, out _);

    /// <summary>
    /// Same shape as <c>WorkItemSyncJob.BuildAdoOrgUrl</c> — accepts either a
    /// bare org name or a full URL.
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
}
