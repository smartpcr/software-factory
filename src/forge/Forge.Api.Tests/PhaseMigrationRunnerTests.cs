// -----------------------------------------------------------------------
// <copyright file="PhaseMigrationRunnerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase G.4 follow-up — smoke tests for <see cref="PhaseMigrationRunner"/>.
/// Confirms (1) every catalogued phase appears in the execution order, (2)
/// the runner boots successfully against a fresh per-test DB (via the
/// standard factory's automatic invocation in Program.cs).
/// </summary>
public class PhaseMigrationRunnerTests
{
    [Fact]
    public void ExecutionOrder_ContainsEveryCatalogPhase()
    {
        var orderSet = PhaseMigrationRunner.ExecutionOrder.ToHashSet();
        var catalogSet = PhaseMigrationCatalog.All.Select(s => s.PhaseNumber).ToHashSet();

        var missing = catalogSet.Except(orderSet).OrderBy(p => p).ToList();
        var extra = orderSet.Except(catalogSet).OrderBy(p => p).ToList();

        Assert.True(missing.Count == 0,
            $"Phases present in PhaseMigrationCatalog but missing from " +
            $"PhaseMigrationRunner.ExecutionOrder: [{string.Join(", ", missing)}]. " +
            $"Add them to the runner's execution order (preserving dependency order).");
        Assert.True(extra.Count == 0,
            $"Phases referenced by PhaseMigrationRunner.ExecutionOrder but missing " +
            $"from PhaseMigrationCatalog: [{string.Join(", ", extra)}].");
    }

    [Fact]
    public void ExecutionOrder_AllPhasesUnique()
    {
        // Phase 43 used to be invoked twice in the historical Program.cs sequence.
        // The runner's ExecutionOrder dedups to one entry per phase; verify.
        var dupes = PhaseMigrationRunner.ExecutionOrder
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(dupes.Count == 0,
            $"PhaseMigrationRunner.ExecutionOrder has duplicates: [{string.Join(", ", dupes)}]. " +
            $"Each phase migration is idempotent so duplicate entries waste boot time.");
    }

    [Fact]
    public void ExecutionOrder_HonoursPhase42Dependency()
    {
        // Phase 42 must run before Phase 38 — Phase 38's EF query against
        // workstreams projects every mapped column, including PlanAnchor /
        // PlanContentHash which Phase 42 adds.
        var p42Index = PhaseMigrationRunner.ExecutionOrder.ToList().IndexOf(42);
        var p38Index = PhaseMigrationRunner.ExecutionOrder.ToList().IndexOf(38);
        Assert.True(p42Index >= 0, "Phase 42 missing from ExecutionOrder.");
        Assert.True(p38Index >= 0, "Phase 38 missing from ExecutionOrder.");
        Assert.True(p42Index < p38Index,
            $"Phase 42 (index {p42Index}) MUST run before Phase 38 (index {p38Index}). " +
            $"Phase 38's EF projection requires the columns Phase 42 adds.");
    }

    [Fact]
    public void ExecutionOrder_HonoursPlanDocColumnDependencies()
    {
        var order = PhaseMigrationRunner.ExecutionOrder.ToList();
        // 43/45/47/49/50 add columns on story_spawns or workstreams that
        // Phase 38's EF read depends on.
        int p38 = order.IndexOf(38);
        foreach (var dep in new[] { 43, 45, 47, 49, 50 })
        {
            int idx = order.IndexOf(dep);
            Assert.True(idx >= 0, $"Phase {dep} missing from ExecutionOrder.");
            Assert.True(idx < p38,
                $"Phase {dep} (index {idx}) MUST run before Phase 38 (index {p38}). " +
                $"Phase 38's EF projection depends on columns Phase {dep} adds.");
        }
    }

    [Fact]
    public async Task Runner_BootsAgainstFreshDb_WithNoExceptions()
    {
        // The standard test factory boots Forge.Api which now invokes the
        // runner. If the runner crashes, factory construction fails — so
        // simply creating the factory + a client exercises every migration.
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Runner_BootsAgainstMultiProductFactory_WithFanout()
    {
        // The multi-product factory creates two products and runs split-apply,
        // physicalising per-product DBs. The runner's fan-out path then
        // invokes Product/Mixed migrations against each {slug}.db. If any
        // migration crashes on a per-product invocation, factory construction
        // throws — so this is a smoke test for the fan-out path.
        using var f = await MultiProductForgeWebApplicationFactory.CreateAsync("alpha", "beta");
        Assert.NotNull(f);
        Assert.Equal(2, f.ProductSlugs.Count);
        // Both per-product DBs exist on disk after the runner's fan-out.
        Assert.True(File.Exists(Path.Combine(f.Folder, "alpha.db")));
        Assert.True(File.Exists(Path.Combine(f.Folder, "beta.db")));
    }
}
