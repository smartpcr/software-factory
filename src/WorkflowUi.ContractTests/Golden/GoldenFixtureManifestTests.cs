// -----------------------------------------------------------------------
// <copyright file="GoldenFixtureManifestTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Golden;

using System.Linq;
using Xunit;

/// <summary>
/// Asserts the golden harness sees every authored fixture triplet.
/// </summary>
[Trait("Category", "Golden")]
public class GoldenFixtureManifestTests
{
    [Fact]
    public void Manifest_covers_task_family()
    {
        var fixtures = GoldenFixtureLoader.LoadFamily("task").ToList();
        Assert.Equal(6, fixtures.Count);
    }

    [Fact]
    public void Manifest_covers_control_family()
    {
        var fixtures = GoldenFixtureLoader.LoadFamily("control").ToList();
        Assert.Equal(4, fixtures.Count);
    }

    [Fact]
    public void Manifest_covers_composition_family()
    {
        var fixtures = GoldenFixtureLoader.LoadFamily("composition").ToList();
        Assert.Equal(4, fixtures.Count);
    }

    [Fact]
    public void Manifest_covers_connections_family()
    {
        var fixtures = GoldenFixtureLoader.LoadFamily("connections").ToList();
        Assert.True(fixtures.Count >= 1, "Expected at least 1 connection fixture");
    }

    [Fact]
    public void Total_fixture_count_is_at_least_15()
    {
        var all = GoldenFixtureLoader.LoadAll().ToList();
        Assert.True(all.Count >= 15, $"Expected at least 15 fixtures, found {all.Count}");
    }

    [Fact]
    public void All_families_represented()
    {
        var families = GoldenFixtureLoader.LoadAll()
            .Select(f => f.Family)
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        Assert.Contains("task", families);
        Assert.Contains("control", families);
        Assert.Contains("composition", families);
        Assert.Contains("connections", families);
    }
}
