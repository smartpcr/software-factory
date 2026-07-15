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
/// not just the in-memory registry.
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

    /// <summary>
    /// Append every model in <paramref name="discovered"/> whose id is not
    /// already in the seed file at <paramref name="seedFilePath"/>. Returns the
    /// number of entries added (0 when nothing was new or on any failure).
    /// </summary>
    internal static int AppendDiscovered(string seedFilePath, IReadOnlyList<ModelInfo> discovered)
    {
        if (string.IsNullOrWhiteSpace(seedFilePath) || !File.Exists(seedFilePath)) return 0;
        if (discovered is null || discovered.Count == 0) return 0;

        JsonArray array;
        try
        {
            var raw = File.ReadAllText(seedFilePath);
            if (JsonNode.Parse(raw) is not JsonArray parsed) return 0;
            array = parsed;
        }
        catch (JsonException)
        {
            return 0;
        }

        var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (item is JsonObject obj
                && obj.TryGetPropertyValue("id", out var idNode)
                && idNode is JsonValue idVal
                && idVal.TryGetValue<string>(out var id)
                && !string.IsNullOrWhiteSpace(id))
            {
                existing.Add(id);
            }
        }

        var added = 0;
        foreach (var model in discovered)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Id)) continue;
            if (!existing.Add(model.Id)) continue;
            array.Add(ToJsonObject(model));
            added++;
        }

        if (added == 0) return 0;

        var tmp = seedFilePath + ".tmp";
        File.WriteAllText(tmp, array.ToJsonString(WriteOptions));
        File.Move(tmp, seedFilePath, overwrite: true);
        return added;
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
