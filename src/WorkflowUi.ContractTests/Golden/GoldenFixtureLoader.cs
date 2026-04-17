// -----------------------------------------------------------------------
// <copyright file="GoldenFixtureLoader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Golden;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Loads fixture triplets (.yaml, .ui.json, .golden.json) for golden-file testing.
/// </summary>
public static class GoldenFixtureLoader
{
    /// <summary>
    /// Discovers all fixture triplets under the mapper fixtures directory.
    /// </summary>
    public static IEnumerable<FixtureTriplet> LoadAll()
    {
        var fixtureRoot = FindFixtureRoot();
        if (fixtureRoot == null || !Directory.Exists(fixtureRoot))
        {
            yield break;
        }

        var yamlFiles = Directory.GetFiles(fixtureRoot, "*.yaml", SearchOption.AllDirectories);

        foreach (var yamlPath in yamlFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            var basePath = Path.ChangeExtension(yamlPath, null);
            var fixtureId = Path.GetFileNameWithoutExtension(yamlPath);
            var family = Path.GetFileName(Path.GetDirectoryName(yamlPath)) ?? "unknown";

            var uiJsonPath = basePath + ".ui.json";
            var goldenPath = basePath + ".golden.json";

            yield return new FixtureTriplet
            {
                FixtureId = fixtureId,
                Family = family,
                YamlPath = yamlPath,
                UiJsonPath = File.Exists(uiJsonPath) ? uiJsonPath : null,
                GoldenJsonPath = File.Exists(goldenPath) ? goldenPath : null,
                YamlContent = File.ReadAllText(yamlPath),
                UiJsonContent = File.Exists(uiJsonPath) ? File.ReadAllText(uiJsonPath) : null,
                GoldenJsonContent = File.Exists(goldenPath) ? File.ReadAllText(goldenPath) : null,
            };
        }
    }

    /// <summary>
    /// Discovers fixture triplets for a specific family (task, control, composition, connections).
    /// </summary>
    public static IEnumerable<FixtureTriplet> LoadFamily(string family)
    {
        return LoadAll().Where(t => t.Family == family);
    }

    /// <summary>
    /// Gets all fixture IDs in the corpus.
    /// </summary>
    public static IReadOnlyList<string> GetAllFixtureIds()
    {
        return LoadAll().Select(t => t.FixtureId).ToList();
    }

    private static string? FindFixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Workflow.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir != null
            ? Path.Combine(dir, "src", "WorkflowUi.ContractTests", "Fixtures", "mapper")
            : null;
    }
}

/// <summary>
/// Represents a fixture triplet: YAML, sidecar UI JSON, and golden DTO JSON.
/// </summary>
public class FixtureTriplet
{
    public string FixtureId { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    public string YamlPath { get; set; } = string.Empty;

    public string? UiJsonPath { get; set; }

    public string? GoldenJsonPath { get; set; }

    public string YamlContent { get; set; } = string.Empty;

    public string? UiJsonContent { get; set; }

    public string? GoldenJsonContent { get; set; }

    public bool IsComplete => UiJsonPath != null && GoldenJsonPath != null;

    public override string ToString() => $"{Family}/{FixtureId}";
}
