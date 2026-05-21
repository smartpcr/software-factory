// -----------------------------------------------------------------------
// <copyright file="MultiProductForgeWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Phase G.3 — test factory that seeds 2+ products and physicalises a
/// per-product DB for each so fan-out workers exercise true multi-DB
/// semantics in tests.
///
/// <para>
/// Without this factory, every test runs against a single control DB
/// containing the union schema (Phase D interim behaviour). That single
/// DB satisfies most tests but cannot detect mis-routed reads/writes
/// where a fanout callback accidentally captures the outer scope's
/// <c>ControlDbContext</c> instead of resolving a fresh
/// <c>ProductDbContext</c> per slug.
/// </para>
/// <para>
/// Usage:
/// <code>
/// using var f = await MultiProductForgeWebApplicationFactory.CreateAsync(
///     "primary", "secondary");
/// // f.Folder contains forge.db + primary.db + secondary.db
/// using var client = f.CreateClient();
/// // Hit /api/... with X-Forge-Product-Id: primary or secondary
/// </code>
/// </para>
/// <para>
/// Important: this factory uses <see cref="SplitApplyService"/> to copy
/// the seeded product-side data out of <c>forge.db</c> and into per-product
/// files. The split runs once after boot. Tests that mutate state should
/// seed it via the HTTP API or against the appropriate per-product
/// context resolved through <see cref="IProductDbContextFactory.CreateFor"/>.
/// </para>
/// </summary>
public sealed class MultiProductForgeWebApplicationFactory : ForgeWebApplicationFactory
{
    /// <summary>Slugs of products seeded into this factory.</summary>
    public IReadOnlyList<string> ProductSlugs { get; }

    private MultiProductForgeWebApplicationFactory(IReadOnlyList<string> slugs)
        : base(new Dictionary<string, string?>
        {
            // Split must be enabled or POST /api/admin/db/split-apply is a 403.
            ["Forge:Db:SplitEnabled"] = "true",
        })
    {
        this.ProductSlugs = slugs;
    }

    /// <summary>
    /// Create a factory pre-seeded with the given product slugs and with
    /// per-product DBs physicalised via <see cref="SplitApplyService"/>.
    /// At least 2 slugs are required (single-product setups should use the
    /// base <see cref="ForgeWebApplicationFactory"/>).
    /// </summary>
    /// <param name="productSlugs">Lowercase, hyphenated slugs. Each becomes
    /// a <c>{slug}.db</c> file inside <see cref="ForgeWebApplicationFactory.Folder"/>.</param>
    public static async Task<MultiProductForgeWebApplicationFactory> CreateAsync(params string[] productSlugs)
    {
        if (productSlugs is null || productSlugs.Length < 2)
        {
            throw new ArgumentException(
                "MultiProductForgeWebApplicationFactory requires at least 2 product slugs; " +
                "use the base ForgeWebApplicationFactory for single-product tests.",
                nameof(productSlugs));
        }
        foreach (var slug in productSlugs)
        {
            if (!ForgeDbPaths.TryValidateSlug(slug, out var error))
            {
                throw new ArgumentException(
                    $"Invalid product slug '{slug}': {error}", nameof(productSlugs));
            }
        }

        var f = new MultiProductForgeWebApplicationFactory(productSlugs);

        // Trigger boot + base migrations (creates forge.db schema +
        // canonical crews/agents seed).
        using (f.CreateClient()) { /* boot */ }

        // Seed each requested product into the control DB. The default
        // "atlas" product is created by ForgeSeed on boot; additional
        // products go in here so the per-product DBs have a stable
        // ProductId to partition by.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            foreach (var slug in productSlugs)
            {
                if (await control.Products.AnyAsync(p => p.Slug == slug)) continue;
                control.Products.Add(new ProductRow
                {
                    Id = $"p-{slug}",
                    Slug = slug,
                    Name = slug,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await control.SaveChangesAsync();
        }

        // Physicalise per-product DBs. SplitApplyService moves every
        // product-table row matching each product's ProductId from
        // forge.db into {slug}.db. After this, ProductDbContextFactory
        // opens {slug}.db for any request scoped to that product.
        using (var scope = f.Services.CreateScope())
        {
            var apply = scope.ServiceProvider.GetRequiredService<SplitApplyService>();
            await apply.ApplyAsync(CancellationToken.None);

            // Phase 80.B — the splitter classifies `repos` as a Control table
            // (DbBoundary) and leaves it in forge.db; a dedicated, marker-gated
            // move relocates it into each {slug}.db. The live boot runs this
            // right after the split, so the factory must too — otherwise no
            // {slug}.db has a `repos` table.
            var movePaths = scope.ServiceProvider.GetRequiredService<IForgeDbPaths>();
            await new RepoMoveToProductService(movePaths).ApplyAsync(CancellationToken.None);
        }

        // The process-wide SplitMarkerCache was probed (and cached `false`)
        // during boot — before the split ran. ProductDbContextFactory.CreateFor
        // gates per-product routing on that marker, so without this reset every
        // post-split CreateFor would fall back to forge.db and all products
        // would share one DB. Invalidate so the now-stamped marker is observed.
        SplitMarkerCache.ResetForTesting();

        return f;
    }

    /// <summary>
    /// Create a <see cref="ProductDbContext"/> bound to a specific product
    /// for direct DB seeding / inspection in tests. The caller owns the
    /// returned context's lifetime via the scope.
    /// </summary>
    public IServiceScope CreateProductScope(string productSlug, out ProductDbContext db)
    {
        var scope = this.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
        db = factory.CreateFor(productSlug);
        return scope;
    }

    /// <summary>
    /// Phase 80.H — the multi-<b>repo</b> fixture. Seeds <paramref name="repoIds"/>
    /// into the given product's <c>{slug}.db</c> (where <c>repos</c> lives post-split)
    /// and reconciles the control-side <see cref="RepoRegistryRow"/> index so the
    /// repos are indexed, <see cref="ProductRow.DefaultRepoId"/> is set to the
    /// lowest-sort repo, and product-table <c>RepoId</c> nulls are backfilled.
    /// Exercises the true multi-repo shape: one product owning several repos in
    /// its own DB. Returns the seeded repo ids in sort order.
    /// </summary>
    public async Task<IReadOnlyList<string>> SeedReposAsync(string productSlug, params string[] repoIds)
    {
        if (repoIds is null || repoIds.Length == 0)
        {
            throw new ArgumentException("At least one repo id is required.", nameof(repoIds));
        }

        using var scope = this.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();

        var product = await control.Products.AsNoTracking().FirstAsync(p => p.Slug == productSlug);
        var db = factory.CreateFor(productSlug);

        var sort = 0;
        foreach (var id in repoIds)
        {
            if (await db.Repos.AnyAsync(r => r.Id == id)) { sort++; continue; }
            db.Repos.Add(new RepoRow
            {
                Id = id,
                Sort = sort++,
                ProductId = product.Id,
                Kind = "ado",
                RemoteUrl = $"https://dev.azure.com/{productSlug}/{id}/_git/{id}",
                DisplayName = id,
                DefaultBranch = "main",
                CloneRoot = $@"C:\forge\{productSlug}\{id}",
            });
        }
        await db.SaveChangesAsync();

        // Index the new repos into the control registry + set DefaultRepoId.
        await RepoRegistrySync.ReconcileAsync(control, factory);
        return repoIds.ToList();
    }
}
