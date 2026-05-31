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
}

/// <summary>
/// 2026-05-30 — authoritative per-model capability slice from the live Copilot
/// catalog, used to overlay the JSON registry. Null fields leave the JSON value
/// untouched.
/// </summary>
public sealed record ModelCatalogOverlay(int? ContextWindow, bool? SupportsReasoning);
