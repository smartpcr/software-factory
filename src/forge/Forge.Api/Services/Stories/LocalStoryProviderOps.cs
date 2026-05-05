// -----------------------------------------------------------------------
// <copyright file="LocalStoryProviderOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IStoryProviderOps"/> binding. Persists PR + comment
/// rows in the local DB and emits SignalR events so existing dashboards
/// pick the changes up. When <see cref="IForgeProvider"/> is configured,
/// this also dispatches the corresponding upstream call (PR open / merge /
/// approve / comment / WIT state transition); otherwise the local rows are
/// the source of truth (offline-friendly, test-friendly).
/// </summary>
public sealed class LocalStoryProviderOps : IStoryProviderOps
{
    private readonly IServiceScopeFactory scopeFactory;

    public LocalStoryProviderOps(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    private async Task<T> WithDbAsync<T>(Func<ForgeDbContext, IHubContext<ForgeHub>?, Task<T>> body, CancellationToken ct)
    {
        // Use IServiceScopeFactory rather than IServiceProvider so the
        // singleton can't end up holding a disposed request scope. The
        // factory is itself a singleton tied to the root provider.
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
        return await body(db, hub);
    }

    public Task<string> OpenPrAsync(StoryPrRequest req, CancellationToken ct = default)
    {
        return this.WithDbAsync<string>(async (db, hub) =>
        {
            var prId = $"PR-spawn-{Guid.NewGuid():N}".Substring(0, 16);
            var pr = new PrRow
            {
                Id = prId,
                RepoId = req.Repo?.Id,
                Col = "review",
                Title = req.Title,
                Branch = req.Branch,
                Base = req.BaseBranch,
                Workstream = req.Workstream ?? string.Empty,
                Epic = req.Epic ?? string.Empty,
                EpicColor = string.Empty,
                AuthorJson = "{}",
                Description = req.Body,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                IsActive = true,
                LastSyncStatus = "idle",
                Provider = req.Repo?.Kind,
                WorkItemId = req.WorkItemId,
            };
            db.Prs.Add(pr);
            await db.SaveChangesAsync(ct);
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("pr.changed", new { kind = "opened", source = "story-spawn", id = prId }, ct);
            }
            return prId;
        }, ct);
    }

    public Task MergePrAsync(string prId, CancellationToken ct = default)
    {
        return this.WithDbAsync<bool>(async (db, hub) =>
        {
            var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (pr is null) return false;
            pr.Col = "merged";
            pr.IsActive = false;
            pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await db.SaveChangesAsync(ct);
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", id = prId }, ct);
            }
            return true;
        }, ct);
    }

    public Task ApprovePrAsync(string prId, string reviewerAgentId, CancellationToken ct = default)
    {
        return this.WithDbAsync<bool>(async (db, hub) =>
        {
            var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (pr is null) return false;
            // Stash a minimal review JSON so existing UI can render the badge.
            pr.ReviewJson = $"{{\"verdict\":\"approve\",\"reviewer\":\"{reviewerAgentId}\"}}";
            pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await db.SaveChangesAsync(ct);
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("pr.changed", new { kind = "approved", id = prId }, ct);
            }
            return true;
        }, ct);
    }

    public Task<long> PostPrCommentAsync(
        string prId, string filePath, int line, string body, string authorAgentId, CancellationToken ct = default)
    {
        return this.WithDbAsync<long>(async (db, _) =>
        {
            var row = new PrCommentRow
            {
                PrId = prId,
                ProviderCommentId = $"local-{Guid.NewGuid():N}",
                ThreadId = $"thread-{Guid.NewGuid():N}",
                AuthorId = authorAgentId,
                AuthorKind = "agent",
                Body = body,
                FilePath = filePath,
                Line = line,
                State = "active",
                RequiresAttention = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.PrComments.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, ct);
    }

    public Task ResolvePrCommentAsync(long commentId, string resolverAgentId, CancellationToken ct = default)
    {
        return this.WithDbAsync<bool>(async (db, _) =>
        {
            var c = await db.PrComments.FirstOrDefaultAsync(x => x.Id == commentId, ct);
            if (c is null) return false;
            c.State = "resolved";
            c.RequiresAttention = false;
            c.ResolvedByUserId = resolverAgentId;
            c.ResolvedAt = DateTimeOffset.UtcNow;
            c.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    public Task UpdatePrBaseBranchAsync(string prId, string newBaseBranch, CancellationToken ct = default)
    {
        return this.WithDbAsync<bool>(async (db, hub) =>
        {
            var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (pr is null) return false;
            // Open PRs only — re-targeting a merged PR is meaningless and
            // would mislead anyone reading PR history. Caller is responsible
            // for filtering, but we still no-op defensively.
            if (string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase)) return false;
            pr.Base = newBaseBranch;
            pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await db.SaveChangesAsync(ct);
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("pr.changed",
                    new { kind = "base-branch-changed", id = prId, newBaseBranch }, ct);
            }
            return true;
        }, ct);
    }

    public Task TransitionWorkItemAsync(string workItemId, string state, CancellationToken ct = default)
    {
        return this.WithDbAsync<bool>(async (db, hub) =>
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return false;
            wi.State = state;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("work-item.changed", new { kind = "state", id = workItemId, state }, ct);
            }
            return true;
        }, ct);
    }
}
