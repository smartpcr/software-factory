// -----------------------------------------------------------------------
// <copyright file="Phase80MultiRepoTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 80.H — multi-repo fixture + structural tripwires for the
/// repos-move-to-product refactor. A product owns many repos that live in its
/// own <c>{slug}.db</c>; the control DB keeps only the thin
/// <see cref="RepoRegistryRow"/> index (no <c>repos</c> table). These tests
/// lock that boundary in.
/// </summary>
public class Phase80MultiRepoTests
{
    /// <summary>
    /// 80.H.1 — two repos seeded into one product land in that product's
    /// <c>{slug}.db</c>, get indexed in the control registry (scoped to the
    /// product), set <c>DefaultRepoId</c> to the lowest-sort repo, and stay
    /// isolated from a sibling product's DB.
    /// </summary>
    [Fact]
    public async Task MultiRepo_LandInProductDb_RegistryInControl_AndIsolatedPerProduct()
    {
        using var f = await MultiProductForgeWebApplicationFactory.CreateAsync("alpha", "beta");
        await f.SeedReposAsync("alpha", "alpha-api", "alpha-web");

        // 1. Both repos live in alpha.db (the product DB).
        using (f.CreateProductScope("alpha", out var alphaDb))
        {
            var repos = await alphaDb.Repos.AsNoTracking()
                .Where(r => r.ProductId == "p-alpha")
                .OrderBy(r => r.Sort)
                .Select(r => r.Id)
                .ToListAsync();
            Assert.Equal(new[] { "alpha-api", "alpha-web" }, repos.ToArray());
        }

        // 2. beta.db carries none of alpha's repos (per-product isolation).
        using (f.CreateProductScope("beta", out var betaDb))
        {
            var betaRepos = await betaDb.Repos.AsNoTracking()
                .Where(r => r.Id == "alpha-api" || r.Id == "alpha-web")
                .CountAsync();
            Assert.Equal(0, betaRepos);
        }

        // 3. Control registry indexes both, scoped to alpha; default = lowest sort.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var reg = await control.RepoRegistry.AsNoTracking()
                .Where(r => r.ProductSlug == "alpha" && r.DeletedAt == null)
                .OrderBy(r => r.Sort)
                .Select(r => r.Id)
                .ToListAsync();
            Assert.Equal(new[] { "alpha-api", "alpha-web" }, reg.ToArray());

            var product = await control.Products.AsNoTracking().FirstAsync(p => p.Slug == "alpha");
            Assert.Equal("alpha-api", product.DefaultRepoId);
        }
    }

    /// <summary>
    /// 80.H.2 (structural tripwire) — the control DB must NOT map the full
    /// <see cref="RepoRow"/> (table gone); it keeps the thin
    /// <see cref="RepoRegistryRow"/> with registry-only uniqueness. The product
    /// DB owns <see cref="RepoRow"/> (<c>repos</c>). A compile of
    /// <c>control.Repos</c> already fails (no DbSet); this guards the model.
    /// </summary>
    [Fact]
    public void Boundary_ControlHasRegistryOnly_ProductHasRepos()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot to build the EF models */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var product = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Control: no RepoRow entity, no table named "repos".
        Assert.Null(control.Model.FindEntityType(typeof(RepoRow)));
        Assert.DoesNotContain(
            control.Model.GetEntityTypes(),
            t => string.Equals(t.GetTableName(), "repos", StringComparison.OrdinalIgnoreCase));

        // Control: has the thin registry mapped to repo_registry.
        var reg = control.Model.FindEntityType(typeof(RepoRegistryRow));
        Assert.NotNull(reg);
        Assert.Equal("repo_registry", reg!.GetTableName());

        // Registry-only uniqueness: unique indexes cover RemoteUrl + CloneRoot.
        var uniqueProps = reg.GetIndexes()
            .Where(i => i.IsUnique)
            .SelectMany(i => i.Properties.Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("RemoteUrl", uniqueProps);
        Assert.Contains("CloneRoot", uniqueProps);

        // Product: owns the full RepoRow mapped to repos.
        var repoEntity = product.Model.FindEntityType(typeof(RepoRow));
        Assert.NotNull(repoEntity);
        Assert.Equal("repos", repoEntity!.GetTableName());
    }

    /// <summary>
    /// 80.H.2 (backfill tripwire) — once a product has a repo, the registry
    /// reconcile backfills every null <c>RepoId</c> on the product's tables to
    /// the default repo, leaving zero nulls.
    /// </summary>
    [Fact]
    public async Task Backfill_LeavesZeroNullRepoId_ForProductWithRepos()
    {
        using var f = await MultiProductForgeWebApplicationFactory.CreateAsync("alpha", "beta");
        await f.SeedReposAsync("alpha", "alpha-api");

        // Seed a work item with NO repo into alpha.db (predates repo binding).
        using (f.CreateProductScope("alpha", out var alphaDb))
        {
            alphaDb.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-ALPHA-1",
                Provider = "ado",
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "repo-less work item",
                Priority = "med",
                RepoId = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await alphaDb.SaveChangesAsync();
        }

        // Re-reconcile: the null RepoId must be backfilled to the default repo.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var factory = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
            await RepoRegistrySync.ReconcileAsync(control, factory);
        }

        using (f.CreateProductScope("alpha", out var alphaDb))
        {
            var nullCount = await alphaDb.WorkItems.AsNoTracking()
                .Where(w => w.RepoId == null)
                .CountAsync();
            Assert.Equal(0, nullCount);

            var wi = await alphaDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "WI-ALPHA-1");
            Assert.Equal("alpha-api", wi.RepoId);
        }
    }
}
