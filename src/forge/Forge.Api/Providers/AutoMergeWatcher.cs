// -----------------------------------------------------------------------
// <copyright file="AutoMergeWatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 6.8 — periodically scans cached PRs that have <c>autoMerge=true</c>
/// and merges them via the configured provider when:
///   * build status is <c>passing</c> (or null when no CI is configured);
///   * the human-gate evaluator returns <c>Required=false</c>;
///   * there is no merge conflict;
///   * the review verdict is <c>approved</c> or absent.
/// All merges fan out via <c>pr.changed { kind = "merged" }</c>.
/// </summary>
public class AutoMergeWatcher : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly IHumanGateEvaluator gate;
    private readonly ILogger<AutoMergeWatcher> logger;
    private readonly ProviderSyncOptions syncOptions;

    public AutoMergeWatcher(
        IServiceProvider services,
        IHumanGateEvaluator gate,
        IOptions<ProviderSyncOptions> syncOptions,
        ILogger<AutoMergeWatcher> logger)
    {
        this.services = services;
        this.gate = gate;
        this.syncOptions = syncOptions.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.syncOptions.Enabled)
        {
            this.logger.LogInformation("forge.auto-merge disabled (sync disabled)");
            return;
        }
        // Stagger first run so we don't race the seed/sync round.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await this.RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, this.syncOptions.IntervalSeconds)), stoppingToken);
            }
            catch (TaskCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = this.services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IForgeProvider>>().ToList();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ForgeHub>>();

            var rows = await db.Prs.AsNoTracking().Where(p => p.AutoMerge).ToListAsync(ct);
            foreach (var row in rows)
            {
                var pr = PrMapper.ToContract(row);
                var decision = this.gate.Evaluate(pr);
                if (decision.Required) continue;
                if (pr.Build?.Status is not null && pr.Build.Status != "passing") continue;
                if (pr.Review?.Verdict is "changes" or "pending") continue;
                // Conflicts are NOT a skip when AutoMerge is on — the story
                // owner pre-authorised destructive resolution, so we let the
                // -X ours path try when the provider rejects with a conflict.

                // Pick the provider from the PR's own provider field (was
                // hard-coded github→ado fallback, which broke ADO-only
                // multi-repo deployments).
                var providerKind = !string.IsNullOrEmpty(row.Provider) ? row.Provider : "github";
                var provider = providers.FirstOrDefault(p => p.Kind == providerKind && p.IsConfigured);
                if (provider is null)
                {
                    this.logger.LogDebug(
                        "forge.auto-merge.skipped pr={Pr} reason=no-configured-provider kind={Kind}",
                        pr.Id, providerKind);
                    continue;
                }

                // Per-PR repo coords so AdoProvider doesn't fall back to the
                // global Forge:Ado:Repository in multi-repo deployments.
                var repo = !string.IsNullOrEmpty(row.RepoId)
                    ? await db.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RepoId, ct)
                    : null;
                var (repoName, projectName) = PrCoordsResolver.Resolve(repo, provider.Kind);
                var callCtx = new ProviderCallContext(
                    UserId: null,
                    UserPat: null,
                    IdempotencyKey: null,
                    RepositoryName: repoName,
                    ProjectName: projectName);

                // PROACTIVE FLOW (matches the human-driven endpoint): rebase
                // the source branch onto base locally first (always with
                // force=true since AutoMerge is the operator's standing
                // pre-authorisation), push, then ask ADO to complete.
                if (!string.IsNullOrEmpty(row.Branch) && !string.IsNullOrEmpty(row.Base))
                {
                    try
                    {
                        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
                        var rebaseOk = await PrMergeUtilities.RebaseSourceOnBaseAsync(db, git, row, force: true, ct);
                        if (!rebaseOk)
                        {
                            this.logger.LogWarning(
                                "forge.auto-merge.rebase.failed pr={Pr} — leaving AutoMerge=true to retry next tick",
                                pr.Id);
                            continue;
                        }
                    }
                    catch (Exception rebaseEx)
                    {
                        this.logger.LogWarning(rebaseEx, "forge.auto-merge.rebase.error pr={Pr}", pr.Id);
                        continue;
                    }
                }

                Pr? merged = null;
                try
                {
                    merged = await provider.MergePullRequestAsync(pr.Id, callCtx, ct);
                }
                catch (HttpRequestException rateEx) when (
                    rateEx.StatusCode == System.Net.HttpStatusCode.Forbidden
                    || rateEx.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    || rateEx.Message.Contains("403", StringComparison.OrdinalIgnoreCase))
                {
                    // GitHub returns 403 when the per-hour rate budget is
                    // exhausted. Stop processing the rest of this tick — every
                    // additional call would burn further budget against an
                    // already-closed window. The watcher resumes next tick;
                    // by then the rate window may have rolled over.
                    this.logger.LogWarning(
                        rateEx,
                        "forge.auto-merge.rate-limited pr={Pr} — aborting tick to let the rate window roll over",
                        pr.Id);
                    return;
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "forge.auto-merge.complete.failed pr={Pr}", pr.Id);
                    continue;
                }

                if (merged is null) continue;

                // Trust the provider response — only flip Col=merged when
                // upstream actually reports the PR as completed. Otherwise
                // the watcher would lie when ADO returns 200 with status
                // still 'active' (queued merge / branch-policy reject).
                if (!string.Equals(merged.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    this.logger.LogInformation(
                        "forge.auto-merge.deferred pr={Pr} reason=provider-returned-col={Col}",
                        pr.Id, merged.Col);
                    continue;
                }

                var fresh = await db.Prs.FirstOrDefaultAsync(p => p.Id == row.Id, ct);
                IReadOnlyList<string> advancedWsIds = Array.Empty<string>();
                if (fresh is not null)
                {
                    fresh.Col = "merged";
                    fresh.AutoMerge = false;
                    advancedWsIds = await WorkstreamMergeAdvancer
                        .AdvanceForMergedPrAsync(db, fresh.Id, ct);
                    await db.SaveChangesAsync(ct);
                }
                this.logger.LogInformation("forge.auto-merge.merged pr={Pr} provider={Provider}", pr.Id, provider.Kind);
                await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", source = "auto-merge", pr = merged }, ct);
                if (advancedWsIds.Count > 0)
                {
                    await hub.Clients.All.SendAsync(
                        "story-spawn.changed",
                        new { kind = "ws-completed", prId = fresh!.Id, workstreamIds = advancedWsIds }, ct);
                }
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "forge.auto-merge.tick.failed");
        }
    }
}
