// -----------------------------------------------------------------------
// <copyright file="CorrelationIdTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using System.Net.Http.Headers;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for CorrelationIdMiddleware behavior.
/// </summary>
public class CorrelationIdTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public CorrelationIdTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task MissingHeader_ServerGeneratesGuid()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.True(resp.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        var id = resp.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(32, id.Length); // Guid.ToString("N") produces 32 hex chars
    }

    [Fact]
    public async Task ProvidedHeader_IsPreserved_AndEchoed()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "test-corr-1");
        var resp = await client.GetAsync("/api/health");
        var id = resp.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal("test-corr-1", id);
    }

    [Fact]
    public async Task ResponseAlways_HasHeader()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.True(resp.Headers.Contains(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task InvalidHeader_TooLong_Rejected_AndServerRegenerates()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, new string('x', 200));
        var resp = await client.GetAsync("/api/health");
        var id = resp.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(32, id.Length);
    }

    [Fact]
    public async Task ParallelRequests_ProduceDistinctIds()
    {
        using var client = this.factory.CreateClient();
        var tasks = Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/health"));
        var responses = await Task.WhenAll(tasks);
        var ids = responses
            .Select(r => r.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single())
            .ToHashSet();
        Assert.Equal(10, ids.Count);
    }
}
