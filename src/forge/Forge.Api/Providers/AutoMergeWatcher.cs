// -----------------------------------------------------------------------
// <copyright file="AutoMergeWatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Domain;
using Forge.Api.Persistence;
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
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
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
                if (pr.Conflicts) continue;
                if (pr.Build?.Status is not null && pr.Build.Status != "passing") continue;
                if (pr.Review?.Verdict is "changes" or "pending") continue;

                // Pick a provider that owns the workstream/branch — we don't
                // track that mapping yet, so we try GitHub first then ADO.
                var provider = providers.FirstOrDefault(p => p.IsConfigured && p.Kind == "github")
                    ?? providers.FirstOrDefault(p => p.IsConfigured && p.Kind == "ado");
                if (provider is null)
                {
                    this.logger.LogDebug("forge.auto-merge.skipped pr={Pr} reason=no-configured-provider", pr.Id);
                    continue;
                }

                try
                {
                    var merged = await provider.MergePullRequestAsync(pr.Id, ProviderCallContext.Anonymous, ct);
                    var fresh = await db.Prs.FirstOrDefaultAsync(p => p.Id == row.Id, ct);
                    if (fresh is not null)
                    {
                        fresh.Col = "merged";
                        fresh.AutoMerge = false;
                        await db.SaveChangesAsync(ct);
                    }
                    this.logger.LogInformation("forge.auto-merge.merged pr={Pr} provider={Provider}", pr.Id, provider.Kind);
                    await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", source = "auto-merge", pr = merged }, ct);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "forge.auto-merge.failed pr={Pr}", pr.Id);
                }
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "forge.auto-merge.tick.failed");
        }
    }
}
