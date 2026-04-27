// -----------------------------------------------------------------------
// <copyright file="StateEndpointsSmokeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Smoke tests for the aggregated GET /api/state endpoint.
/// Asserts the seeded counts match the prototype's sample data exactly.
/// </summary>
public class StateEndpointsSmokeTests : IClassFixture<ForgeWebApplicationFactory>
{
    private readonly ForgeWebApplicationFactory factory;

    public StateEndpointsSmokeTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetState_Returns200()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/state");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Status={resp.StatusCode} Body={body}");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetState_HasExpectedCounts()
    {
        using var client = this.factory.CreateClient();
        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/state")).Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(5,  root.GetProperty("epics").GetArrayLength());
        Assert.Equal(23, root.GetProperty("cards").GetArrayLength());
        Assert.Equal(5,  root.GetProperty("columns").GetArrayLength());
        Assert.Equal(8,  root.GetProperty("workstreams").GetArrayLength());
        Assert.Equal(8,  root.GetProperty("wsEvents").GetArrayLength());
        Assert.Equal(9,  root.GetProperty("prs").GetArrayLength());
        Assert.Equal(4,  root.GetProperty("wsStats").GetArrayLength());
        Assert.Equal(8,  root.GetProperty("overviewActivity").GetArrayLength());
        Assert.Equal(8,  root.GetProperty("overviewAgents").GetArrayLength());
    }

    [Fact]
    public async Task GetState_FirstEpic_IsMultiTenantAuth()
    {
        using var client = this.factory.CreateClient();
        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/state")).Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("epics")[0];
        Assert.Equal("EPIC-024", first.GetProperty("id").GetString());
        Assert.Equal("Multi-tenant authentication", first.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetState_Health_StaysOk()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}
