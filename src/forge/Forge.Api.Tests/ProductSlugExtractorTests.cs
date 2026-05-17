// -----------------------------------------------------------------------
// <copyright file="ProductSlugExtractorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 73 Stage 2A — unit tests for <see cref="ProductSlugExtractor"/>.
/// Verifies the convention-based heuristics + DB-lookup fallback used
/// by writer entry points to derive the right product slug.
/// </summary>
public class ProductSlugExtractorTests : IDisposable
{
    public void Dispose()
    {
        if (!string.IsNullOrEmpty(this.testRoot))
        {
            try { Directory.Delete(this.testRoot, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static readonly string[] KnownSlugs =
    {
        "atlas",
        "code-intelligence",
        "failover-cluster",
        "code", // intentionally shadowed by code-intelligence to verify longest-match
    };

    [Theory]
    [InlineData("code-intelligence:AGENT-MEMORY", "code-intelligence")]
    [InlineData("failover-cluster:XRAFT", "failover-cluster")]
    [InlineData("atlas:WI-12345", "atlas")]
    [InlineData("code-intelligence:WI-9", "code-intelligence")]
    public void FromStoryId_ParsesSlugBeforeColon(string storyId, string expected)
    {
        Assert.Equal(expected, ProductSlugExtractor.FromStoryId(storyId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoColon")]
    [InlineData(":NoSlug")]
    [InlineData("trailing:")]
    public void FromStoryId_ReturnsNullForBadInput(string? storyId)
    {
        Assert.Null(ProductSlugExtractor.FromStoryId(storyId));
    }

    [Fact]
    public void FromStoryId_RejectsInvalidSlugCharacters()
    {
        // ForgeDbPaths.TryValidateSlug rejects anything outside [a-z0-9-].
        Assert.Null(ProductSlugExtractor.FromStoryId("Invalid_Slug:STORY"));
        Assert.Null(ProductSlugExtractor.FromStoryId("../evil:STORY"));
    }

    [Theory]
    [InlineData("spawn-code-intelligence-AGENT-MEMORY", "code-intelligence")]
    [InlineData("spawn-failover-cluster-XRAFT", "failover-cluster")]
    [InlineData("spawn-atlas-WI-12345", "atlas")]
    public void FromSpawnId_ParsesSlugAgainstKnownSet(string spawnId, string expected)
    {
        Assert.Equal(expected, ProductSlugExtractor.FromSpawnId(spawnId, KnownSlugs));
    }

    [Fact]
    public void FromSpawnId_PrefersLongestMatch()
    {
        // 'code' and 'code-intelligence' both prefix-match 'code-intelligence-...';
        // longest must win.
        Assert.Equal(
            "code-intelligence",
            ProductSlugExtractor.FromSpawnId("spawn-code-intelligence-AGENT-MEMORY", KnownSlugs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notspawn-x-y")]
    [InlineData("spawn-unknown-product-WI")]
    public void FromSpawnId_ReturnsNullForUnknownOrMalformed(string? spawnId)
    {
        Assert.Null(ProductSlugExtractor.FromSpawnId(spawnId, KnownSlugs));
    }

    [Fact]
    public void FromSpawnId_RequiresKnownSlugList()
    {
        Assert.Null(ProductSlugExtractor.FromSpawnId("spawn-code-intelligence-X", knownSlugs: null));
    }

    [Theory]
    [InlineData("ws-code-intelligence-agent-memory-phase-learning-loop-stage-reranker-trainer", "code-intelligence")]
    [InlineData("ws-failover-cluster-xraft-phase-x-stage-y", "failover-cluster")]
    [InlineData("ws-atlas-something", "atlas")]
    public void FromWorkstreamId_ParsesSlugAgainstKnownSet(string wsId, string expected)
    {
        Assert.Equal(expected, ProductSlugExtractor.FromWorkstreamId(wsId, KnownSlugs));
    }

    [Fact]
    public void FromWorkstreamId_PrefersLongestMatch()
    {
        Assert.Equal(
            "code-intelligence",
            ProductSlugExtractor.FromWorkstreamId("ws-code-intelligence-anything", KnownSlugs));
    }

    [Fact]
    public void FromWorkstreamId_RequiresSeparatorAfterSlug()
    {
        // 'codez' is not 'code' followed by '-' so 'code' should NOT match.
        Assert.Null(ProductSlugExtractor.FromWorkstreamId("ws-codez-something", new[] { "code" }));
    }

    [Theory]
    [InlineData("plan-doc:spawn-code-intelligence-AGENT-MEMORY:architecture.md", "code-intelligence")]
    [InlineData("plan-doc:spawn-failover-cluster-XRAFT:tech-spec.md", "failover-cluster")]
    public void FromPlanDocWorkItemId_ExtractsViaSpawnSegment(string id, string expected)
    {
        Assert.Equal(expected, ProductSlugExtractor.FromPlanDocWorkItemId(id, KnownSlugs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WI-12345")]
    [InlineData("not-plan-doc:x:y")]
    public void FromPlanDocWorkItemId_ReturnsNullForNonPlanDoc(string? id)
    {
        Assert.Null(ProductSlugExtractor.FromPlanDocWorkItemId(id, KnownSlugs));
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToConventions_NoDbLookup()
    {
        // ResolveAsync no longer does a Control DB lookup for raw work
        // item ids (WorkItems lives on ProductDbContext post-split, so
        // the chicken-and-egg is unsolvable from Control alone). For
        // raw IDs that don't match a convention, it returns null and
        // the caller falls through to control DB / fail-loud guard.
        await using var ctx = BuildInMemoryControl(seed: control =>
        {
            control.Products.Add(new ProductRow
            {
                Id = "prod-ci",
                Slug = "code-intelligence",
                Name = "Code Intelligence",
            });
        });

        var slug = await ProductSlugExtractor.ResolveAsync("WI-37204296", ctx);
        Assert.Null(slug);
    }

    [Fact]
    public async Task ResolveAsync_MatchesStoryIdConvention()
    {
        await using var ctx = BuildInMemoryControl();
        var slug = await ProductSlugExtractor.ResolveAsync("code-intelligence:AGENT-MEMORY", ctx);
        Assert.Equal("code-intelligence", slug);
    }

    [Fact]
    public async Task ResolveAsync_MatchesWorkstreamId_AgainstKnownSlugs()
    {
        await using var ctx = BuildInMemoryControl(seed: control =>
        {
            control.Products.AddRange(
                new ProductRow { Id = "prod-ci", Slug = "code-intelligence", Name = "CI" },
                new ProductRow { Id = "prod-fo", Slug = "failover-cluster", Name = "FO" });
        });
        var slug = await ProductSlugExtractor.ResolveAsync(
            "ws-code-intelligence-agent-memory-phase-x-stage-y", ctx);
        Assert.Equal("code-intelligence", slug);
    }

    [Fact]
    public async Task ResolveAsync_NullInput_ReturnsNull()
    {
        await using var ctx = BuildInMemoryControl();
        Assert.Null(await ProductSlugExtractor.ResolveAsync(null, ctx));
        Assert.Null(await ProductSlugExtractor.ResolveAsync("", ctx));
    }

    private string testRoot = string.Empty;

    private ControlDbContext BuildInMemoryControl(Action<ControlDbContext>? seed = null)
    {
        if (string.IsNullOrEmpty(this.testRoot))
        {
            this.testRoot = Path.Combine(Path.GetTempPath(), $"forge-slugtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this.testRoot);
        }
        var dbPath = Path.Combine(this.testRoot, $"control-{Guid.NewGuid():N}.db");
        var opt = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var ctx = new ControlDbContext(opt);
        ctx.Database.EnsureCreated();
        seed?.Invoke(ctx);
        ctx.SaveChanges();
        return ctx;
    }
}
