// -----------------------------------------------------------------------
// <copyright file="ModelRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Phase 11.2 — loads the model catalog from <c>Agents/Models/models.json</c>
/// next to the binary. Failure to read or parse the file is fatal — the
/// runtime needs the catalog to compute cost.
/// </summary>
public sealed class ModelRegistry : IModelRegistry
{
    private const string SeedFileName = "models.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Lazy<IReadOnlyList<ModelInfo>> baseModels;

    // The catalog overlay (from the live Copilot catalog) is applied on top of
    // the JSON base. `effective` is swapped atomically by ApplyCatalogOverlay
    // so concurrent readers never see a torn list. Null until first read or
    // first overlay → falls back to the JSON base.
    private volatile IReadOnlyList<ModelInfo>? effective;
    private readonly object overlayLock = new();

    // 2026-06-22 — the live Copilot catalog id set (recorded by
    // ModelCatalogEnrichmentService). Swapped atomically; a HashSet so
    // ValidateAssignable's membership test is O(1). Empty = "not fetched yet"
    // (fail-open), never "nothing available".
    private volatile HashSet<string> liveCatalog = new(StringComparer.OrdinalIgnoreCase);

    private readonly string seedFilePath;

    public ModelRegistry(IHostEnvironment env)
    {
        var contentRoot = env?.ContentRootPath ?? AppContext.BaseDirectory;
        this.seedFilePath = ResolveSeedFilePath(contentRoot);
        this.baseModels = new Lazy<IReadOnlyList<ModelInfo>>(() => Load(this.seedFilePath));
    }

    public IReadOnlyList<ModelInfo> All => this.effective ?? this.baseModels.Value;

    public string SeedFilePath => this.seedFilePath;

    public bool CatalogEnriched { get; private set; }

    public IReadOnlyCollection<string> LiveCatalogIds => this.liveCatalog;

    public void RecordLiveCatalog(IReadOnlyCollection<string> liveModelIds)
    {
        if (liveModelIds is null) return;
        this.liveCatalog = new HashSet<string>(liveModelIds, StringComparer.OrdinalIgnoreCase);
    }

    public string? ValidateAssignable(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var id = modelId.Trim();

        var model = this.Find(id);
        if (model is null)
        {
            return $"Model '{id}' is not in the Forge model catalog (it may have been retired/removed). "
                + "Pick a model from the catalog.";
        }

        if (!string.Equals(model.Status, "live", StringComparison.OrdinalIgnoreCase))
        {
            return $"Model '{id}' is '{model.Status}', not live — it can no longer be assigned.";
        }

        // Only Copilot-vendor models route through the live Copilot catalog;
        // anthropic (Claude SDK) and forge (local-stub) models use other
        // backends and are exempt. Fail OPEN when the live catalog hasn't been
        // fetched yet.
        var live = this.liveCatalog;
        if (string.Equals(model.Vendor, "copilot", StringComparison.OrdinalIgnoreCase)
            && live.Count > 0
            && !live.Contains(id))
        {
            return $"Model '{id}' is not in the live Copilot catalog and would be rejected by the "
                + "runner at session.create. Pick an available model.";
        }

        return null;
    }

    public ModelInfo? Find(string id)
        => this.All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public void ApplyCatalogOverlay(
        IReadOnlyDictionary<string, ModelCatalogOverlay> overlay,
        IReadOnlyList<ModelInfo>? discoveredModels = null)
    {
        var hasOverlay = overlay is { Count: > 0 };
        var hasDiscovered = discoveredModels is { Count: > 0 };
        if (!hasOverlay && !hasDiscovered) return;

        lock (this.overlayLock)
        {
            // Always rebuild from the immutable JSON base so repeated overlays
            // (refresh cadence) are idempotent rather than compounding.
            var seed = this.baseModels.Value;
            var rebuilt = new List<ModelInfo>(seed.Count + (discoveredModels?.Count ?? 0));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in seed)
            {
                seen.Add(m.Id);
                if (hasOverlay && overlay.TryGetValue(m.Id, out var ov))
                {
                    rebuilt.Add(m with
                    {
                        ContextWindow = ov.ContextWindow ?? m.ContextWindow,
                        ReasoningOverride = ov.SupportsReasoning ?? m.ReasoningOverride,
                        DefaultReasoningEffort = ov.DefaultReasoningEffort ?? m.DefaultReasoningEffort,
                    });
                }
                else
                {
                    rebuilt.Add(m);
                }
            }

            // Append live-catalog models the seed doesn't list (auto-discovered
            // by the scheduled enrichment). A seed id always wins, so its
            // curated pricing is preserved; intra-list duplicates are dropped.
            if (hasDiscovered)
            {
                foreach (var d in discoveredModels!)
                {
                    if (d is null || string.IsNullOrWhiteSpace(d.Id)) continue;
                    if (!seen.Add(d.Id)) continue;
                    rebuilt.Add(d);
                }
            }

            this.effective = rebuilt;
            this.CatalogEnriched = true;
        }
    }

    private static string ResolveSeedFilePath(string contentRoot)
    {
        // Probe the published location first, fall back to the source tree
        // when running tests from the project's bin/ folder. When neither
        // exists yet, return the primary candidate so a writer still has a
        // deterministic target.
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Agents", "Models", SeedFileName),
            Path.Combine(AppContext.BaseDirectory, "Agents", "Models", SeedFileName),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IReadOnlyList<ModelInfo> Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Model registry seed '{SeedFileName}' not found at '{path}'.");
        }

        using var stream = File.OpenRead(path);
        var list = JsonSerializer.Deserialize<List<ModelInfo>>(stream, JsonOpts)
            ?? throw new InvalidOperationException($"{SeedFileName} parsed to null");
        return list;
    }
}
