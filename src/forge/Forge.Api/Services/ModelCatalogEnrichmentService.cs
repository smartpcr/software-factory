// -----------------------------------------------------------------------
// <copyright file="ModelCatalogEnrichmentService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Agents;
using Forge.Api.Contracts;
using Microsoft.Extensions.Hosting;

/// <summary>
/// 2026-05-30: keeps the <see cref="IModelRegistry"/> context windows and
/// reasoning flags honest by overlaying the AUTHORITATIVE Copilot model
/// catalog onto the JSON seed, and (2026-07-14) AUTO-ADDS live models the seed
/// doesn't list yet so the catalog stays current on a schedule.
///
/// <para><b>Schedule.</b> A run shortly after boot (so a mid-week deploy is not
/// stale) then a recurring run every week at Sunday 00:00 local time.</para>
///
/// <para><b>Why.</b> <c>Agents/Models/models.json</c> was repeatedly wrong about
/// context windows (gpt-5.5 listed 128K, really 1.05M; Opus 4.7 xhigh listed
/// 1M, really 200K; etc.) because they were hand-maintained guesses. The real
/// numbers live in the Copilot catalog (<c>capabilities.limits.max_prompt_tokens</c>,
/// <c>capabilities.supports.reasoningEffort</c>, <c>defaultReasoningEffort</c>),
/// reachable via the SDK's already-authenticated
/// <see cref="CopilotServerManager.ListModelsAsync"/> (the raw
/// <c>copilot_internal/v2/token</c> exchange 404s for OAuth-app/PAT tokens).
/// This service fetches that catalog and applies it; models.json becomes the
/// pricing/fallback only.</para>
///
/// <para><b>Auto-add (2026-07-14).</b> The overlay previously only corrected
/// EXISTING seed entries and recorded live ids for reject-only validation, so a
/// newly released model (e.g. <c>gpt-5.6-*</c>, <c>claude-sonnet-5</c>) never
/// surfaced until a human edited the seed and restarted Forge. Now every live
/// model absent from the seed is mapped (via <see cref="ModelCatalogMapper"/>)
/// to a <see cref="Forge.Api.Contracts.ModelInfo"/> (vendor <c>copilot</c>,
/// inferred family and estimated pricing), merged into the registry, and
/// persisted back to the seed file (via <see cref="ModelSeedFileWriter"/>). A
/// curated seed entry always wins over an auto-added one, so a human can still
/// pin authoritative pricing.</para>
///
/// <para><b>Resilient.</b> A fetch failure (no GitHub auth, copilot server
/// won't spawn, offline) is logged and the JSON values stand; enrichment is
/// best-effort. Lazy: the first run is delayed so we don't force the copilot
/// server to spawn during the boot critical path.</para>
/// </summary>
public sealed class ModelCatalogEnrichmentService : BackgroundService
{
    // Delay the first run so boot isn't blocked and the shared copilot server
    // spawn (triggered by ListModelsAsync) happens off the hot path. A run at
    // startup keeps a mid-week deploy from serving a stale catalog until the
    // next weekly tick.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);

    private readonly CopilotServerManager copilotServer;
    private readonly IModelRegistry registry;
    private readonly ILogger<ModelCatalogEnrichmentService> log;

    // Pristine seed model ids, snapshotted on the first enrichment run (before
    // any overlay/discovery is applied, so registry.All == the JSON seed).
    // Used to tell a genuinely new live model from one the seed already ships,
    // and to keep auto-add idempotent across the refresh cadence.
    private HashSet<string>? seedIds;

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

            // Recurring cadence: weekly, at Sunday 00:00 local time. Recomputed
            // from "now" each loop so there is no drift.
            var wait = TimeUntilNextSundayMidnight(DateTime.Now);
            this.log.LogInformation(
                "Model-catalog enrichment: next scheduled run at {NextRun:yyyy-MM-dd HH:mm} (Sunday midnight, in {Hours:F1}h).",
                DateTime.Now.Add(wait), wait.TotalHours);
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Time from <paramref name="now"/> until the next Sunday 00:00 local time.
    /// When <paramref name="now"/> is already exactly a Sunday midnight (or any
    /// later moment on Sunday) it returns the delay to the FOLLOWING Sunday, so
    /// a run that just completed does not immediately re-fire.
    /// </summary>
    internal static TimeSpan TimeUntilNextSundayMidnight(DateTime now)
    {
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var next = now.Date.AddDays(daysUntilSunday);
        if (next <= now)
        {
            next = next.AddDays(7);
        }

        return next - now;
    }

    /// <summary>
    /// Fetch the live catalog and overlay it onto the registry. Never throws:
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
            var liveIds = new List<string>(models.Count);
            var candidates = new List<ModelInfo>(models.Count);
            foreach (var m in models)
            {
                if (!ModelCatalogMapper.TryMap(m, out var live)) continue;
                liveIds.Add(live.Id);

                // Overlay authoritative context window, reasoning flag and
                // default reasoning effort onto the matching seed entry (if any).
                if (live.ContextWindow is not null || live.SupportsReasoning is not null
                    || live.DefaultReasoningEffort is not null)
                {
                    overlay[live.Id] = new ModelCatalogOverlay(
                        live.ContextWindow, live.SupportsReasoning, live.DefaultReasoningEffort);
                }

                // Candidate for auto-add when the seed doesn't ship this model.
                if (ModelCatalogMapper.IsDiscoverable(live))
                {
                    candidates.Add(ModelCatalogMapper.ToDiscovered(live));
                }
            }

            // Snapshot the pristine seed ids on the first run: registry.All is
            // still the JSON seed before any overlay/discovery lands, and this
            // service is the only writer, so the snapshot is the true seed.
            this.seedIds ??= new HashSet<string>(
                this.registry.All.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

            var discovered = candidates
                .Where(c => !this.seedIds.Contains(c.Id))
                .ToList();

            // 2026-06-22: record the live catalog id set so crew/agent model
            // assignment (IModelRegistry.ValidateAssignable) can reject a model
            // the runner would later refuse at session.create. Recorded even
            // when no capability overlay was usable (ids alone are enough).
            this.registry.RecordLiveCatalog(liveIds);

            if (overlay.Count == 0 && discovered.Count == 0)
            {
                this.log.LogInformation("Model-catalog enrichment: catalog had no usable capability data and no new models; registry unchanged. Recorded {Count} live model ids.", liveIds.Count);
                return;
            }

            this.registry.ApplyCatalogOverlay(overlay, discovered);
            if (discovered.Count > 0)
            {
                this.log.LogInformation(
                    "Model-catalog enrichment applied: overlaid {Overlay} seed model(s); AUTO-ADDED {Discovered} new live model(s) absent from models.json [{Ids}].",
                    overlay.Count, discovered.Count, string.Join(", ", discovered.Select(d => d.Id)));
                this.TryPersistDiscovered(discovered);
            }
            else
            {
                this.log.LogInformation(
                    "Model-catalog enrichment applied: overlaid context window / reasoning for {Count} models from the live Copilot catalog; no new models to add.",
                    overlay.Count);
            }
        }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Model-catalog enrichment failed (non-fatal); registry keeps models.json values. Will retry on the next scheduled run.");
        }
    }

    /// <summary>
    /// Persist newly discovered models to the on-disk seed file so the catalog
    /// stays current across restarts. Best-effort: a write failure is logged and
    /// never disturbs the already-applied in-memory registry.
    /// </summary>
    private void TryPersistDiscovered(IReadOnlyList<ModelInfo> discovered)
    {
        try
        {
            var path = this.registry.SeedFilePath;
            var added = ModelSeedFileWriter.AppendDiscovered(path, discovered);
            if (added > 0)
            {
                this.log.LogInformation(
                    "Model-catalog enrichment: persisted {Added} newly discovered model(s) to the seed file {Path}.",
                    added, path);
            }
        }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Model-catalog enrichment: failed to persist discovered models to the seed file (non-fatal; the in-memory registry is already updated).");
        }
    }
}
