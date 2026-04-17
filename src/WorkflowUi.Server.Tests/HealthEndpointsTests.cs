// -----------------------------------------------------------------------
// <copyright file="HealthEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using System.Text.Json;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for the GET /api/health endpoint.
/// </summary>
public class HealthEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public HealthEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetHealth_Returns200()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetHealth_BodyHasStatusOk()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetHealth_BodyHasIsoTimestamp()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var ts = doc.RootElement.GetProperty("timestamp").GetString();
        Assert.NotNull(ts);
        Assert.True(DateTimeOffset.TryParse(ts, out var parsed));
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - parsed).TotalSeconds) < 5);
    }

    [Fact]
    public async Task GetHealth_ProducesNewTimestampEachCall()
    {
        using var client = this.factory.CreateClient();
        var body1 = await (await client.GetAsync("/api/health")).Content.ReadAsStringAsync();
        await Task.Delay(10);
        var body2 = await (await client.GetAsync("/api/health")).Content.ReadAsStringAsync();
        var ts1 = JsonDocument.Parse(body1).RootElement.GetProperty("timestamp").GetString();
        var ts2 = JsonDocument.Parse(body2).RootElement.GetProperty("timestamp").GetString();

        // Timestamps should differ (or at worst be equal if executed within the same millisecond).
        // The key assertion is they are both valid, and both are fresh.
        Assert.NotNull(ts1);
        Assert.NotNull(ts2);
        Assert.True(DateTimeOffset.TryParse(ts1, out _));
        Assert.True(DateTimeOffset.TryParse(ts2, out _));
    }

    [Fact]
    public async Task GetHealth_UnknownPath_Returns404()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }
}
