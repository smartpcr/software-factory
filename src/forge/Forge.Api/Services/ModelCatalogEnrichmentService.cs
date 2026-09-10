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
/// stale) then a recurring run every day at local midnight. Operators can force
/// a cycle at any time with <c>POST /api/models/refresh</c>.</para>
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
/// <para><b>Retire / revive (2026-09-10).</b> A model the live catalog STOPS
/// serving used to sit in the seed forever with <c>"status": "live"</c>, so the
/// Crews model picker filled up with dead options (<c>gpt-5.2</c>,
/// <c>claude-opus-4.6</c>, <c>gemini-3.1-pro-preview</c>, …) rendered as
/// "— unsupported". Each cycle now reconciles status: a Copilot-vendor model
/// absent from the live catalog is marked <c>retired</c>, and one that comes
/// back is marked <c>live</c> again. Entries are never deleted — crew/agent
/// rows reference them for historical cost lookups. Anthropic (Claude SDK) and
/// forge (local-stub) models are exempt because they never appear in the
/// Copilot catalog by design.</para>
///
/// <para><b>Resilient.</b> A fetch failure (no GitHub auth, copilot server
/// won't spawn, offline) is logged and the JSON values stand; enrichment is
/// best-effort. An empty catalog NEVER retires anything. Lazy: the first run is
/// delayed so we don't force the copilot server to spawn during the boot
/// critical path.</para>
/// </summary>
public sealed class ModelCatalogEnrichmentService : BackgroundService
{
    // Delay the first run so boot isn't blocked and the shared copilot server
    // spawn (triggered by ListModelsAsync) happens off the hot path. A run at
    // startup keeps a mid-week deploy from serving a stale catalog until the
    // next scheduled tick.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);

    // A live catalog smaller than this is treated as a partial/degraded fetch:
    // capabilities are still overlaid, but nothing is retired, so a transient
    // upstream hiccup can't mass-retire the catalog on disk.
    private const int MinCatalogSizeForRetirement = 5;

    private readonly CopilotServerManager copilotServer;
    private readonly IModelRegistry registry;
    private readonly ILogger<ModelCatalogEnrichmentService> log;

    // Serialises the scheduled tick against an operator-triggered
    // POST /api/models/refresh so two cycles never write the seed at once.
    private readonly SemaphoreSlim gate = new(1, 1);

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

    /// <summary>
    /// 2026-09-10 — run one enrichment cycle on demand (operator-triggered via
    /// <c>POST /api/models/refresh</c>) instead of waiting for the daily tick.
    /// Serialised against the scheduled run; never throws.
    /// </summary>
    public Task RefreshNowAsync(CancellationToken ct) => this.EnrichOnceAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await this.EnrichOnceAsync(stoppingToken);

            // Recurring cadence: daily, at local midnight. Recomputed from "now"
            // each loop so there is no drift.
            var wait = TimeUntilNextMidnight(DateTime.Now);
            this.log.LogInformation(
                "Model-catalog enrichment: next scheduled run at {NextRun:yyyy-MM-dd HH:mm} (local midnight, in {Hours:F1}h).",
                DateTime.Now.Add(wait), wait.TotalHours);
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Time from <paramref name="now"/> until the next local midnight. When
    /// <paramref name="now"/> is exactly midnight it returns a full day, so a
    /// run that just completed does not immediately re-fire.
    /// </summary>
    internal static TimeSpan TimeUntilNextMidnight(DateTime now)
    {
        var next = now.Date.AddDays(1);
        return next - now;
    }

    /// <summary>
    /// Fetch the live catalog and overlay it onto the registry. Never throws:
    /// failures leave the JSON values in place.
    /// </summary>
    private async Task EnrichOnceAsync(CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
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

            var statusOverrides = BuildStatusOverrides(this.registry.All, liveIds);

            if (overlay.Count == 0 && discovered.Count == 0 && statusOverrides.Count == 0)
            {
                this.log.LogInformation("Model-catalog enrichment: catalog had no usable capability data, no new models and no status changes; registry unchanged. Recorded {Count} live model ids.", liveIds.Count);
                return;
            }

            this.registry.ApplyCatalogOverlay(overlay, discovered, statusOverrides);
            this.log.LogInformation(
                "Model-catalog enrichment applied: overlaid {Overlay} seed model(s); auto-added {Discovered} new live model(s) [{Added}]; status changes for {Status} model(s) [{Changed}].",
                overlay.Count,
                discovered.Count,
                string.Join(", ", discovered.Select(d => d.Id)),
                statusOverrides.Count,
                string.Join(", ", statusOverrides.Select(kv => $"{kv.Key}={kv.Value}")));

            this.TryPersist(discovered, statusOverrides);
        }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Model-catalog enrichment failed (non-fatal); registry keeps models.json values. Will retry on the next scheduled run.");
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// 2026-09-10 — reconcile each catalog entry's <c>status</c> against the
    /// live Copilot catalog: <c>retired</c> when a currently-live Copilot model
    /// is no longer served, <c>live</c> when a retired one reappears. Returns
    /// only the ids whose status must CHANGE, so the result is empty (and no
    /// write happens) on a steady-state cycle.
    ///
    /// <para>Skips non-Copilot vendors — <c>anthropic</c> models run through the
    /// Claude SDK and <c>forge</c>'s <c>local-stub</c> bypasses every backend, so
    /// neither ever appears in the Copilot catalog. This mirrors the vendor
    /// exemption in <see cref="IModelRegistry.ValidateAssignable"/>. Also skips
    /// a suspiciously small catalog, which signals a partial fetch rather than
    /// a genuine mass retirement.</para>
    /// </summary>
    internal static Dictionary<string, string> BuildStatusOverrides(
        IReadOnlyList<ModelInfo> catalog,
        IReadOnlyCollection<string> liveIds)
    {
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (catalog is null || liveIds is null || liveIds.Count < MinCatalogSizeForRetirement) return changes;

        var live = new HashSet<string>(liveIds, StringComparer.OrdinalIgnoreCase);
        foreach (var m in catalog)
        {
            if (m is null || string.IsNullOrWhiteSpace(m.Id)) continue;
            if (!string.Equals(m.Vendor, "copilot", StringComparison.OrdinalIgnoreCase)) continue;

            // Only flip between the two states this service owns; a manually
            // curated status such as "preview" or "deprecated" is left alone.
            var isLive = string.Equals(m.Status, "live", StringComparison.OrdinalIgnoreCase);
            var isRetired = string.Equals(m.Status, "retired", StringComparison.OrdinalIgnoreCase);
            if (!isLive && !isRetired) continue;

            var served = live.Contains(m.Id);
            if (isLive && !served) changes[m.Id] = "retired";
            else if (isRetired && served) changes[m.Id] = "live";
        }

        return changes;
    }

    /// <summary>
    /// Persist newly discovered models and status changes to the on-disk seed
    /// file so the catalog stays current across restarts. Best-effort: a write
    /// failure is logged and never disturbs the already-applied in-memory
    /// registry.
    /// </summary>
    private void TryPersist(
        IReadOnlyList<ModelInfo> discovered,
        IReadOnlyDictionary<string, string> statusOverrides)
    {
        try
        {
            var path = this.registry.SeedFilePath;
            var result = ModelSeedFileWriter.Sync(path, discovered, statusOverrides);
            if (result.Changed)
            {
                this.log.LogInformation(
                    "Model-catalog enrichment: seed file {Path} updated — {Added} added, {Retired} retired, {Revived} revived.",
                    path, result.Added, result.Retired, result.Revived);
            }
        }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Model-catalog enrichment: failed to persist catalog changes to the seed file (non-fatal; the in-memory registry is already updated).");
        }
    }
}
