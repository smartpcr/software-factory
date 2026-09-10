// -----------------------------------------------------------------------
// <copyright file="ModelSeedFileWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Api.Contracts;

/// <summary>
/// 2026-07-14: appends newly discovered live Copilot models to the on-disk
/// <c>models.json</c> seed so the scheduled enrichment keeps the file current,
/// not just the in-memory registry. 2026-09-10: also rewrites the
/// <c>status</c> of models the live catalog retired (or brought back).
///
/// <para>The file is treated as a raw JSON array (<see cref="JsonNode"/>) rather
/// than deserialized to <see cref="ModelInfo"/> so existing entries, including
/// their hand-curated <c>_note</c> annotations and formatting choices, are
/// preserved verbatim. Only ids not already present are appended. The write is
/// atomic (temp file then replace) and best-effort: any failure returns 0 and
/// leaves the file untouched.</para>
/// </summary>
internal static class ModelSeedFileWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Outcome of a <see cref="Sync"/> call.</summary>
    internal readonly record struct SeedSyncResult(int Added, int Retired, int Revived)
    {
        internal bool Changed => this.Added > 0 || this.Retired > 0 || this.Revived > 0;
    }

    /// <summary>
    /// Append every model in <paramref name="discovered"/> whose id is not
    /// already in the seed file at <paramref name="seedFilePath"/>. Returns the
    /// number of entries added (0 when nothing was new or on any failure).
    /// </summary>
    internal static int AppendDiscovered(string seedFilePath, IReadOnlyList<ModelInfo> discovered)
        => Sync(seedFilePath, discovered, null).Added;

    /// <summary>
    /// Apply one enrichment cycle to the on-disk seed: append models the file
    /// doesn't list yet AND rewrite the <c>status</c> of ids in
    /// <paramref name="statusOverrides"/> (id → <c>"retired"</c> / <c>"live"</c>).
    /// Ids already carrying the requested status are left untouched, so the call
    /// is idempotent and only writes when something genuinely changed.
    /// Best-effort: any failure returns an all-zero result and leaves the file
    /// as it was.
    /// </summary>
    internal static SeedSyncResult Sync(
        string seedFilePath,
        IReadOnlyList<ModelInfo>? discovered,
        IReadOnlyDictionary<string, string>? statusOverrides)
    {
        var none = default(SeedSyncResult);
        if (string.IsNullOrWhiteSpace(seedFilePath) || !File.Exists(seedFilePath)) return none;

        var hasDiscovered = discovered is { Count: > 0 };
        var hasStatus = statusOverrides is { Count: > 0 };
        if (!hasDiscovered && !hasStatus) return none;

        JsonArray array;
        try
        {
            var raw = File.ReadAllText(seedFilePath);
            if (JsonNode.Parse(raw) is not JsonArray parsed) return none;
            array = parsed;
        }
        catch (JsonException)
        {
            return none;
        }

        var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var retired = 0;
        var revived = 0;
        foreach (var item in array)
        {
            if (item is not JsonObject obj
                || !obj.TryGetPropertyValue("id", out var idNode)
                || idNode is not JsonValue idVal
                || !idVal.TryGetValue<string>(out var id)
                || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            existing.Add(id);

            if (!hasStatus || !statusOverrides!.TryGetValue(id, out var desired)
                || string.IsNullOrWhiteSpace(desired))
            {
                continue;
            }

            var current = obj.TryGetPropertyValue("status", out var sNode) && sNode is JsonValue sVal
                && sVal.TryGetValue<string>(out var s) ? s : null;
            if (string.Equals(current, desired, System.StringComparison.OrdinalIgnoreCase)) continue;

            obj["status"] = desired;
            obj["_statusChanged_note"] = string.Equals(desired, "retired", System.StringComparison.OrdinalIgnoreCase)
                ? $"Retired {System.DateTime.UtcNow:yyyy-MM-dd} — the live Copilot catalog no longer serves this "
                    + "model, so the runner would reject it at session.create. Kept for historical cost lookups."
                : $"Revived {System.DateTime.UtcNow:yyyy-MM-dd} — the live Copilot catalog serves this model again.";

            if (string.Equals(desired, "retired", System.StringComparison.OrdinalIgnoreCase)) retired++;
            else revived++;
        }

        var added = 0;
        if (hasDiscovered)
        {
            foreach (var model in discovered!)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Id)) continue;
                if (!existing.Add(model.Id)) continue;
                array.Add(ToJsonObject(model));
                added++;
            }
        }

        var result = new SeedSyncResult(added, retired, revived);
        if (!result.Changed) return none;

        var tmp = seedFilePath + ".tmp";
        File.WriteAllText(tmp, array.ToJsonString(WriteOptions));
        File.Move(tmp, seedFilePath, overwrite: true);
        return result;
    }

    private static JsonObject ToJsonObject(ModelInfo model)
    {
        var obj = new JsonObject
        {
            ["id"] = model.Id,
            ["vendor"] = model.Vendor,
            ["family"] = model.Family,
            ["displayName"] = model.DisplayName,
            ["_autoAdded_note"] = $"Auto-added {System.DateTime.UtcNow:yyyy-MM-dd} by the weekly model-catalog "
                + "enrichment from the live Copilot catalog. Pricing is an estimate; adjust if needed.",
            ["contextWindow"] = model.ContextWindow,
            ["inputPricePerMTok"] = model.InputPricePerMTok,
            ["outputPricePerMTok"] = model.OutputPricePerMTok,
            ["supportsTools"] = model.SupportsTools,
            ["supportsStreaming"] = model.SupportsStreaming,
            ["status"] = model.Status,
        };

        if (model.ReasoningOverride is bool reasoning)
        {
            obj["reasoningOverride"] = reasoning;
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            obj["defaultReasoningEffort"] = model.DefaultReasoningEffort;
        }

        return obj;
    }
}
