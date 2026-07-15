// -----------------------------------------------------------------------
// <copyright file="IModelRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using Forge.Api.Contracts;

/// <summary>
/// Phase 11.2 — read-only catalog of LLM models Forge knows about.
/// Backed by <c>Agents/Models/models.json</c> shipped with the API.
/// </summary>
public interface IModelRegistry
{
    /// <summary>All known models in the order they appear in the JSON seed.</summary>
    IReadOnlyList<ModelInfo> All { get; }

    /// <summary>Returns the model with the given <paramref name="id"/>, or null if not in the registry.</summary>
    ModelInfo? Find(string id);

    /// <summary>
    /// 2026-07-14: absolute path of the <c>models.json</c> seed file the registry
    /// loaded (resolved from the content root, falling back to the app base
    /// directory). The scheduled enrichment writes newly discovered models back
    /// to this file so the on-disk catalog stays current.
    /// </summary>
    string SeedFilePath { get; }

    /// <summary>
    /// 2026-05-30: overlay authoritative capability data from the live Copilot
    /// catalog (<c>GET /api/admin/copilot-models</c> / ListModelsAsync) onto the
    /// JSON-seeded registry: corrects <c>ContextWindow</c>, <c>ReasoningOverride</c>
    /// and <c>DefaultReasoningEffort</c> per model id. Applied by
    /// <see cref="Forge.Api.Services.ModelCatalogEnrichmentService"/> at boot and
    /// on a refresh cadence. Ids not present in the overlay keep their JSON
    /// values (pricing is never overlaid: Copilot bills by subscription/credits,
    /// so the JSON USD price is the local estimate). Idempotent.
    ///
    /// <para>2026-07-14: <paramref name="discoveredModels"/> are live-catalog
    /// models the JSON seed does NOT list yet (a freshly released model);
    /// they are AUTO-ADDED to <see cref="All"/> so the scheduled refresh keeps
    /// the catalog current without a code change or restart. A discovered id
    /// that collides with a seed id is ignored (the curated seed entry, incl.
    /// its pricing, always wins). Rebuilt from the immutable seed each call, so
    /// passing the full current discovered set each cycle both adds new models
    /// and drops ones the catalog no longer serves.</para>
    /// </summary>
    void ApplyCatalogOverlay(
        IReadOnlyDictionary<string, ModelCatalogOverlay> overlay,
        IReadOnlyList<ModelInfo>? discoveredModels = null);

    /// <summary>True once <see cref="ApplyCatalogOverlay"/> has run at least once.</summary>
    bool CatalogEnriched { get; }

    /// <summary>
    /// 2026-06-22 — the set of model ids the live Copilot catalog
    /// (<see cref="Forge.Api.Agents.CopilotServerManager.ListModelsAsync"/>)
    /// currently returns, recorded by
    /// <see cref="Forge.Api.Services.ModelCatalogEnrichmentService"/>. Empty
    /// until the first successful fetch; callers MUST treat empty as "unknown"
    /// (fail-open), not "nothing available".
    /// </summary>
    IReadOnlyCollection<string> LiveCatalogIds { get; }

    /// <summary>Record the live Copilot catalog ids (case-insensitive). Idempotent.</summary>
    void RecordLiveCatalog(IReadOnlyCollection<string> liveModelIds);

    /// <summary>
    /// 2026-06-22 — returns a human-readable reason when <paramref name="modelId"/>
    /// must NOT be assigned to a crew/agent, else <c>null</c>. Blocks: an id not
    /// in the catalog (retired/removed), a non-<c>live</c> status, and a
    /// Copilot-vendor model the live catalog no longer serves (the runner would
    /// reject it at <c>session.create</c> with "Model is not available", looping
    /// the deliverable to its iter cap). Null/blank id = inherit = allowed.
    /// Fails OPEN for Copilot models when the live catalog hasn't been fetched
    /// yet (<see cref="LiveCatalogIds"/> empty) so a transient catalog outage
    /// never blocks a legitimate assignment.
    /// </summary>
    string? ValidateAssignable(string? modelId);
}

/// <summary>
/// 2026-05-30: authoritative per-model capability slice from the live Copilot
/// catalog, used to overlay the JSON registry. Null fields leave the JSON value
/// untouched. <paramref name="DefaultReasoningEffort"/> (2026-07-14) carries the
/// catalog's <c>defaultReasoningEffort</c> (e.g. "medium", "max").
/// </summary>
public sealed record ModelCatalogOverlay(
    int? ContextWindow,
    bool? SupportsReasoning,
    string? DefaultReasoningEffort = null);
