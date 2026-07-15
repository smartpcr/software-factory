// -----------------------------------------------------------------------
// <copyright file="ModelCatalogMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Api.Contracts;

/// <summary>
/// 2026-07-14: maps a raw live Copilot catalog model (the JSON returned by
/// <see cref="Forge.Api.Agents.CopilotServerManager.ListModelsAsync"/>) to a
/// <see cref="ModelInfo"/> so the scheduled
/// <see cref="ModelCatalogEnrichmentService"/> can AUTO-ADD models the
/// hand-maintained <c>Agents/Models/models.json</c> seed doesn't list yet
/// (e.g. a freshly released <c>gpt-5.6-*</c> or <c>claude-sonnet-5</c>).
///
/// <para><b>Why a mapper.</b> The live catalog carries no vendor, family or
/// pricing (<c>billing.multiplier</c> is null for every entry), so those are
/// INFERRED from the id. Pricing is therefore a rough tier estimate; a human
/// can later pin an authoritative price by adding a curated seed entry, which
/// always wins over an auto-added one (the registry dedups by seed id).</para>
///
/// <para>Context window is taken from <c>capabilities.limits.max_prompt_tokens</c>
/// (the usable prompt budget) when present, else <c>max_context_window_tokens</c>.
/// Reasoning surfaces both a boolean and the catalog's
/// <c>defaultReasoningEffort</c> (e.g. "medium", "max").</para>
///
/// <para>Kept as a pure static helper (no I/O) so the inference and skip rules
/// are unit-testable without spinning the Copilot server.</para>
/// </summary>
internal static class ModelCatalogMapper
{
    /// <summary>A capability slice extracted from one live catalog entry.</summary>
    internal readonly record struct LiveCatalogModel(
        string Id,
        string DisplayName,
        int? ContextWindow,
        bool? SupportsReasoning,
        string? DefaultReasoningEffort);

    /// <summary>
    /// Extract the fields Forge cares about from a single live catalog JSON
    /// element. Returns false (and a default struct) when the element has no
    /// usable string <c>id</c>. Context window prefers <c>max_prompt_tokens</c>
    /// (usable prompt budget) over <c>max_context_window_tokens</c>; both are
    /// null/zero for the <c>auto</c> router, which then maps to a null window.
    /// </summary>
    internal static bool TryMap(JsonElement model, out LiveCatalogModel result)
    {
        result = default;
        if (model.ValueKind != JsonValueKind.Object) return false;
        if (!model.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        var id = idEl.GetString();
        if (string.IsNullOrWhiteSpace(id)) return false;

        var displayName = model.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? id
            : id;

        int? ctx = null;
        bool? reasoning = null;
        if (model.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
        {
            if (caps.TryGetProperty("limits", out var lim) && lim.ValueKind == JsonValueKind.Object)
            {
                // Prefer max_prompt_tokens (the usable prompt budget) over
                // max_context_window_tokens (the total window incl. output).
                // The CLI's smaller interactive session cap is not in the catalog.
                if (lim.TryGetProperty("max_prompt_tokens", out var mp)
                    && mp.ValueKind == JsonValueKind.Number && mp.TryGetInt32(out var mpv) && mpv > 0)
                {
                    ctx = mpv;
                }
                else if (lim.TryGetProperty("max_context_window_tokens", out var cw)
                    && cw.ValueKind == JsonValueKind.Number && cw.TryGetInt32(out var cwv) && cwv > 0)
                {
                    ctx = cwv;
                }
            }

            if (caps.TryGetProperty("supports", out var sup) && sup.ValueKind == JsonValueKind.Object
                && sup.TryGetProperty("reasoningEffort", out var re)
                && (re.ValueKind == JsonValueKind.True || re.ValueKind == JsonValueKind.False))
            {
                reasoning = re.GetBoolean();
            }
        }

        // defaultReasoningEffort is a top-level field (sibling of capabilities),
        // e.g. "medium" or "max"; surfaced so the picker can show the effort level.
        string? defaultEffort = null;
        if (model.TryGetProperty("defaultReasoningEffort", out var dre)
            && dre.ValueKind == JsonValueKind.String)
        {
            var s = dre.GetString();
            if (!string.IsNullOrWhiteSpace(s)) defaultEffort = s;
        }

        result = new LiveCatalogModel(id!, displayName, ctx, reasoning, defaultEffort);
        return true;
    }

    /// <summary>
    /// Whether a live model should be AUTO-ADDED to the registry when the seed
    /// doesn't already list it. Excludes the <c>auto</c> router (no real
    /// context window) and org-namespaced foundry entries (ids containing
    /// <c>/</c>, e.g. <c>octodemo/...</c>) which aren't standard assignable
    /// Copilot models.
    /// </summary>
    internal static bool IsDiscoverable(LiveCatalogModel model)
        => model.ContextWindow is > 0
        && !string.IsNullOrWhiteSpace(model.Id)
        && !model.Id.Contains('/');

    /// <summary>
    /// Build a <see cref="ModelInfo"/> for a discovered live model. Vendor is
    /// always <c>copilot</c> (it came from the Copilot catalog); family and
    /// pricing are inferred from the id. Status is <c>live</c> so it is
    /// immediately assignable once the enrichment also records it in the live
    /// catalog id set.
    /// </summary>
    internal static ModelInfo ToDiscovered(LiveCatalogModel model)
    {
        var (input, output) = InferPricing(model.Id);
        return new ModelInfo(
            Id: model.Id,
            Vendor: "copilot",
            Family: InferFamily(model.Id),
            DisplayName: string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName,
            ContextWindow: model.ContextWindow ?? 0,
            InputPricePerMTok: input,
            OutputPricePerMTok: output,
            SupportsTools: true,
            SupportsStreaming: true,
            Status: "live")
        {
            ReasoningOverride = model.SupportsReasoning,
            DefaultReasoningEffort = model.DefaultReasoningEffort,
        };
    }

    /// <summary>
    /// Family grouping for the model picker. For the primary brands
    /// (gpt / claude / gemini) it is <c>{brand}-{major}</c> derived from the
    /// first integer in the id (e.g. <c>gpt-5.6-sol</c> gives <c>gpt-5</c>,
    /// <c>claude-sonnet-5</c> gives <c>claude-5</c>); everything else falls back
    /// to the leading brand token (<c>kimi</c>, <c>mai</c>, and so on).
    /// </summary>
    internal static string InferFamily(string id)
    {
        var s = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0) return "other";

        var brand = s.Split(new[] { '-', '/', '.' }, 2)[0];
        var digits = Regex.Match(s, @"\d+");
        var major = digits.Success && int.TryParse(digits.Value, out var m) ? m : 0;

        return brand switch
        {
            "gpt" or "claude" or "gemini" when major > 0 => $"{brand}-{major}",
            _ => brand,
        };
    }

    /// <summary>
    /// Rough per-MTok USD estimate by id tier. The live catalog exposes no
    /// billing (<c>billing.multiplier</c> is null), and Copilot is
    /// subscription-billed anyway, so this only drives the "est." cost pill.
    /// Mirrors the tiers the seed already uses.
    /// </summary>
    internal static (decimal Input, decimal Output) InferPricing(string id)
    {
        var s = (id ?? string.Empty).ToLowerInvariant();
        if (s.Contains("opus")) return (15m, 75m);
        if (s.Contains("sonnet")) return (3m, 15m);
        if (s.Contains("haiku")) return (1m, 5m);
        if (s.Contains("mini") || s.Contains("flash") || s.Contains("picker")) return (1m, 5m);

        // gpt-5.x flagship / gemini pro / kimi / other reasoning-class models.
        return (3m, 15m);
    }
}
