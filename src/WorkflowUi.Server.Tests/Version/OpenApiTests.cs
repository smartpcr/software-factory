// -----------------------------------------------------------------------
// <copyright file="OpenApiTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Version;

using System.Net;
using System.Text.Json;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

public class OpenApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client;

    public OpenApiTests(TestWebApplicationFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task SwaggerJson_Returns200()
    {
        var response = await this.client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerJson_ContainsApiVersionEndpoint()
    {
        var response = await this.client.GetAsync("/swagger/v1/swagger.json");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("paths", out var paths));
        Assert.True(paths.TryGetProperty("/api/version", out _));
    }

    [Fact]
    public async Task SwaggerJson_HasInfoTitle()
    {
        var response = await this.client.GetAsync("/swagger/v1/swagger.json");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("info", out var info));
        Assert.Equal("Workflow UI API", info.GetProperty("title").GetString());
    }
}
