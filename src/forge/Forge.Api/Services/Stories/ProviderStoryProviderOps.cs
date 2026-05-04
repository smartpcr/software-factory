// -----------------------------------------------------------------------
// <copyright file="ProviderStoryProviderOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Production <see cref="IStoryProviderOps"/> — delegates PR open / merge /
/// approve / comment + work-item state transitions to the configured
/// <see cref="IForgeProvider"/> for the spawn's repo, and persists
/// matching <see cref="PrRow"/> + <see cref="PrCommentRow"/> rows so the
/// existing UI dashboards see the changes.
///
/// <para>
/// Provider routing is by <see cref="RepoRow.Kind"/> — "github" / "ado".
/// If the resolved provider is the <c>NullForgeProvider</c> or
/// <c>!IsConfigured</c>, the call fails loud (no synthetic fallback) so
/// the story spawn surfaces the misconfiguration rather than silently
/// writing fake PR ids.
/// </para>
/// </summary>
public sealed class ProviderStoryProviderOps : IStoryProviderOps
{
    private readonly IServiceProvider services;
    private readonly ILogger<ProviderStoryProviderOps>? logger;

    public ProviderStoryProviderOps(IServiceProvider services, ILogger<ProviderStoryProviderOps>? logger = null)
    {
        this.services = services;
        this.logger = logger;
    }

    public async Task<string> OpenPrAsync(StoryPrRequest req, CancellationToken ct = default)
    {
        if (req.Repo is null || string.IsNullOrEmpty(req.Repo.Kind))
            throw new InvalidOperationException("StoryPrRequest.Repo is required for real PR creation.");

        using var scope = this.services.CreateScope();
        var provider = ResolveProvider(scope.ServiceProvider, req.Repo.Kind);
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

        // Per-repo overrides for the provider's default project / repo. ADO
        // and GitHub use different naming, so we map from the matching
        // RepoRow columns.
        var (repoName, projectName) = req.Repo.Kind.ToLowerInvariant() switch
        {
            "github" => (req.Repo.GhRepo, req.Repo.GhOwner),
            "ado" => (req.Repo.DisplayName, req.Repo.AdoProject),
            _ => (null, null),
        };

        string prId;
        try
        {
            var prContract = await provider.CreatePullRequestAsync(
                new NewPullRequestRequest(
                    Branch: req.Branch,
                    BaseBranch: req.BaseBranch,
                    Title: req.Title,
                    Body: req.Body,
                    Draft: false,
                    RepositoryName: repoName,
                    ProjectName: projectName),
                ProviderCallContext.Anonymous,
                ct);
            prId = prContract.Id;

            // Mirror the real PR into PrRow so the UI / sync jobs see it.
            if (await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct) is null)
            {
                db.Prs.Add(new PrRow
                {
                    Id = prId,
                    RepoId = req.Repo.Id,
                    Col = "review",
                    Title = prContract.Title,
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
                    Provider = req.Repo.Kind,
                    WorkItemId = req.WorkItemId,
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // 422 typically means a PR already exists for this head branch.
            // Find the existing PR in the DB or create a placeholder.
            var existing = await db.Prs.FirstOrDefaultAsync(
                p => p.Branch == req.Branch && p.IsActive, ct);
            if (existing is not null)
            {
                prId = existing.Id;
            }
            else
            {
                // No existing PR in DB — re-throw, the error is something else
                throw;
            }
        }
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("pr.changed",
                new { kind = "opened", source = "story-spawn", id = prId }, ct);
        }
        this.logger?.LogInformation("Story PR opened via {Kind}: {PrId} ({Branch} → {Base})",
            req.Repo.Kind, prId, req.Branch, req.BaseBranch);
        return prId;
    }

    public async Task MergePrAsync(string prId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct)
            ?? throw new InvalidOperationException($"PR {prId} not found in local cache.");
        if (string.IsNullOrEmpty(pr.Provider))
            throw new InvalidOperationException($"PR {prId} has no provider — cannot merge.");

        var provider = ResolveProvider(scope.ServiceProvider, pr.Provider);
        await provider.MergePullRequestAsync(prId, ProviderCallContext.Anonymous, ct);

        pr.Col = "merged";
        pr.IsActive = false;
        pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", id = prId }, ct);
        }
        this.logger?.LogInformation("Story PR merged via {Kind}: {PrId}", pr.Provider, prId);
    }

    public async Task ApprovePrAsync(string prId, string reviewerAgentId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct)
            ?? throw new InvalidOperationException($"PR {prId} not found in local cache.");
        if (string.IsNullOrEmpty(pr.Provider))
            throw new InvalidOperationException($"PR {prId} has no provider — cannot approve.");

        var provider = ResolveProvider(scope.ServiceProvider, pr.Provider);
        await provider.ApprovePullRequestAsync(prId, ProviderCallContext.Anonymous, ct);

        pr.ReviewJson = $"{{\"verdict\":\"approve\",\"reviewer\":\"{reviewerAgentId}\"}}";
        pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("pr.changed", new { kind = "approved", id = prId, reviewerAgentId }, ct);
        }
    }

    public async Task<long> PostPrCommentAsync(
        string prId, string filePath, int line, string body, string authorAgentId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Local row first so the comment id is stable for downstream callers.
        var row = new PrCommentRow
        {
            PrId = prId,
            ProviderCommentId = string.Empty,
            ThreadId = string.Empty,
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

        // Best-effort upstream comment — non-fatal if the provider's
        // comment API fails. Without per-line support on the contract we
        // post a single line-prefixed body.
        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
        if (pr is not null && !string.IsNullOrEmpty(pr.Provider))
        {
            try
            {
                var provider = ResolveProvider(scope.ServiceProvider, pr.Provider);
                var formatted = $"`{filePath}:{line}` — {body}\n\n_({authorAgentId})_";
                await provider.CommentOnPullRequestAsync(prId, formatted, ProviderCallContext.Anonymous, ct);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Provider comment failed for PR {PrId}; local row persisted.", prId);
            }
        }
        return row.Id;
    }

    public async Task ResolvePrCommentAsync(long commentId, string resolverAgentId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var c = await db.PrComments.FirstOrDefaultAsync(x => x.Id == commentId, ct)
            ?? throw new InvalidOperationException($"Comment {commentId} not found.");
        c.State = "resolved";
        c.RequiresAttention = false;
        c.ResolvedByUserId = resolverAgentId;
        c.ResolvedAt = DateTimeOffset.UtcNow;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task TransitionWorkItemAsync(string workItemId, string state, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);

        // Local-only spawns (e.g. ProductStory-sourced) have no work item
        // in the providers — skip the upstream call cleanly.
        if (wi is null || string.IsNullOrEmpty(wi.Provider) || wi.Provider == "local")
        {
            this.logger?.LogInformation(
                "TransitionWorkItem skipped — {WorkItemId} has no upstream provider.", workItemId);
            return;
        }

        var provider = ResolveProvider(scope.ServiceProvider, wi.Provider);
        try
        {
            await provider.UpdateWorkItemAsync(workItemId,
                new UpdateWorkItemRequest(State: state),
                ProviderCallContext.Anonymous, ct);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex,
                "Provider {Kind} UpdateWorkItem failed for {WorkItemId}; local row updated only.",
                wi.Provider, workItemId);
        }

        wi.State = state;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("work-item.changed",
                new { kind = "state", id = workItemId, state }, ct);
        }
    }

    private static IForgeProvider ResolveProvider(IServiceProvider sp, string kind)
    {
        var providers = sp.GetServices<IForgeProvider>();
        var match = providers.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No IForgeProvider registered for kind '{kind}'.");
        if (!match.IsConfigured)
            throw new InvalidOperationException(
                $"Provider '{kind}' is not configured. Configure credentials before spawning stories.");
        return match;
    }
}
