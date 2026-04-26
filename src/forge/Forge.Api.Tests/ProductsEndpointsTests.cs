// -----------------------------------------------------------------------
// <copyright file="ProductsEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Phase 8.12 — contract tests for /api/products and /api/repos.
/// Each method gets its own factory so the SQLite DB is isolated and
/// 409 / 422 paths don't see leftover state from earlier asserts.
/// </summary>
public class ProductsEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListProducts_Returns_Backfilled_Atlas()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = new List<string>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            ids.Add(p.GetProperty("id").GetString()!);
        }
        Assert.Contains("atlas", ids);
    }

    [Fact]
    public async Task GetAtlasProduct_Has_Expected_Shape()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/products/atlas");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("atlas", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Atlas", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("atlas", doc.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task ListProductRepos_Returns_Backfilled_Atlas_Platform()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/products/atlas/repos");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Contains(arr, r => r.GetProperty("id").GetString() == "atlas-platform");
    }

    [Fact]
    public async Task CreateProduct_Without_User_Header_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/products", new { id = "newp", name = "New Product" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateProduct_With_Auth_Returns_201()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/products",
            new { id = "kepler", name = "Kepler", description = "satellite imagery pipeline" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("kepler", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("kepler", doc.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task CreateProduct_Duplicate_Id_Returns_409()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // atlas is seeded.
        var resp = await client.PostAsJsonAsync(
            "/api/products",
            new { id = "atlas", name = "Atlas Two" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task AttachRepo_With_Bad_Kind_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/products/atlas/repos",
            new
            {
                id = "bad-kind",
                kind = "gitlab",
                remoteUrl = "https://gitlab.com/x/y.git",
                displayName = "x/y",
                cloneRoot = @"C:\forge\bad-kind",
            },
            JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AttachRepo_With_Mismatched_Host_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/products/atlas/repos",
            new
            {
                id = "wrong-host",
                kind = "ado",
                remoteUrl = "https://github.com/x/y.git",
                displayName = "x/y",
                cloneRoot = @"C:\forge\wrong-host",
            },
            JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AttachRepo_Valid_Returns_201()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/products/atlas/repos",
            new
            {
                id = "tenant-svc",
                kind = "ado",
                remoteUrl = "https://dev.azure.com/atlas/atlas-platform/_git/tenant-svc",
                displayName = "tenant-svc",
                cloneRoot = @"C:\forge\atlas\tenant-svc",
                defaultBranch = "main",
            },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task AttachRepo_Duplicate_RemoteUrl_Returns_409()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // First attach.
        var first = await client.PostAsJsonAsync(
            "/api/products/atlas/repos",
            new
            {
                id = "dupe-1",
                kind = "github",
                remoteUrl = "https://github.com/atlas/dupe.git",
                displayName = "atlas/dupe",
                cloneRoot = @"C:\forge\dupe1",
            },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Second attach with the same remoteUrl into the same product.
        var second = await client.PostAsJsonAsync(
            "/api/products/atlas/repos",
            new
            {
                id = "dupe-2",
                kind = "github",
                remoteUrl = "https://github.com/atlas/dupe.git",
                displayName = "atlas/dupe again",
                cloneRoot = @"C:\forge\dupe2",
            },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("already attached", body);
    }

    [Fact]
    public async Task ProductScope_Header_Filters_Workstreams()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        // The seeded workstreams are all backfilled to product 'atlas'.
        // Asking for an unknown product should yield zero workstreams.
        client.DefaultRequestHeaders.Add("X-Forge-Product-Id", "no-such-product");
        var resp = await client.GetAsync("/api/workstreams");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.EnumerateArray());

        // 'all' bypasses the scope.
        client.DefaultRequestHeaders.Remove("X-Forge-Product-Id");
        client.DefaultRequestHeaders.Add("X-Forge-Product-Id", "all");
        var respAll = await client.GetAsync("/api/workstreams");
        Assert.Equal(HttpStatusCode.OK, respAll.StatusCode);
        var docAll = JsonDocument.Parse(await respAll.Content.ReadAsStringAsync());
        Assert.NotEmpty(docAll.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task BackfillRepoId_Pins_Seeded_Workstreams_To_Default_Repo()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        // Default scope = atlas. Seeded workstreams should resolve through the scope.
        var resp = await client.GetAsync("/api/workstreams");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // Every returned workstream should carry repoId = "atlas-platform" after backfill.
        foreach (var ws in doc.RootElement.EnumerateArray())
        {
            Assert.Equal("atlas-platform", ws.GetProperty("repoId").GetString());
        }
    }
}
