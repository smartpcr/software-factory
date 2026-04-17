// -----------------------------------------------------------------------
// <copyright file="RequestLoggingEnricherTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for RequestLoggingEnricher structured logging.
/// </summary>
public class RequestLoggingEnricherTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public RequestLoggingEnricherTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LogScope_IncludesCorrelationId()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-log-scope-corr");
        await client.GetAsync("/api/health");

        // Find log entries from RequestLoggingEnricher
        var enricherLogs = this.factory.LogCapture.Entries
            .Where(e => e.CategoryName.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLogs);

        // At least one entry should have correlationId in its scope
        var hasCorrelationId = enricherLogs.Any(e =>
            e.Scope.Any(kv => kv.Key == "correlationId" && kv.Value?.ToString() == "test-log-scope-corr"));

        Assert.True(hasCorrelationId, "Expected correlationId in log scope");
    }

    [Fact]
    public async Task LogScope_IncludesMethodAndPath()
    {
        using var client = this.factory.CreateClient();
        await client.GetAsync("/api/health");

        var enricherLogs = this.factory.LogCapture.Entries
            .Where(e => e.CategoryName.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLogs);

        var hasMethod = enricherLogs.Any(e =>
            e.Scope.Any(kv => kv.Key == "method" && kv.Value?.ToString() == "GET"));
        var hasPath = enricherLogs.Any(e =>
            e.Scope.Any(kv => kv.Key == "path" && kv.Value?.ToString() == "/api/health"));

        Assert.True(hasMethod, "Expected 'method' in log scope");
        Assert.True(hasPath, "Expected 'path' in log scope");
    }

    [Fact]
    public async Task LogOutput_IncludesElapsedMs()
    {
        using var client = this.factory.CreateClient();
        await client.GetAsync("/api/health");

        var enricherLogs = this.factory.LogCapture.Entries
            .Where(e => e.CategoryName.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLogs);

        var hasElapsed = enricherLogs.Any(e => e.Message.Contains("ms"));
        Assert.True(hasElapsed, "Expected elapsed time in log message");
    }

    [Fact]
    public async Task LogState_IncludesStatusCode()
    {
        using var client = this.factory.CreateClient();
        await client.GetAsync("/api/health");

        var enricherLogs = this.factory.LogCapture.Entries
            .Where(e => e.CategoryName.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLogs);

        var hasStatusCode = enricherLogs.Any(e =>
            e.State.Any(kv => kv.Key == "StatusCode" && Convert.ToInt32(kv.Value) == 200));

        Assert.True(hasStatusCode, "Expected 'StatusCode' as structured field in log state");
    }

    [Fact]
    public async Task LogState_IncludesElapsedMs()
    {
        using var client = this.factory.CreateClient();
        await client.GetAsync("/api/health");

        var enricherLogs = this.factory.LogCapture.Entries
            .Where(e => e.CategoryName.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLogs);

        var hasElapsedMs = enricherLogs.Any(e =>
            e.State.Any(kv => kv.Key == "ElapsedMs" && kv.Value is long));

        Assert.True(hasElapsedMs, "Expected 'ElapsedMs' as structured field in log state");
    }
}
