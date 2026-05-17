// -----------------------------------------------------------------------
// <copyright file="MultiProductForgeWebApplicationFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using Forge.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase G.3 — smoke tests for the multi-product test factory. These
/// confirm that fan-out fixtures can seed 2+ products with physicalised
/// per-product DBs in the per-test folder.
/// </summary>
public class MultiProductForgeWebApplicationFactoryTests
{
    [Fact]
    public async Task CreateAsync_SeedsTwoProducts_AndPhysicalisesPerProductDbs()
    {
        using var f = await MultiProductForgeWebApplicationFactory.CreateAsync("alpha", "beta");

        // Both per-product DBs must exist in the per-test folder.
        var alphaDb = Path.Combine(f.Folder, "alpha.db");
        var betaDb = Path.Combine(f.Folder, "beta.db");
        Assert.True(File.Exists(alphaDb), $"alpha.db missing at '{alphaDb}'.");
        Assert.True(File.Exists(betaDb), $"beta.db missing at '{betaDb}'.");
        Assert.Equal(new[] { "alpha", "beta" }, f.ProductSlugs.ToArray());
    }

    [Fact]
    public async Task CreateAsync_RejectsSingleProduct()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => MultiProductForgeWebApplicationFactory.CreateAsync("only-one"));
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidSlug()
    {
        // Forge slug must be [a-z0-9-]; "Bad Slug!" is rejected.
        await Assert.ThrowsAsync<ArgumentException>(
            () => MultiProductForgeWebApplicationFactory.CreateAsync("good-slug", "Bad Slug!"));
    }

    [Fact]
    public async Task CreateProductScope_ReturnsProductDbContext_BoundToSlug()
    {
        using var f = await MultiProductForgeWebApplicationFactory.CreateAsync("primary", "secondary");

        using var scope = f.CreateProductScope("primary", out var db);
        Assert.NotNull(db);
        Assert.Equal("primary", db.ProductSlug);
    }
}
