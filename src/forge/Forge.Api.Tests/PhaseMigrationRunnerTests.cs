// -----------------------------------------------------------------------
// <copyright file="PhaseMigrationRunnerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
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
        // 79 (docs/forge/e2e.md Stage 1.1) adds workstreams.Category — same
        // constraint: Phase 38's EF projection on workstreams references
        // WorkstreamRow.Category so the column must exist before the read.
        int p38 = order.IndexOf(38);
        foreach (var dep in new[] { 43, 45, 47, 49, 50, 79 })
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

    [Fact]
    public async Task Runner_RebootAfterSplit_DoesNotCrashOnMixedMigrations()
    {
        // Regression for: 2026-05-17 server crash at boot with
        // "no such table: forge_actions" thrown from Phase8Migration via
        // PhaseMigrationRunner's fan-out loop. Root cause: Mixed migrations
        // (which touch BOTH control + product tables, interleaved) were
        // being fanned out to per-product DBs, where the control-side
        // references (e.g. forge_actions, products) don't exist.
        //
        // The factory creates two products and runs split-apply, then we
        // simulate a reboot by booting Forge.Api AGAIN against the same
        // folder. The second boot finds the per-product DBs already on
        // disk and exercises the fan-out path against post-split state —
        // which is when the regression triggered in production.
        using var f1 = await MultiProductForgeWebApplicationFactory.CreateAsync("zeta", "eta");
        var folder = f1.Folder;
        f1.Dispose();

        // The per-product DBs persist in the folder even after Dispose
        // until the temp folder is GC'd. Re-open a fresh factory in the
        // SAME folder so PhaseMigrationRunner sees the {slug}.db files
        // on disk and runs its fan-out loop.
        Assert.True(File.Exists(Path.Combine(folder, "zeta.db")),
            "Per-product DB should still exist after factory Dispose for this regression test.");
        Assert.True(File.Exists(Path.Combine(folder, "eta.db")),
            "Per-product DB should still exist after factory Dispose for this regression test.");

        // Boot a fresh factory pointed at the same folder. If Mixed
        // migrations are being fanned out, this crashes on Phase 8's
        // forge_actions EnsureColumn call (control table absent from
        // zeta.db / eta.db).
        var f2 = new RebootInPlaceFactory(folder);
        try
        {
            using var client = f2.CreateClient();
            Assert.NotNull(client);
        }
        finally
        {
            f2.Dispose();
        }
    }

    /// <summary>
    /// Test factory variant that points <c>Forge:Db:Folder</c> at a
    /// pre-existing folder (containing per-product DBs from a prior split)
    /// instead of allocating a fresh GUID folder. Used by the
    /// reboot-after-split regression test.
    /// </summary>
    private sealed class RebootInPlaceFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string folder;

        public RebootInPlaceFactory(string folder) { this.folder = folder; }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("FORGE_TEST_GUARDRAIL", "1");
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Forge:Db:Folder"] = this.folder,
                    ["Forge:Db:Path"] = null,
                    ["Forge:Db:SplitEnabled"] = "true",
                    ["Forge:Repo:AutoClone"] = "false",
                    ["Forge:Background:Disabled"] = "true",
                    ["DetailedErrors"] = "true",
                    ["Logging:LogLevel:Default"] = "Error",
                });
            });
        }
    }
}
