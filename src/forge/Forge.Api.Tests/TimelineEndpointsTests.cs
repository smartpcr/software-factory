// -----------------------------------------------------------------------
// <copyright file="TimelineEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Phase 2.10 — contract tests for GET /api/timeline.
/// </summary>
public class TimelineEndpointsTests : IClassFixture<ForgeWebApplicationFactory>
{
    private readonly ForgeWebApplicationFactory factory;

    public TimelineEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetTimeline_Default_Window_Returns_All_Bars()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/timeline");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(-10, root.GetProperty("from").GetDouble());
        Assert.Equal(5, root.GetProperty("to").GetDouble());
        // Default window covers all 8 seeded workstreams.
        Assert.Equal(8, root.GetProperty("bars").GetArrayLength());
        Assert.Equal(8, root.GetProperty("markers").GetArrayLength());
    }

    [Fact]
    public async Task GetTimeline_Narrow_Window_Excludes_Out_Of_Range_Bars()
    {
        using var client = this.factory.CreateClient();
        // Window -5..-3 keeps only bars that overlap (4 of 8 in the seed):
        // ws-oidc (-3..1.5), ws-idp-failover (-8..0.2), ws-dlq (-7..0),
        // ws-bits-cleanup (-6..-0.5).
        var resp = await client.GetAsync("/api/timeline?from=-5&to=-3");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var bars = doc.RootElement.GetProperty("bars");
        Assert.Equal(4, bars.GetArrayLength());
    }

    [Fact]
    public async Task GetTimeline_Bar_Has_Expected_Shape()
    {
        using var client = this.factory.CreateClient();
        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/timeline")).Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("bars")[0];

        Assert.True(first.TryGetProperty("workstreamId", out _));
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("branch", out _));
        Assert.True(first.TryGetProperty("epicColor", out _));
        Assert.True(first.TryGetProperty("status", out _));
        Assert.True(first.TryGetProperty("barStart", out _));
        Assert.True(first.TryGetProperty("barEnd", out _));
        Assert.True(first.TryGetProperty("planned", out _));
        Assert.True(first.TryGetProperty("agents", out _));
        Assert.True(first.TryGetProperty("ahead", out _));
        Assert.True(first.TryGetProperty("behind", out _));
        Assert.True(first.TryGetProperty("conflicts", out _));
    }

    [Fact]
    public async Task GetTimeline_Invalid_Range_Returns_400()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/timeline?from=5&to=-5");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
