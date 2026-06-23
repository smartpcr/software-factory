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
    /// 2026-05-30 — overlay authoritative capability data from the live Copilot
    /// catalog (<c>GET /api/admin/copilot-models</c> / ListModelsAsync) onto the
    /// JSON-seeded registry: corrects <c>ContextWindow</c> and sets
    /// <c>ReasoningOverride</c> per model id. Applied by
    /// <see cref="Forge.Api.Services.ModelCatalogEnrichmentService"/> at boot +
    /// on a refresh cadence. Ids not present in the overlay keep their JSON
    /// values (pricing is never overlaid — Copilot bills by subscription/credits,
    /// so the JSON USD price is the local estimate). Idempotent.
    /// </summary>
    void ApplyCatalogOverlay(IReadOnlyDictionary<string, ModelCatalogOverlay> overlay);

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
/// 2026-05-30 — authoritative per-model capability slice from the live Copilot
/// catalog, used to overlay the JSON registry. Null fields leave the JSON value
/// untouched.
/// </summary>
public sealed record ModelCatalogOverlay(int? ContextWindow, bool? SupportsReasoning);
