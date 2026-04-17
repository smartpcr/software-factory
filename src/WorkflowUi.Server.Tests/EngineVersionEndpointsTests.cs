// -----------------------------------------------------------------------
// <copyright file="EngineVersionEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Endpoints;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for the GET /api/engine/version endpoint and EngineAssemblyVersionProvider.
/// </summary>
public class EngineVersionEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public EngineVersionEndpointsTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetVersion_Returns200()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/engine/version");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetVersion_BodyHasEngineAssemblyVersion_NonEmpty()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/engine/version");
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var version = doc.RootElement.GetProperty("engineAssemblyVersion").GetString();
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        Assert.True(System.Version.TryParse(version, out _));
    }

    [Fact]
    public void Provider_ReadsFromWorkflowDefinitionAssembly()
    {
        var provider = new EngineAssemblyVersionProvider();
        var info = provider.GetVersion();
        var expected = typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString();
        Assert.Equal(expected, info.EngineAssemblyVersion);
    }

    [Fact]
    public void Provider_IsSingleton_InDi()
    {
        using var scope1 = this.factory.Services.CreateScope();
        using var scope2 = this.factory.Services.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<EngineAssemblyVersionProvider>();
        var b = scope2.ServiceProvider.GetRequiredService<EngineAssemblyVersionProvider>();
        Assert.Same(a, b);
    }

    [Fact]
    public async Task JsonProperty_IsCamelCase()
    {
        using var client = this.factory.CreateClient();
        var body = await (await client.GetAsync("/api/engine/version")).Content.ReadAsStringAsync();
        Assert.Contains("engineAssemblyVersion", body);
        Assert.DoesNotContain("EngineAssemblyVersion", body);
    }

    [Fact]
    public async Task TwoSequentialGets_ReturnSameVersion()
    {
        using var client = this.factory.CreateClient();
        var body1 = await (await client.GetAsync("/api/engine/version")).Content.ReadAsStringAsync();
        var body2 = await (await client.GetAsync("/api/engine/version")).Content.ReadAsStringAsync();
        var v1 = JsonDocument.Parse(body1).RootElement.GetProperty("engineAssemblyVersion").GetString();
        var v2 = JsonDocument.Parse(body2).RootElement.GetProperty("engineAssemblyVersion").GetString();
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void Program_PartialClass_SentinelExists()
    {
        var programType = typeof(Program);
        Assert.True(programType.IsPublic || programType.IsNestedPublic || !programType.IsAbstract);
    }
}
