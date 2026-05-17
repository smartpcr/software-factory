// -----------------------------------------------------------------------
// <copyright file="PhaseMigrationCatalogTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Reflection;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Xunit;

/// <summary>
/// Phase G.4 — invariant tests for the migration catalog. Verifies the
/// classification matches reality (DbBoundary), no phase migration is
/// missing from the catalog, and the catalog has no stale entries.
/// </summary>
public class PhaseMigrationCatalogTests
{
    [Fact]
    public void Catalog_ContainsEveryPhaseMigrationInAssembly()
    {
        var assembly = typeof(PhaseMigrationCatalog).Assembly;
        var migrationTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "Forge.Api.Persistence"
                && t.Name.StartsWith("Phase", StringComparison.Ordinal)
                && t.Name.EndsWith("Migration", StringComparison.Ordinal)
                && t.GetMethod("ApplyAsync", BindingFlags.Public | BindingFlags.Static) is not null)
            .ToList();

        var expectedPhases = migrationTypes
            .Select(t => ParsePhaseNumber(t.Name))
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToHashSet();

        var cataloguedPhases = PhaseMigrationCatalog.All.Select(s => s.PhaseNumber).ToHashSet();
        var missing = expectedPhases.Except(cataloguedPhases).ToList();
        var extra = cataloguedPhases.Except(expectedPhases).ToList();

        Assert.True(missing.Count == 0,
            $"Phase migration class(es) exist in the assembly but are missing from the catalog: " +
            $"[{string.Join(", ", missing.OrderBy(p => p))}]. Add them to PhaseMigrationCatalog.All.");
        Assert.True(extra.Count == 0,
            $"Phase migration(s) are in the catalog but the Phase{{N}}Migration class no longer exists: " +
            $"[{string.Join(", ", extra.OrderBy(p => p))}]. Remove the stale catalog entry.");
    }

    [Fact]
    public void Catalog_ClassificationMatchesDbBoundary()
    {
        var controlBoundary = DbBoundary.AllTables
            .Where(t => t.Kind == DbKind.Control)
            .Select(t => t.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var productBoundary = DbBoundary.AllTables
            .Where(t => t.Kind == DbKind.Product)
            .Select(t => t.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in PhaseMigrationCatalog.All)
        {
            foreach (var ct in spec.ControlTables)
            {
                Assert.True(controlBoundary.Contains(ct),
                    $"Phase{spec.PhaseNumber}Migration is catalogued as touching control table '{ct}', " +
                    $"but DbBoundary doesn't classify '{ct}' as control. Either update the catalog or DbBoundary.");
            }
            foreach (var pt in spec.ProductTables)
            {
                Assert.True(productBoundary.Contains(pt),
                    $"Phase{spec.PhaseNumber}Migration is catalogued as touching product table '{pt}', " +
                    $"but DbBoundary doesn't classify '{pt}' as product. Either update the catalog or DbBoundary.");
            }
        }
    }

    [Fact]
    public void Catalog_KindMatchesTableLists()
    {
        foreach (var spec in PhaseMigrationCatalog.All)
        {
            switch (spec.Kind)
            {
                case PhaseMigrationKind.Control:
                    Assert.True(spec.ControlTables.Count > 0,
                        $"Phase{spec.PhaseNumber} is classified Control but has no control tables.");
                    Assert.Empty(spec.ProductTables);
                    break;
                case PhaseMigrationKind.Product:
                    Assert.Empty(spec.ControlTables);
                    Assert.True(spec.ProductTables.Count > 0,
                        $"Phase{spec.PhaseNumber} is classified Product but has no product tables.");
                    break;
                case PhaseMigrationKind.Mixed:
                    Assert.True(spec.ControlTables.Count > 0,
                        $"Phase{spec.PhaseNumber} is classified Mixed but has no control tables.");
                    Assert.True(spec.ProductTables.Count > 0,
                        $"Phase{spec.PhaseNumber} is classified Mixed but has no product tables.");
                    break;
            }
        }
    }

    [Fact]
    public void Catalog_PhaseNumbersAreUnique()
    {
        var duplicates = PhaseMigrationCatalog.All
            .GroupBy(s => s.PhaseNumber)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate phase numbers in catalog: [{string.Join(", ", duplicates)}].");
    }

    [Fact]
    public void Catalog_Counts_AreSensible()
    {
        var (control, product, mixed) = PhaseMigrationCatalog.Counts;
        Assert.True(control + product + mixed == PhaseMigrationCatalog.All.Count,
            "Counts tuple must equal total catalog size.");
        // Smoke-check expected ratios: most migrations touch product tables
        // (work_items / workstreams / story_spawns are the hottest tables).
        Assert.True(product >= control,
            $"Expected >= as many Product migrations as Control. Got Control={control}, Product={product}, Mixed={mixed}.");
    }

    private static int? ParsePhaseNumber(string typeName)
    {
        // Expect "Phase{N}Migration" e.g., "Phase42Migration" → 42.
        const string prefix = "Phase";
        const string suffix = "Migration";
        if (!typeName.StartsWith(prefix, StringComparison.Ordinal)
            || !typeName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        var digits = typeName.Substring(prefix.Length, typeName.Length - prefix.Length - suffix.Length);
        return int.TryParse(digits, out var n) ? n : null;
    }
}
