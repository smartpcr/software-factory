// -----------------------------------------------------------------------
// <copyright file="GoldenFileTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Golden;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.ContractTests.Helpers;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Golden-file tests that verify fixture triplets round-trip through the
/// real engine serializer and mapper pipeline:
///   YAML -> WorkflowSerializer.FromYaml -> EngineDtoMapper.MapWorkflowToDto -> golden DTO
///   engine -> WorkflowSerializer.ToYaml -> canonical YAML
/// </summary>
[Trait("Category", "Golden")]
public class GoldenFileTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly WorkflowSerializer Serializer = new();

    [Fact]
    public void Golden_harness_enumerates_every_fixture_triplet()
    {
        var fixtures = GoldenFixtureLoader.LoadAll().ToList();
        Assert.NotEmpty(fixtures);
        Assert.True(fixtures.Count >= 11, $"Expected at least 11 fixtures, found {fixtures.Count}");
    }

    [Fact]
    public void Every_fixture_has_complete_triplet()
    {
        var fixtures = GoldenFixtureLoader.LoadAll().ToList();
        foreach (var fixture in fixtures)
        {
            Assert.True(fixture.IsComplete,
                $"Fixture {fixture.Family}/{fixture.FixtureId} is missing " +
                $"{(fixture.UiJsonPath == null ? ".ui.json" : "")} " +
                $"{(fixture.GoldenJsonPath == null ? ".golden.json" : "")}");
        }
    }

    [Fact]
    public void Golden_fixture_ids_are_stable()
    {
        var expectedTaskIds = new[] { "csharp-compiled", "csharp-script-inline", "csharp-task-inline", "noop-basic", "powershell-path", "powershell-task-inline" };
        var expectedControlIds = new[] { "foreach-basic", "ifelse-basic", "switch-three-case", "while-default-max-iterations" };
        var expectedCompositionIds = new[] { "container-parallel", "subflow-file", "subflow-inline", "timer-cron" };

        var taskFixtures = GoldenFixtureLoader.LoadFamily("task").Select(f => f.FixtureId).OrderBy(f => f).ToArray();
        var controlFixtures = GoldenFixtureLoader.LoadFamily("control").Select(f => f.FixtureId).OrderBy(f => f).ToArray();
        var compositionFixtures = GoldenFixtureLoader.LoadFamily("composition").Select(f => f.FixtureId).OrderBy(f => f).ToArray();

        Assert.Equal(expectedTaskIds, taskFixtures);
        Assert.Equal(expectedControlIds, controlFixtures);
        Assert.Equal(expectedCompositionIds, compositionFixtures);
    }

    [Fact]
    public void Sidecars_contain_ui_only_state()
    {
        foreach (var fixture in GoldenFixtureLoader.LoadAll())
        {
            if (fixture.UiJsonContent == null)
            {
                continue;
            }

            Assert.True(
                SidecarMerger.ContainsOnlyUiMetadata(fixture.UiJsonContent),
                $"Sidecar {fixture.Family}/{fixture.FixtureId} contains engine-owned fields");
        }
    }

    /// <summary>
    /// For each fixture, loads YAML via the real engine WorkflowSerializer,
    /// maps to DTO via EngineDtoMapper, and compares the DTO against the
    /// authored .golden.json content. When WORKFLOWUI_WRITE_FIXTURES=1,
    /// regenerates golden files from the real pipeline instead of comparing.
    /// </summary>
    [Fact]
    public void Golden_DTO_roundtrip_via_real_serializer_and_mapper()
    {
        var writeMode = Environment.GetEnvironmentVariable("WORKFLOWUI_WRITE_FIXTURES") == "1";

        foreach (var fixture in GoldenFixtureLoader.LoadAll())
        {
            Assert.NotNull(fixture.GoldenJsonContent);

            // Load YAML through the real engine serializer
            WorkflowDefinition engineWorkflow;
            try
            {
                engineWorkflow = Serializer.FromYaml(fixture.YamlContent);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Fixture {fixture.Family}/{fixture.FixtureId}: WorkflowSerializer.FromYaml failed: {ex.Message}", ex);
            }

            // Map engine -> DTO through the real mapper
            var dto = EngineDtoMapper.MapWorkflowToDto(engineWorkflow);
            Assert.NotNull(dto);
            Assert.Equal(fixture.FixtureId, dto.WorkflowId, StringComparer.OrdinalIgnoreCase);

            // Serialize the mapped DTO
            var mappedJson = NormalizeJson(JsonSerializer.Serialize(dto, JsonOpts));

            if (writeMode && fixture.GoldenJsonPath != null)
            {
                File.WriteAllText(fixture.GoldenJsonPath, mappedJson + "\n");
                continue;
            }

            var goldenJson = NormalizeJson(fixture.GoldenJsonContent!);
            Assert.Equal(goldenJson, mappedJson);
        }
    }

    /// <summary>
    /// For each fixture, loads YAML via engine serializer, then serializes
    /// back to YAML and compares canonicalized output to the authored fixture.
    /// When WORKFLOWUI_WRITE_FIXTURES=1, regenerates YAML fixtures from canonical output.
    /// </summary>
    [Fact]
    public void Canonical_YAML_roundtrip_via_real_serializer()
    {
        var writeMode = Environment.GetEnvironmentVariable("WORKFLOWUI_WRITE_FIXTURES") == "1";

        foreach (var fixture in GoldenFixtureLoader.LoadAll())
        {
            // Load YAML through the real engine serializer
            WorkflowDefinition engineWorkflow;
            try
            {
                engineWorkflow = Serializer.FromYaml(fixture.YamlContent);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Fixture {fixture.Family}/{fixture.FixtureId}: WorkflowSerializer.FromYaml failed: {ex.Message}", ex);
            }

            // Serialize back to YAML
            var reEmittedYaml = Serializer.ToYaml(engineWorkflow);

            if (writeMode)
            {
                File.WriteAllText(fixture.YamlPath, reEmittedYaml);
                continue;
            }

            // Canonicalize both and compare
            var authoredCanonical = CanonicalYaml.Canonicalize(fixture.YamlContent);
            var roundTrippedCanonical = CanonicalYaml.Canonicalize(reEmittedYaml);

            Assert.True(
                string.Equals(authoredCanonical, roundTrippedCanonical, StringComparison.Ordinal),
                $"YAML round-trip drift in fixture {fixture.Family}/{fixture.FixtureId}.\n" +
                $"=== Authored (canonical) ===\n{authoredCanonical}\n" +
                $"=== Round-tripped (canonical) ===\n{roundTrippedCanonical}");
        }
    }

    /// <summary>
    /// Proves the full pipeline: YAML -> engine -> DTO -> engine -> YAML
    /// with both WorkflowSerializer and EngineDtoMapper in the path.
    /// Note: The mapper normalizes null string fields to empty strings
    /// for non-nullable engine properties, so we compare canonicalized
    /// YAML after the first engine load (not the raw authored fixture).
    /// </summary>
    [Fact]
    public void Full_pipeline_YAML_engine_DTO_engine_YAML_roundtrip()
    {
        foreach (var fixture in GoldenFixtureLoader.LoadAll())
        {
            // Step 1: YAML -> engine (first load establishes engine-canonical values)
            var engine1 = Serializer.FromYaml(fixture.YamlContent);

            // Step 1b: engine -> YAML (this is the engine-canonical form)
            var canonicalSource = CanonicalYaml.Canonicalize(Serializer.ToYaml(engine1));

            // Step 2: engine -> DTO
            var dto = EngineDtoMapper.MapWorkflowToDto(engine1);

            // Step 3: DTO -> engine
            var engine2 = EngineDtoMapper.MapWorkflowFromDto(dto);

            // Step 4: engine -> YAML
            var roundTripped = CanonicalYaml.Canonicalize(Serializer.ToYaml(engine2));

            // Compare the canonical engine forms (both went through real serializer)
            Assert.True(
                string.Equals(canonicalSource, roundTripped, StringComparison.Ordinal),
                $"Full pipeline drift in fixture {fixture.Family}/{fixture.FixtureId}.\n" +
                $"=== Engine canonical ===\n{canonicalSource}\n" +
                $"=== After DTO round-trip ===\n{roundTripped}");
        }
    }

    [Fact]
    public void Golden_harness_uses_real_engine_serializer()
    {
        // Structural proof: the harness creates WorkflowSerializer and calls FromYaml/ToYaml
        var serializer = new WorkflowSerializer();
        var fixture = GoldenFixtureLoader.LoadAll().First();
        var workflow = serializer.FromYaml(fixture.YamlContent);
        Assert.NotNull(workflow);
        var yaml = serializer.ToYaml(workflow);
        Assert.False(string.IsNullOrWhiteSpace(yaml));
    }

    [Fact]
    public void Snapshot_is_deterministic_across_two_runs()
    {
        var run1 = GetAllSnapshots();
        var run2 = GetAllSnapshots();
        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].FixtureId, run2[i].FixtureId);
            Assert.Equal(run1[i].HydratedDtoJson, run2[i].HydratedDtoJson);
            Assert.Equal(run1[i].CanonicalYaml, run2[i].CanonicalYaml);
        }
    }

    private static List<GoldenSnapshotModel> GetAllSnapshots()
    {
        return GoldenFixtureLoader.LoadAll()
            .Select(f =>
            {
                var engine = Serializer.FromYaml(f.YamlContent);
                var dto = EngineDtoMapper.MapWorkflowToDto(engine);
                return new GoldenSnapshotModel
                {
                    FixtureId = f.FixtureId,
                    Family = f.Family,
                    CanonicalYaml = CanonicalYaml.Canonicalize(Serializer.ToYaml(engine)),
                    HydratedDtoJson = NormalizeJson(JsonSerializer.Serialize(dto, JsonOpts)),
                };
            })
            .OrderBy(s => s.Family, StringComparer.Ordinal)
            .ThenBy(s => s.FixtureId, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeJson(string json)
    {
        return json.Replace("\r\n", "\n").TrimEnd();
    }
}
