// -----------------------------------------------------------------------
// <copyright file="ModelCatalogEnrichmentTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Contracts;
using Forge.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// 2026-07-14: covers the scheduled model-catalog refresh AUTO-ADDING live
/// Copilot models the hand-maintained seed doesn't list yet (the gap that kept
/// gpt-5.6-* and claude-sonnet-5 out of the registry): the pure
/// <see cref="ModelCatalogMapper"/> inference/skip rules, the
/// <see cref="ModelRegistry"/> seed+overlay+discovered merge, the weekly
/// Sunday-midnight cadence, and persistence back to the seed file.
/// </summary>
public sealed class ModelCatalogEnrichmentTests : IDisposable
{
    private readonly List<string> tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in this.tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- ModelCatalogMapper (pure inference + skip rules) -----------------

    [Fact]
    public void Mapper_MapsNewModel_WithCopilotVendorInferredFamilyAndReasoning()
    {
        var el = Parse("""
            {
              "id": "gpt-5.6-sol",
              "name": "GPT-5.6 Sol",
              "capabilities": { "supports": { "reasoningEffort": true },
                                "limits": { "max_context_window_tokens": 1050000 } },
              "billing": { "multiplier": null }
            }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        Assert.True(ModelCatalogMapper.IsDiscoverable(live));

        var model = ModelCatalogMapper.ToDiscovered(live);
        Assert.Equal("gpt-5.6-sol", model.Id);
        Assert.Equal("copilot", model.Vendor);
        Assert.Equal("gpt-5", model.Family);
        Assert.Equal("GPT-5.6 Sol", model.DisplayName);
        Assert.Equal(1_050_000, model.ContextWindow);
        Assert.Equal("live", model.Status);
        Assert.True(model.SupportsTools);
        Assert.True(model.ReasoningOverride);
    }

    [Fact]
    public void Mapper_SkipsAutoRouter_NoUsableContextWindow()
    {
        var el = Parse("""
            { "id": "auto", "name": "Auto",
              "capabilities": { "limits": { "max_context_window_tokens": 0 } } }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        Assert.Null(live.ContextWindow);
        Assert.False(ModelCatalogMapper.IsDiscoverable(live));
    }

    [Fact]
    public void Mapper_SkipsOrgNamespacedFoundryModel()
    {
        var el = Parse("""
            { "id": "octodemo/Octodemo_Foundry/DeepSeek-V4-Flash", "name": "DeepSeek",
              "capabilities": { "limits": { "max_context_window_tokens": 128000 } } }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        Assert.False(ModelCatalogMapper.IsDiscoverable(live));
    }

    [Theory]
    [InlineData("gpt-5.6-sol", "gpt-5")]
    [InlineData("gpt-4.1", "gpt-4")]
    [InlineData("claude-sonnet-5", "claude-5")]
    [InlineData("claude-opus-4.8-fast", "claude-4")]
    [InlineData("gemini-3.5-flash", "gemini-3")]
    [InlineData("kimi-k2.7-code", "kimi")]
    [InlineData("mai-code-1-flash-picker", "mai")]
    public void Mapper_InfersFamily(string id, string expectedFamily)
        => Assert.Equal(expectedFamily, ModelCatalogMapper.InferFamily(id));

    [Theory]
    [InlineData("claude-opus-4.8-fast", 15, 75)]
    [InlineData("claude-sonnet-5", 3, 15)]
    [InlineData("claude-haiku-4.5", 1, 5)]
    [InlineData("gpt-5.6-terra", 3, 15)]
    [InlineData("gpt-5.4-mini", 1, 5)]
    [InlineData("gemini-3.5-flash", 1, 5)]
    public void Mapper_InfersPricing(string id, int input, int output)
    {
        var (inPrice, outPrice) = ModelCatalogMapper.InferPricing(id);
        Assert.Equal(input, inPrice);
        Assert.Equal(output, outPrice);
    }

    // ---- ModelRegistry seed + overlay + discovered merge ------------------

    [Fact]
    public void Registry_AutoAddsDiscoveredModelAbsentFromSeed()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"), SeedJson("local-stub", vendor: "forge"));
        Assert.Null(reg.Find("gpt-5.6-sol"));

        reg.ApplyCatalogOverlay(EmptyOverlay, new[] { Discovered("gpt-5.6-sol") });

        var added = reg.Find("gpt-5.6-sol");
        Assert.NotNull(added);
        Assert.Equal("copilot", added!.Vendor);
        Assert.Equal(3, reg.All.Count); // 2 seed + 1 discovered
    }

    [Fact]
    public void Registry_SeedEntryWinsOverDiscoveredDuplicate()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4", input: 3m, output: 15m, display: "GPT-5.4"));

        // A discovered entry with the SAME id but bogus pricing must not clobber
        // the curated seed row, and must not duplicate it.
        var dupe = new ModelInfo("gpt-5.4", "copilot", "gpt-5", "DISCOVERED-DUP", 999_999, 999m, 999m, true, true, "live");
        reg.ApplyCatalogOverlay(EmptyOverlay, new[] { dupe });

        Assert.Single(reg.All.Where(m => string.Equals(m.Id, "gpt-5.4", System.StringComparison.OrdinalIgnoreCase)));
        var kept = reg.Find("gpt-5.4")!;
        Assert.Equal("GPT-5.4", kept.DisplayName);
        Assert.Equal(3m, kept.InputPricePerMTok);
    }

    [Fact]
    public void Registry_MergeIsIdempotentAcrossRefreshes()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"));
        var discovered = new[] { Discovered("gpt-5.6-sol"), Discovered("claude-sonnet-5") };

        reg.ApplyCatalogOverlay(EmptyOverlay, discovered);
        reg.ApplyCatalogOverlay(EmptyOverlay, discovered);

        Assert.Equal(3, reg.All.Count); // 1 seed + 2 discovered, no compounding
    }

    [Fact]
    public void Registry_DropsDiscoveredModelWhenCatalogNoLongerServesIt()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"));
        var overlay = new Dictionary<string, ModelCatalogOverlay>
        {
            ["gpt-5.4"] = new ModelCatalogOverlay(1_050_000, null),
        };

        reg.ApplyCatalogOverlay(overlay, new[] { Discovered("gpt-5.6-sol") });
        Assert.NotNull(reg.Find("gpt-5.6-sol"));

        // Next refresh: overlay still present but the model is gone from the
        // live catalog → not passed as discovered → dropped from the registry.
        reg.ApplyCatalogOverlay(overlay, System.Array.Empty<ModelInfo>());
        Assert.Null(reg.Find("gpt-5.6-sol"));
        Assert.Equal(1_050_000, reg.Find("gpt-5.4")!.ContextWindow);
    }

    [Fact]
    public void Registry_AutoAddedModelBecomesAssignable_WhenRecordedLive()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"));

        // Before discovery: not in the catalog at all → not assignable.
        Assert.NotNull(reg.ValidateAssignable("gpt-5.6-sol"));

        reg.ApplyCatalogOverlay(EmptyOverlay, new[] { Discovered("gpt-5.6-sol") });
        reg.RecordLiveCatalog(new[] { "gpt-5.4", "gpt-5.6-sol" });

        // Now present in the registry AND the live catalog → assignable.
        Assert.Null(reg.ValidateAssignable("gpt-5.6-sol"));
    }

    // ---- (c) context = max_prompt_tokens, (b) defaultReasoningEffort --------

    [Fact]
    public void Mapper_PrefersMaxPromptTokensOverContextWindow()
    {
        var el = Parse("""
            {
              "id": "gpt-5.6-terra", "name": "GPT-5.6 Terra",
              "capabilities": { "supports": { "reasoningEffort": true },
                                "limits": { "max_prompt_tokens": 922000,
                                            "max_context_window_tokens": 1050000 } },
              "defaultReasoningEffort": "medium"
            }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        Assert.Equal(922_000, live.ContextWindow);
        Assert.Equal("medium", live.DefaultReasoningEffort);
        Assert.Equal(922_000, ModelCatalogMapper.ToDiscovered(live).ContextWindow);
    }

    [Fact]
    public void Mapper_FallsBackToContextWindow_WhenNoMaxPrompt()
    {
        var el = Parse("""
            { "id": "kimi-k2.7-code", "name": "Kimi",
              "capabilities": { "limits": { "max_context_window_tokens": 256000 } } }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        Assert.Equal(256_000, live.ContextWindow);
    }

    [Fact]
    public void Mapper_CapturesDefaultReasoningEffort()
    {
        var el = Parse("""
            {
              "id": "claude-opus-4.8", "name": "Claude Opus 4.8",
              "capabilities": { "supports": { "reasoningEffort": true },
                                "limits": { "max_prompt_tokens": 936000 } },
              "defaultReasoningEffort": "max"
            }
            """);

        Assert.True(ModelCatalogMapper.TryMap(el, out var live));
        var model = ModelCatalogMapper.ToDiscovered(live);
        Assert.Equal("max", model.DefaultReasoningEffort);
        Assert.True(model.SupportsReasoning);
    }

    [Fact]
    public void Registry_OverlayAppliesContextAndReasoningEffort()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"));
        var overlay = new Dictionary<string, ModelCatalogOverlay>
        {
            ["gpt-5.4"] = new ModelCatalogOverlay(922_000, true, "max"),
        };

        reg.ApplyCatalogOverlay(overlay);

        var m = reg.Find("gpt-5.4")!;
        Assert.Equal(922_000, m.ContextWindow);
        Assert.Equal("max", m.DefaultReasoningEffort);
        Assert.True(m.SupportsReasoning);
    }

    // ---- daily midnight schedule -------------------------------------------

    [Theory]
    [InlineData("2026-07-15T10:30:00")]
    [InlineData("2026-07-14T00:00:00")]
    [InlineData("2026-07-18T23:59:59")]
    public void Schedule_AlwaysLandsOnStrictlyFutureMidnight(string nowIso)
    {
        var now = DateTime.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        var next = now + ModelCatalogEnrichmentService.TimeUntilNextMidnight(now);

        Assert.Equal(TimeSpan.Zero, next.TimeOfDay);
        Assert.True(next > now);
        Assert.True(next - now <= TimeSpan.FromDays(1));
    }

    [Fact]
    public void Schedule_WhenExactlyMidnight_WaitsFullDay()
    {
        var midnight = new DateTime(2026, 7, 15, 0, 0, 0);
        Assert.Equal(TimeSpan.FromDays(1), ModelCatalogEnrichmentService.TimeUntilNextMidnight(midnight));

        var oneHourBefore = midnight.AddDays(1).AddHours(-1);
        Assert.Equal(TimeSpan.FromHours(1), ModelCatalogEnrichmentService.TimeUntilNextMidnight(oneHourBefore));
    }

    // ---- retire / revive against the live catalog ---------------------------

    [Fact]
    public void Status_RetiresCopilotModelTheLiveCatalogNoLongerServes()
    {
        var catalog = new[]
        {
            Model("gpt-5.4"), Model("gpt-5.2"), Model("claude-opus-4.6"),
            Model("gpt-5.6-sol"), Model("gemini-3.8-flash"), Model("grok-4.6"),
        };
        var live = new[] { "gpt-5.4", "gpt-5.6-sol", "gemini-3.8-flash", "grok-4.6", "claude-opus-5" };

        var changes = ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, live);

        Assert.Equal(2, changes.Count);
        Assert.Equal("retired", changes["gpt-5.2"]);
        Assert.Equal("retired", changes["claude-opus-4.6"]);
        Assert.False(changes.ContainsKey("gpt-5.4"));
    }

    [Fact]
    public void Status_RevivesRetiredModelThatCameBack()
    {
        var catalog = new[]
        {
            Model("gpt-5.4"), Model("gpt-5.2", status: "retired"),
            Model("gpt-5.6-sol"), Model("grok-4.6"), Model("gemini-3.8-flash"),
        };
        var live = new[] { "gpt-5.4", "gpt-5.2", "gpt-5.6-sol", "grok-4.6", "gemini-3.8-flash" };

        var changes = ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, live);

        Assert.Equal("live", Assert.Single(changes).Value);
        Assert.True(changes.ContainsKey("gpt-5.2"));
    }

    [Fact]
    public void Status_NeverRetiresNonCopilotVendors()
    {
        // anthropic models run through the Claude SDK and forge/local-stub
        // bypasses every backend, so neither is ever in the Copilot catalog.
        var catalog = new[]
        {
            Model("claude-opus-4-7", vendor: "anthropic"),
            Model("local-stub", vendor: "forge"),
            Model("gpt-5.4"), Model("gpt-5.6-sol"), Model("grok-4.6"), Model("gemini-3.8-flash"),
        };
        var live = new[] { "gpt-5.4", "gpt-5.6-sol", "grok-4.6", "gemini-3.8-flash", "claude-opus-5" };

        Assert.Empty(ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, live));
    }

    [Fact]
    public void Status_SkipsSuspiciouslySmallCatalog_SoAPartialFetchCannotMassRetire()
    {
        var catalog = new[] { Model("gpt-5.4"), Model("gpt-5.2"), Model("claude-opus-4.6") };

        Assert.Empty(ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, new[] { "gpt-5.4" }));
        Assert.Empty(ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, System.Array.Empty<string>()));
    }

    [Fact]
    public void Status_LeavesCuratedNonLiveNonRetiredStatusAlone()
    {
        var catalog = new[]
        {
            Model("gpt-5.9-preview", status: "preview"),
            Model("gpt-5.4"), Model("gpt-5.6-sol"), Model("grok-4.6"), Model("gemini-3.8-flash"),
        };
        var live = new[] { "gpt-5.4", "gpt-5.6-sol", "grok-4.6", "gemini-3.8-flash", "claude-opus-5" };

        Assert.Empty(ModelCatalogEnrichmentService.BuildStatusOverrides(catalog, live));
    }

    [Fact]
    public void Registry_StatusOverrideRetiresModelAndBlocksAssignment()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"), SeedJson("gpt-5.2"));
        reg.RecordLiveCatalog(new[] { "gpt-5.4" });

        reg.ApplyCatalogOverlay(
            EmptyOverlay,
            null,
            new Dictionary<string, string> { ["gpt-5.2"] = "retired" });

        Assert.Equal("retired", reg.Find("gpt-5.2")!.Status);
        Assert.Equal("live", reg.Find("gpt-5.4")!.Status);
        Assert.NotNull(reg.ValidateAssignable("gpt-5.2"));
        Assert.Null(reg.ValidateAssignable("gpt-5.4"));
    }

    [Fact]
    public void Registry_StatusOverrideIsRebuiltFromSeed_SoRevivalWorks()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"), SeedJson("gpt-5.2"));

        reg.ApplyCatalogOverlay(EmptyOverlay, null, new Dictionary<string, string> { ["gpt-5.2"] = "retired" });
        Assert.Equal("retired", reg.Find("gpt-5.2")!.Status);

        // Next cycle: the model is served again → no override → the immutable
        // seed's "live" is restored rather than the retirement compounding.
        reg.ApplyCatalogOverlay(EmptyOverlay, null, new Dictionary<string, string> { ["gpt-5.4"] = "retired" });
        Assert.Equal("live", reg.Find("gpt-5.2")!.Status);
        Assert.Equal("retired", reg.Find("gpt-5.4")!.Status);
    }

    // ---- persistence to the seed file --------------------------------------

    [Fact]
    public void SeedWriter_AppendsNewModel_PreservingExistingEntries()
    {
        var path = WriteTempSeed(
            """{ "id": "gpt-5.4", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.4", "_note": "curated", "contextWindow": 1050000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""");

        var added = ModelSeedFileWriter.AppendDiscovered(path, new[] { Discovered("gpt-5.6-sol", 922_000, true, "medium") });

        Assert.Equal(1, added);
        var text = File.ReadAllText(path);
        Assert.Contains("\"_note\": \"curated\"", text); // curated annotation preserved

        var models = JsonSerializer.Deserialize<List<ModelInfo>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, models.Count);
        var addedModel = models.Single(m => m.Id == "gpt-5.6-sol");
        Assert.Equal("copilot", addedModel.Vendor);
        Assert.Equal("gpt-5", addedModel.Family);
        Assert.Equal(922_000, addedModel.ContextWindow);
        Assert.Equal("medium", addedModel.DefaultReasoningEffort);
    }

    [Fact]
    public void SeedWriter_SkipsIdsAlreadyPresent()
    {
        var path = WriteTempSeed(
            """{ "id": "gpt-5.4", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.4", "contextWindow": 1050000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""");

        var added = ModelSeedFileWriter.AppendDiscovered(path, new[] { Discovered("gpt-5.4") });

        Assert.Equal(0, added);
        var models = JsonSerializer.Deserialize<List<ModelInfo>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(models);
    }

    [Fact]
    public void SeedWriter_ReturnsZero_WhenFileMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "forge-missing-" + System.Guid.NewGuid().ToString("N"), "models.json");
        Assert.Equal(0, ModelSeedFileWriter.AppendDiscovered(missing, new[] { Discovered("gpt-5.6-sol") }));
    }

    [Fact]
    public void SeedWriter_RewritesStatusForRetiredAndRevivedModels()
    {
        var path = WriteTempSeed(
            """{ "id": "gpt-5.4", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.4", "_note": "curated", "contextWindow": 1050000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""",
            """{ "id": "gpt-5.2", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.2", "contextWindow": 128000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""",
            """{ "id": "gpt-4.1", "vendor": "copilot", "family": "gpt-4", "displayName": "GPT-4.1", "contextWindow": 128000, "inputPricePerMTok": 1.0, "outputPricePerMTok": 5.0, "supportsTools": true, "supportsStreaming": true, "status": "retired" }""");

        var result = ModelSeedFileWriter.Sync(
            path,
            null,
            new Dictionary<string, string> { ["gpt-5.2"] = "retired", ["gpt-4.1"] = "live" });

        Assert.Equal(1, result.Retired);
        Assert.Equal(1, result.Revived);
        Assert.Equal(0, result.Added);

        var text = File.ReadAllText(path);
        Assert.Contains("\"_note\": \"curated\"", text); // curated annotations preserved

        var models = JsonSerializer.Deserialize<List<ModelInfo>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("live", models.Single(m => m.Id == "gpt-5.4").Status);
        Assert.Equal("retired", models.Single(m => m.Id == "gpt-5.2").Status);
        Assert.Equal("live", models.Single(m => m.Id == "gpt-4.1").Status);

        // Pricing survives retirement — crew history still resolves the model.
        Assert.Equal(3m, models.Single(m => m.Id == "gpt-5.2").InputPricePerMTok);
        Assert.Equal(3, models.Count); // retire never deletes
    }

    [Fact]
    public void SeedWriter_IsIdempotent_WhenStatusAlreadyMatches()
    {
        var path = WriteTempSeed(
            """{ "id": "gpt-5.4", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.4", "contextWindow": 1050000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""");
        var before = File.ReadAllText(path);

        var result = ModelSeedFileWriter.Sync(path, null, new Dictionary<string, string> { ["gpt-5.4"] = "live" });

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllText(path)); // untouched, not reformatted
    }

    [Fact]
    public void SeedWriter_AddsAndRetiresInOneCycle()
    {
        var path = WriteTempSeed(
            """{ "id": "gpt-5.2", "vendor": "copilot", "family": "gpt-5", "displayName": "GPT-5.2", "contextWindow": 128000, "inputPricePerMTok": 3.0, "outputPricePerMTok": 15.0, "supportsTools": true, "supportsStreaming": true, "status": "live" }""");

        var result = ModelSeedFileWriter.Sync(
            path,
            new[] { Discovered("gpt-6-astra", 872_000, true, "medium") },
            new Dictionary<string, string> { ["gpt-5.2"] = "retired" });

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Retired);

        var models = JsonSerializer.Deserialize<List<ModelInfo>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("retired", models.Single(m => m.Id == "gpt-5.2").Status);
        Assert.Equal("live", models.Single(m => m.Id == "gpt-6-astra").Status);
    }

    [Fact]
    public void Registry_SeedFilePath_PointsToLoadedFile()
    {
        var reg = BuildRegistry(SeedJson("gpt-5.4"));
        Assert.True(File.Exists(reg.SeedFilePath));
        Assert.EndsWith(Path.Combine("Agents", "Models", "models.json"), reg.SeedFilePath);
    }

    /// <summary>
    /// The launcher starts Forge.Api.exe out of bin\, so the content root is the
    /// BUILD OUTPUT. Before this, the enrichment persisted every discovered model
    /// there — invisible to the repo and silently discarded the next time MSBuild
    /// re-copied the (newer) source seed. Read and write must both land on the
    /// SOURCE file.
    /// </summary>
    [Fact]
    public void Registry_ResolvesSourceSeed_WhenRunningFromProjectBinOutput()
    {
        var project = Path.Combine(Path.GetTempPath(), "forge-proj-" + System.Guid.NewGuid().ToString("N"));
        this.tempDirs.Add(project);

        var sourceSeedDir = Path.Combine(project, "Agents", "Models");
        var binSeedDir = Path.Combine(project, "bin", "Debug", "net8.0", "win-x64", "Agents", "Models");
        Directory.CreateDirectory(sourceSeedDir);
        Directory.CreateDirectory(binSeedDir);
        File.WriteAllText(Path.Combine(project, "Forge.Api.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(sourceSeedDir, "models.json"), "[" + SeedJson("gpt-5.4") + "]");
        File.WriteAllText(Path.Combine(binSeedDir, "models.json"), "[" + SeedJson("stale-bin-copy") + "]");

        var reg = new ModelRegistry(new FakeEnv
        {
            ContentRootPath = Path.Combine(project, "bin", "Debug", "net8.0", "win-x64"),
        });

        Assert.Equal(Path.Combine(sourceSeedDir, "models.json"), reg.SeedFilePath);
        Assert.NotNull(reg.Find("gpt-5.4"));       // read came from the source seed
        Assert.Null(reg.Find("stale-bin-copy"));   // not the build output
    }

    [Fact]
    public void Registry_KeepsContentRootSeed_ForPublishedLayoutWithNoProjectFile()
    {
        // A published deployment has no .csproj above the binaries, so the
        // operator-editable content-root copy must stay authoritative.
        var deploy = Path.Combine(Path.GetTempPath(), "forge-publish-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(deploy, "Agents", "Models"));
        this.tempDirs.Add(deploy);
        File.WriteAllText(Path.Combine(deploy, "Agents", "Models", "models.json"), "[" + SeedJson("gpt-5.4") + "]");

        var reg = new ModelRegistry(new FakeEnv { ContentRootPath = deploy });

        Assert.Equal(Path.Combine(deploy, "Agents", "Models", "models.json"), reg.SeedFilePath);
        Assert.NotNull(reg.Find("gpt-5.4"));
    }

    // ---- helpers ----------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, ModelCatalogOverlay> EmptyOverlay =
        new Dictionary<string, ModelCatalogOverlay>();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static ModelInfo Discovered(string id, int ctx = 1_000_000, bool reasoning = true, string? effort = "medium")
        => ModelCatalogMapper.ToDiscovered(new ModelCatalogMapper.LiveCatalogModel(id, id, ctx, reasoning, effort));

    /// <summary>A catalog entry as <see cref="ModelCatalogEnrichmentService.BuildStatusOverrides"/> sees it.</summary>
    private static ModelInfo Model(string id, string vendor = "copilot", string status = "live")
        => new(id, vendor, "gpt-5", id, 200_000, 3m, 15m, true, true, status);

    private string WriteTempSeed(params string[] seedObjects)
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-seed-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.tempDirs.Add(dir);
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, "[" + string.Join(",", seedObjects) + "]");
        return path;
    }

    private static string SeedJson(
        string id,
        string vendor = "copilot",
        string family = "gpt-5",
        decimal input = 3m,
        decimal output = 15m,
        string? display = null)
        => $$"""
            { "id": "{{id}}", "vendor": "{{vendor}}", "family": "{{family}}",
              "displayName": "{{display ?? id}}", "contextWindow": 200000,
              "inputPricePerMTok": {{input}}, "outputPricePerMTok": {{output}},
              "supportsTools": true, "supportsStreaming": true, "status": "live" }
            """;

    private ModelRegistry BuildRegistry(params string[] seedObjects)
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-models-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Agents", "Models"));
        File.WriteAllText(
            Path.Combine(dir, "Agents", "Models", "models.json"),
            "[" + string.Join(",", seedObjects) + "]");
        this.tempDirs.Add(dir);

        var reg = new ModelRegistry(new FakeEnv { ContentRootPath = dir });
        _ = reg.All; // force the lazy seed load while the temp file still exists
        return reg;
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "Forge.Api.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
