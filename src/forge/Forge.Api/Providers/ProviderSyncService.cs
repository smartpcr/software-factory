// -----------------------------------------------------------------------
// <copyright file="ProviderSyncService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 5.7 — IHostedService that polls each configured <see cref="IForgeProvider"/>
/// at <see cref="ProviderSyncOptions.IntervalSeconds"/> ± jitter. When a webhook for
/// the provider arrived within <see cref="ProviderSyncOptions.WebhookSilenceFallbackSeconds"/>,
/// the poll is skipped — the webhook already kept the cache fresh.
/// </summary>
public class ProviderSyncService : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly IEnumerable<IForgeProvider> providers;
    private readonly WebhookActivity webhookActivity;
    private readonly ProviderSyncOptions options;
    private readonly ILogger<ProviderSyncService> logger;
    private readonly Random random = new();

    public ProviderSyncService(
        IServiceProvider services,
        IEnumerable<IForgeProvider> providers,
        WebhookActivity webhookActivity,
        IOptions<ProviderSyncOptions> options,
        ILogger<ProviderSyncService> logger)
    {
        this.services = services;
        this.providers = providers;
        this.webhookActivity = webhookActivity;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.options.Enabled)
        {
            this.logger.LogInformation("forge.sync disabled by configuration");
            return;
        }

        // Stagger the initial run so multiple providers don't fire at the same instant.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var provider in this.providers.Where(p => p.IsConfigured))
            {
                if (this.webhookActivity.IsRecent(provider.Kind, TimeSpan.FromSeconds(this.options.WebhookSilenceFallbackSeconds)))
                {
                    this.logger.LogDebug("forge.sync.skipped provider={Kind} reason=webhook-recent", provider.Kind);
                    continue;
                }
                await this.RunOneAsync(provider, stoppingToken);
            }

            var jitter = this.random.Next(-this.options.JitterSeconds, this.options.JitterSeconds + 1);
            var wait = Math.Max(1, this.options.IntervalSeconds + jitter);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), stoppingToken);
            }
            catch (TaskCanceledException) { return; }
        }
    }

    public async Task RunOneAsync(IForgeProvider provider, CancellationToken ct)
    {
        try
        {
            using var scope = this.services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ProviderCache>();

            var cards = await provider.ListWorkItemsAsync(ct);
            var prs = await provider.ListPullRequestsAsync(ct);
            var cardChanges = await cache.UpsertCardsAsync(provider.Kind, cards, ct);
            var prChanges = await cache.UpsertPrsAsync(provider.Kind, prs, ct);
            this.logger.LogInformation(
                "forge.sync.completed provider={Kind} cards={Cards} prs={Prs} cardChanges={CardChanges} prChanges={PrChanges}",
                provider.Kind, cards.Count, prs.Count, cardChanges, prChanges);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "forge.sync.failed provider={Kind}", provider.Kind);
        }
    }
}
