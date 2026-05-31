// -----------------------------------------------------------------------
// <copyright file="ModelCatalogEnrichmentService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Agents;
using Microsoft.Extensions.Hosting;

/// <summary>
/// 2026-05-30 — keeps the <see cref="IModelRegistry"/> context windows +
/// reasoning flags honest by overlaying the AUTHORITATIVE Copilot model
/// catalog onto the JSON seed.
///
/// <para><b>Why.</b> <c>Agents/Models/models.json</c> was repeatedly wrong about
/// context windows (gpt-5.5 listed 128K → really 1.05M; Opus 4.7 xhigh listed
/// 1M → really 200K; etc.) because they were hand-maintained guesses. The real
/// numbers live in the Copilot catalog (<c>capabilities.limits.max_context_window_tokens</c>
/// + <c>capabilities.supports.reasoningEffort</c>), reachable via the SDK's
/// already-authenticated <see cref="CopilotServerManager.ListModelsAsync"/>
/// (the raw <c>copilot_internal/v2/token</c> exchange 404s for OAuth-app/PAT
/// tokens). This service fetches that catalog at boot + on a refresh cadence
/// and applies it; models.json becomes the pricing/fallback only.</para>
///
/// <para><b>Resilient.</b> A fetch failure (no GitHub auth, copilot server
/// won't spawn, offline) is logged and the JSON values stand — enrichment is
/// best-effort. Lazy: the first run is delayed so we don't force the copilot
/// server to spawn during the boot critical path.</para>
/// </summary>
public sealed class ModelCatalogEnrichmentService : BackgroundService
{
    // Delay the first run so boot isn't blocked and the shared copilot server
    // spawn (triggered by ListModelsAsync) happens off the hot path.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);

    // Catalog rarely changes; a slow refresh catches Copilot adding/retiring
    // a model or adjusting a window without a Forge restart.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly CopilotServerManager copilotServer;
    private readonly IModelRegistry registry;
    private readonly ILogger<ModelCatalogEnrichmentService> log;

    public ModelCatalogEnrichmentService(
        CopilotServerManager copilotServer,
        IModelRegistry registry,
        ILogger<ModelCatalogEnrichmentService> log)
    {
        this.copilotServer = copilotServer;
        this.registry = registry;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await this.EnrichOnceAsync(stoppingToken);
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Fetch the live catalog and overlay it onto the registry. Never throws —
    /// failures leave the JSON values in place.
    /// </summary>
    private async Task EnrichOnceAsync(CancellationToken ct)
    {
        try
        {
            var models = await this.copilotServer.ListModelsAsync(ct);
            if (models is null || models.Count == 0)
            {
                this.log.LogInformation(
                    "Model-catalog enrichment skipped: Copilot server returned no models (not authenticated / server unavailable). Registry keeps models.json values.");
                return;
            }

            var overlay = new Dictionary<string, ModelCatalogOverlay>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in models)
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                int? ctx = null;
                bool? reasoning = null;
                if (m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
                {
                    if (caps.TryGetProperty("limits", out var lim) && lim.ValueKind == JsonValueKind.Object
                        && lim.TryGetProperty("max_context_window_tokens", out var cw)
                        && cw.ValueKind == JsonValueKind.Number)
                    {
                        var v = cw.GetInt32();
                        if (v > 0) ctx = v; // skip "auto" (0)
                    }
                    if (caps.TryGetProperty("supports", out var sup) && sup.ValueKind == JsonValueKind.Object
                        && sup.TryGetProperty("reasoningEffort", out var re)
                        && (re.ValueKind == JsonValueKind.True || re.ValueKind == JsonValueKind.False))
                    {
                        reasoning = re.GetBoolean();
                    }
                }

                if (ctx is not null || reasoning is not null)
                {
                    overlay[id] = new ModelCatalogOverlay(ctx, reasoning);
                }
            }

            if (overlay.Count == 0)
            {
                this.log.LogInformation("Model-catalog enrichment: catalog had no usable capability data; registry unchanged.");
                return;
            }

            this.registry.ApplyCatalogOverlay(overlay);
            this.log.LogInformation(
                "Model-catalog enrichment applied: overlaid context window / reasoning for {Count} models from the live Copilot catalog.",
                overlay.Count);
        }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Model-catalog enrichment failed (non-fatal); registry keeps models.json values. Will retry on the next refresh tick.");
        }
    }
}
