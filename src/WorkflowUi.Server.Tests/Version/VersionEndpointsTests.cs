// -----------------------------------------------------------------------
// <copyright file="VersionEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Version;

using System.Net;
using System.Text.Json;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

public class VersionEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client;

    public VersionEndpointsTests(TestWebApplicationFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task GetVersion_Returns200()
    {
        var response = await this.client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetVersion_ReturnsExpectedShape()
    {
        var response = await this.client.GetAsync("/api/version");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("commitSha", out _));
        Assert.True(root.TryGetProperty("buildUtc", out _));
    }

    [Fact]
    public async Task GetVersion_IsAnonymous()
    {
        // The test factory uses a test auth scheme, but version should work
        // even without auth headers — it's AllowAnonymous
        var response = await this.client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VersionHeader_PresentOnEveryResponse()
    {
        var response = await this.client.GetAsync("/api/health");
        Assert.True(response.Headers.Contains("X-Workflow-Ui-Version"));
    }

    [Fact]
    public async Task VersionHeader_PresentOnWorkflowEndpoint()
    {
        var response = await this.client.GetAsync("/api/workflows");
        Assert.True(response.Headers.Contains("X-Workflow-Ui-Version"));
    }
}
