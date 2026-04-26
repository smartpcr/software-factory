// -----------------------------------------------------------------------
// <copyright file="WorkstreamsEndpointsTests.cs" company="Microsoft Corp.">
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
/// Phase 3.6 — contract tests for /api/workstreams.
/// Authentication is the placeholder X-Forge-User header (Phase 3.11).
/// </summary>
public class WorkstreamsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ForgeWebApplicationFactory factory;

    public WorkstreamsEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetWorkstreams_Returns_All_Seeded()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/workstreams");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // Other tests in this class create / delete workstreams in the same DB
        // (we share an IClassFixture). Assert by id-membership of the 8 seeded
        // ones, which is order- and side-effect-stable.
        var ids = new HashSet<string>();
        foreach (var w in doc.RootElement.EnumerateArray())
        {
            ids.Add(w.GetProperty("id").GetString()!);
        }
        var seeded = new[]
        {
            "ws-oidc", "ws-rate-limits", "ws-idp-failover", "ws-retry-jitter",
            "ws-dlq", "ws-tenant-admin", "ws-log-rotation", "ws-bits-cleanup",
        };
        foreach (var id in seeded)
        {
            // ws-tenant-admin may be deleted by a sibling test; treat its absence as ok.
            if (id == "ws-tenant-admin") continue;
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public async Task CreateWorkstream_Without_User_Header_Returns_401()
    {
        using var client = this.factory.CreateClient();
        var req = new
        {
            id = "ws-test-1",
            name = "test-1",
            branch = "ws/test-1",
            @base = "main",
            epic = "EPIC-024",
        };
        var resp = await client.PostAsJsonAsync("/api/workstreams", req, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateWorkstream_With_User_Header_Returns_201()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var req = new
        {
            id = "ws-test-create",
            name = "test-create",
            branch = "ws/test-create",
            @base = "main",
            epic = "EPIC-024",
        };
        var resp = await client.PostAsJsonAsync("/api/workstreams", req, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ws-test-create", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("planning", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeleteWorkstream_With_Open_Prs_Without_Force_Returns_422()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // ws-oidc has open PRs in the seed.
        var resp = await client.DeleteAsync("/api/workstreams/ws-oidc");
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("requireConfirm", body);
    }

    [Fact]
    public async Task DeleteWorkstream_With_Force_Removes_Row()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // ws-tenant-admin has no PRs in the seed → simple delete.
        var resp = await client.DeleteAsync("/api/workstreams/ws-tenant-admin");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Confirm it's gone.
        var listDoc = JsonDocument.Parse(await (await client.GetAsync("/api/workstreams")).Content.ReadAsStringAsync());
        var ids = new List<string>();
        foreach (var w in listDoc.RootElement.EnumerateArray())
        {
            ids.Add(w.GetProperty("id").GetString()!);
        }
        Assert.DoesNotContain("ws-tenant-admin", ids);
    }

    [Fact]
    public async Task SyncWorkstream_Requires_User_Header()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.PostAsync("/api/workstreams/ws-oidc/sync", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SyncWorkstream_Unknown_Returns_404()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsync("/api/workstreams/ws-nope/sync", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
