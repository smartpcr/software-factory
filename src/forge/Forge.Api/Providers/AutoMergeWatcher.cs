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
            // Phase G — resolve control DB once at scope level for cross-context reads.
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            // Phase E — fan out per product. Each product owns its own PRs;
            // the auto-merge watcher must visit each product's `prs` table.
            var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
            await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
            {
                await this.RunOneProductAsync(scope, control, db, fct);
            }, ct);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "AutoMergeWatcher tick failed");
        }
    }

    private async Task RunOneProductAsync(IServiceScope scope, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
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

                // 2026-05-13 — Phase 57b — refuse to merge a Forge-managed
                // workstream PR whose reviewer agent has not run yet
                // (Review verdict is null). Today's existing guard above
                // only catches "changes" / "pending" verdicts; a null
                // verdict slipped past, letting AutoMergeWatcher's 15 s
                // tick race ahead of the orchestrator's slower
                // DriveReviewAsync reviewer dispatch. Observed on
                // failover-cluster:XRAFT PR #9 2026-05-13: AutoMergeWatcher
                // auto-merged immediately after the build came back
                // passing; Shen Gongbao (the reviewer agent) then posted
                // 3 substantive comments on the already-merged PR, leaving
                // them stranded as state="active" with no fix path (the
                // workstream had advanced to "complete" and the worktree
                // was removed). Operator-created PRs without a workstream
                // keep the permissive null-verdict semantics — no
                // orchestrator owns them. Companion to
                // <see cref="StorySpawnOrchestrator.DriveReviewAsync"/>
                // which stamps Review = "pending" the first time it sees
                // an unreviewed workstream PR, so this guard is
                // belt-and-suspenders against the very first tick where
                // the orchestrator hasn't seen the PR yet either.
                if (pr.Review is null)
                {
                    var isWorkstreamPr = await db.Workstreams
                        .AsNoTracking()
                        .AnyAsync(w => w.PrsJson.Contains(row.Id), ct);
                    if (isWorkstreamPr)
                    {
                        this.logger.LogDebug(
                            "forge.auto-merge.skipped pr={Pr} reason=reviewer-not-run",
                            pr.Id);
                        continue;
                    }
                }
                // Conflicts are NOT a skip when AutoMerge is on — the story
                // owner pre-authorised destructive resolution, so we let the
                // -X ours path try when the provider rejects with a conflict.

                // 2026-05-12 — DO NOT merge while reviewer comments are still
                // unresolved. The story orchestrator's DriveReviewAsync runs
                // a parallel review-fix loop that posts replies + flips the
                // local PrCommentRow.State to "resolved" only after the
                // engineer has addressed each comment. AutoMergeWatcher used
                // to race ahead of that loop and merge with active comments
                // stranded on the PR (observed on smartpcr/qq#3 — 5 active
                // top-level comments, merged at 18:41 UTC). Cooperate with
                // the orchestrator: any active comment blocks the merge until
                // the orchestrator's loop resolves them (or operator clears).
                var hasActiveComments = await db.PrComments
                    .AsNoTracking()
                    .AnyAsync(c => c.PrId == row.Id && c.State == "active", ct);
                if (hasActiveComments)
                {
                    this.logger.LogDebug(
                        "forge.auto-merge.skipped pr={Pr} reason=active-comments",
                        pr.Id);
                    continue;
                }

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
                    ? await control.Set<RepoRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RepoId, ct)
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
                        var rebaseOk = await PrMergeUtilities.RebaseSourceOnBaseAsync(control, db, git, row, force: true, ct);
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
                        .AdvanceForMergedPrAsync(control, db, fresh.Id, ct);
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
}
