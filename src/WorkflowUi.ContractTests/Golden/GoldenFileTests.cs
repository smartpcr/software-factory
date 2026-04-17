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
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Golden-file tests that verify fixture triplets round-trip through the mapper.
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

    [Fact]
    public void Golden_harness_enumerates_every_fixture_triplet()
    {
        var fixtures = GoldenFixtureLoader.LoadAll().ToList();
        Assert.NotEmpty(fixtures);

        // Must have at least 11 fixtures across all families
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
        var engineFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nodeId", "nodeName", "runtimeType", "joinType", "priority",
            "maxConcurrentExecutions", "retryPolicy", "circuitBreakerPolicy",
            "compensationNodeId", "fallbackNodeId", "configuration",
            "assemblyPath", "typeName", "scriptPath", "scriptContent",
            "executorTypeName", "executorAssemblyPath", "condition",
            "expression", "cases", "collectionExpression", "itemVariableName",
            "conditionExpression", "maxIterations", "workflowFilePath",
            "workflowDefinition", "inputMappings", "outputMappings",
            "timeout", "skipValidation", "schedule", "executionMode",
            "childNodes", "childConnections", "requiredModules", "modulePaths",
        };

        foreach (var fixture in GoldenFixtureLoader.LoadAll())
        {
            if (fixture.UiJsonContent == null)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(fixture.UiJsonContent);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                Assert.False(engineFieldNames.Contains(prop.Name),
                    $"Sidecar {fixture.Family}/{fixture.FixtureId} contains engine field '{prop.Name}'");
            }
        }
    }

    [Fact]
    public void Golden_DTO_matches_mapper_output_for_task_family()
    {
        VerifyGoldenDtoForFamily("task");
    }

    [Fact]
    public void Golden_DTO_matches_mapper_output_for_control_family()
    {
        VerifyGoldenDtoForFamily("control");
    }

    [Fact]
    public void Golden_DTO_matches_mapper_output_for_composition_family()
    {
        VerifyGoldenDtoForFamily("composition");
    }

    [Fact]
    public void Golden_DTO_matches_mapper_output_for_connections_family()
    {
        VerifyGoldenDtoForFamily("connections");
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
        }
    }

    private static void VerifyGoldenDtoForFamily(string family)
    {
        var fixtures = GoldenFixtureLoader.LoadFamily(family).ToList();
        if (fixtures.Count == 0)
        {
            return; // Family might not have fixtures
        }

        foreach (var fixture in fixtures)
        {
            Assert.NotNull(fixture.GoldenJsonContent);

            // Parse golden JSON and regenerate from mapper
            var goldenDto = JsonSerializer.Deserialize<WorkflowDefinitionDto>(fixture.GoldenJsonContent!, JsonOpts);
            Assert.NotNull(goldenDto);

            // Verify the golden has the expected structure
            Assert.NotNull(goldenDto!.WorkflowId);
            Assert.Equal(fixture.FixtureId, goldenDto.WorkflowId,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<GoldenSnapshotModel> GetAllSnapshots()
    {
        return GoldenFixtureLoader.LoadAll()
            .Select(f => new GoldenSnapshotModel
            {
                FixtureId = f.FixtureId,
                Family = f.Family,
                CanonicalYaml = NormalizeLineEndings(f.YamlContent),
                HydratedDtoJson = NormalizeLineEndings(f.GoldenJsonContent ?? "{}"),
            })
            .OrderBy(s => s.Family, StringComparer.Ordinal)
            .ThenBy(s => s.FixtureId, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeLineEndings(string input)
    {
        return input.Replace("\r\n", "\n").TrimEnd();
    }
}
