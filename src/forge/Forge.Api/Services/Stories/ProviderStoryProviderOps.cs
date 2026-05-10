// -----------------------------------------------------------------------
// <copyright file="ProviderStoryProviderOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Agents;
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

        // Phase 45 follow-up (2026-05-05) — use the same canonical resolver
        // as CrewQueueWorker + WorkItemsEndpoints, so live-exec PRs and
        // story-spawn PRs both end up with the right per-repo coordinates.
        // Operator's local DisplayName is just a friendly alias ("update");
        // ADO's PR API needs the slug after `_git/` in the remote URL
        // ("ASZ-UpdateService" for the operator's hci/update setup).
        var (repoName, projectName) = PrCoordsResolver.Resolve(req.Repo, req.Repo.Kind?.ToLowerInvariant());

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
                    Additions = prContract.Additions,
                    Deletions = prContract.Deletions,
                    Files = prContract.Files,
                    Commits = prContract.Commits,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    IsActive = true,
                    LastSyncStatus = "idle",
                    Provider = req.Repo.Kind,
                    WorkItemId = req.WorkItemId,
                    WebUrl = prContract.WebUrl,
                    // Extract the numeric provider ref from the canonical ID
                    // ("PR #15630125" → "15630125") so the sync client can
                    // fetch state + stats from ADO.
                    ProviderRef = new string(prId.Where(char.IsDigit).ToArray()),
                    // Phase 46 — carry the spawn-level auto-merge policy.
                    AutoMerge = req.AutoMerge,
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

        // Phase 45 follow-up — populate file / commit / iteration stats
        // immediately so the PR tab doesn't show "0 files, 0 commits" until
        // the periodic PrSyncJob tick (~60s later). Fire-and-forget so the
        // accept flow doesn't block on a slow ADO probe; PrSyncJob.RunSyncForAsync
        // is idempotent — a follow-up periodic tick is a safe no-op.
        var syncJob = this.services.GetService<Forge.Api.Services.PrSyncJob>();
        if (syncJob is not null)
        {
            var capturedPrId = prId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await syncJob.RunSyncForAsync(capturedPrId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex,
                        "Inline PR enrichment failed for {PrId}; the periodic sync tick will retry.",
                        capturedPrId);
                }
            });
        }

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
        var ctx = await BuildCallContextAsync(db, pr, ct);
        await provider.MergePullRequestAsync(prId, ctx, ct);

        var wasMerged = string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase);
        pr.Col = "merged";
        pr.IsActive = false;
        pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", id = prId }, ct);
        }
        this.logger?.LogInformation("Story PR merged via {Kind}: {PrId}", pr.Provider, prId);

        // Phase 45 follow-up — Forge-issued plan-PR merge needs to stop any
        // still-running plan-doc loops. PrSyncJob runs the same hook for
        // upstream-detected merges; both paths converge on PlanPrMergeReactor.
        if (!wasMerged)
        {
            try
            {
                var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
                await PlanPrMergeReactor.OnPlanPrMergedAsync(db, runner, hub, prId, this.logger, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger?.LogWarning(ex,
                    "PlanPrMergeReactor failed for {PrId}; PR is merged but in-flight plan-doc loops were not cancelled.",
                    prId);
            }
        }
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
        var ctx = await BuildCallContextAsync(db, pr, ct);
        await provider.ApprovePullRequestAsync(prId, ctx, ct);

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
        // ProviderCommentId must be unique per PR — the provider's
        // CommentOnPullRequestAsync API returns no id, so we mint a
        // "pending:{guid}" placeholder. PrSyncJob will later sync the real
        // upstream comment as a separate row keyed by its provider id; the
        // pending row remains as the local audit trail of the agent's
        // request and is not reconciled with the upstream copy because
        // there's no reliable matching key (CommentOnPullRequestAsync
        // doesn't echo back the created comment id).
        var row = new PrCommentRow
        {
            PrId = prId,
            ProviderCommentId = $"pending:{Guid.NewGuid():N}",
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
                var ctx = await BuildCallContextAsync(db, pr, ct);
                await provider.CommentOnPullRequestAsync(prId, formatted, ctx, ct);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Provider comment failed for PR {PrId}; local row persisted.", prId);
            }
        }
        return row.Id;
    }

    /// <summary>
    /// Phase 45 — build a <see cref="ProviderCallContext"/> with per-PR repo
    /// coordinates so AdoProvider's PR ops target the correct repo even when
    /// the global <c>Forge:Ado:Repository</c> config points elsewhere. Looks
    /// up the PR's <see cref="PrRow.RepoId"/> → <see cref="RepoRow"/> and
    /// resolves canonical coords via <see cref="PrCoordsResolver"/> (the same
    /// resolver <c>CrewQueueWorker</c> + <c>WorkItemsEndpoints</c> use for
    /// PR creation, so live-exec and story-spawn now share one resolution
    /// path end-to-end).
    /// </summary>
    private static async Task<ProviderCallContext> BuildCallContextAsync(
        ForgeDbContext db, PrRow pr, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pr.RepoId)) return ProviderCallContext.Anonymous;
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == pr.RepoId, ct);
        if (repo is null) return ProviderCallContext.Anonymous;
        var (repoName, projectName) = PrCoordsResolver.Resolve(repo, repo.Kind?.ToLowerInvariant());
        return new ProviderCallContext(
            UserId: null,
            UserPat: null,
            IdempotencyKey: null,
            RepositoryName: repoName,
            ProjectName: projectName);
    }

    public async Task<long> ReplyPrCommentAsync(long parentCommentId, string body, string authorAgentId, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var parent = await db.PrComments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentCommentId, ct)
            ?? throw new InvalidOperationException($"Parent comment {parentCommentId} not found.");

        // Persist the reply locally — keyed under the parent's PrId + ThreadId
        // so the UI's threaded comment renderer groups them. State="resolved"
        // because the reply IS the response (the auto-fix loop sets the
        // PARENT comment's state to resolved separately via ResolvePrCommentAsync;
        // the reply row itself doesn't loop into another fix attempt).
        var row = new PrCommentRow
        {
            PrId = parent.PrId,
            ProviderCommentId = $"pending-reply:{Guid.NewGuid():N}",
            ThreadId = parent.ThreadId,
            ParentCommentId = parentCommentId,
            AuthorId = authorAgentId,
            AuthorKind = "agent",
            Body = body,
            FilePath = parent.FilePath,
            Line = parent.Line,
            State = "resolved",
            RequiresAttention = false,
            ResolvedByUserId = authorAgentId,
            ResolvedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PrComments.Add(row);
        await db.SaveChangesAsync(ct);

        // Best-effort upstream — IForgeProvider doesn't have a per-thread
        // reply API today, so we post the reply as a regular comment with
        // a quote-prefix that visually threads the conversation on GitHub
        // and ADO. The local row above preserves the structural link
        // (ParentCommentId) for Forge-side rendering.
        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == parent.PrId, ct);
        if (pr is not null && !string.IsNullOrEmpty(pr.Provider))
        {
            try
            {
                var provider = ResolveProvider(scope.ServiceProvider, pr.Provider);
                var quotedPreview = parent.Body?.Length > 160 ? parent.Body[..160] + "…" : parent.Body ?? string.Empty;
                var formatted = $"> Re: {parent.FilePath}:{parent.Line} — {quotedPreview}\n\n{body}\n\n_({authorAgentId} reply)_";
                var ctx = await BuildCallContextAsync(db, pr, ct);
                await provider.CommentOnPullRequestAsync(parent.PrId, formatted, ctx, ct);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex,
                    "Provider reply post failed for parent comment {ParentId} on PR {PrId}; local reply row persisted.",
                    parentCommentId, parent.PrId);
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

    /// <summary>
    /// Phase 45 — retarget an open PR at a new base branch. Currently the
    /// <see cref="IForgeProvider"/> contract has no edit-PR endpoint (PATCH
    /// <c>/repos/{owner}/{repo}/pulls/{number}</c> for GitHub, equivalent
    /// REST for ADO). We update the local <see cref="PrRow"/> + emit a
    /// SignalR event so the UI reflects the change immediately, and surface
    /// the missing upstream call as a structured warning so the next sync
    /// pass can catch divergence. When provider-side update support lands,
    /// add the real edit call here ahead of the local row.
    /// </summary>
    public async Task UpdatePrBaseBranchAsync(string prId, string newBaseBranch, CancellationToken ct = default)
    {
        using var scope = this.services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct)
            ?? throw new InvalidOperationException($"PR {prId} not found in local cache.");
        if (string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase)) return;

        pr.Base = newBaseBranch;
        pr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await db.SaveChangesAsync(ct);
        if (hub is not null)
        {
            await hub.Clients.All.SendAsync("pr.changed",
                new { kind = "base-branch-changed", id = prId, newBaseBranch }, ct);
        }
        this.logger?.LogWarning(
            "PR {PrId} base branch changed locally to {Base}, but the IForgeProvider has no edit-PR endpoint — operator must reset the base on {Provider} manually until provider support lands.",
            prId, newBaseBranch, pr.Provider);
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
