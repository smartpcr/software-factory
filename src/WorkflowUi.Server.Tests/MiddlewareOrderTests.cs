// -----------------------------------------------------------------------
// <copyright file="MiddlewareOrderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using System.Text.Json;
using WorkflowUi.Server.Hosting;
using Xunit;

/// <summary>
/// Integration tests that assert middleware ordering.
/// </summary>
public class MiddlewareOrderTests : IClassFixture<ThrowingEndpointFactory>
{
    private readonly ThrowingEndpointFactory factory;

    public MiddlewareOrderTests(ThrowingEndpointFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Correlation_Runs_Before_ProblemDetails()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");

        // The correlation ID header must exist
        Assert.True(resp.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        var headerId = resp.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();

        // The problem-details body must contain the same correlation ID
        var body = await resp.Content.ReadAsStringAsync();
        var bodyId = JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString();
        Assert.Equal(headerId, bodyId);
    }
}
