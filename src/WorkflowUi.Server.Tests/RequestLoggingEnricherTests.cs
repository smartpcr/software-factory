// -----------------------------------------------------------------------
// <copyright file="RequestLoggingEnricherTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Tests for RequestLoggingEnricher structured logging.
/// Uses <see cref="ITestOutputHelper"/> sink for log capture visibility per spec.
/// </summary>
public class RequestLoggingEnricherTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    private readonly ITestOutputHelper output;

    public RequestLoggingEnricherTests(TestWebApplicationFactory factory, ITestOutputHelper output)
    {
        this.factory = factory;
        this.output = output;

        // Attach ITestOutputHelper sink so captured logs are visible in test output
        this.factory.LogCapture.SetTestOutputHelper(output);
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

    [Fact]
    public async Task TestOutputHelper_Sink_IncludesCorrelationId()
    {
        // Use a capturing wrapper to inspect ITestOutputHelper output
        var capturingOutput = new CapturingTestOutputHelper(this.output);
        this.factory.LogCapture.SetTestOutputHelper(capturingOutput);

        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-sink-corr-123");
        await client.GetAsync("/api/health");

        // The ITestOutputHelper sink should have written lines containing the correlationId
        var sinkLines = capturingOutput.Lines;
        var hasCorrelation = sinkLines.Any(line =>
            line.Contains("correlationId=test-sink-corr-123"));

        Assert.True(hasCorrelation,
            $"Expected ITestOutputHelper sink output to contain 'correlationId=test-sink-corr-123'. " +
            $"Actual lines ({sinkLines.Count}): {string.Join(" | ", sinkLines.Take(5))}");
    }

    [Fact]
    public async Task TestOutputHelper_Sink_IncludesStructuredFields()
    {
        var capturingOutput = new CapturingTestOutputHelper(this.output);
        this.factory.LogCapture.SetTestOutputHelper(capturingOutput);

        using var client = this.factory.CreateClient();
        await client.GetAsync("/api/health");

        var sinkLines = capturingOutput.Lines;
        var enricherLines = sinkLines
            .Where(l => l.Contains("RequestLoggingEnricher"))
            .ToList();

        Assert.NotEmpty(enricherLines);

        // Verify scope fields
        Assert.True(enricherLines.Any(l => l.Contains("method=GET")),
            "Expected 'method=GET' in ITestOutputHelper sink output");
        Assert.True(enricherLines.Any(l => l.Contains("path=/api/health")),
            "Expected 'path=/api/health' in ITestOutputHelper sink output");

        // Verify state fields
        Assert.True(enricherLines.Any(l => l.Contains("StatusCode=")),
            "Expected 'StatusCode=' in ITestOutputHelper sink output");
        Assert.True(enricherLines.Any(l => l.Contains("ElapsedMs=")),
            "Expected 'ElapsedMs=' in ITestOutputHelper sink output");
    }
}

/// <summary>
/// Wraps <see cref="ITestOutputHelper"/> to capture written lines for assertion
/// while still forwarding to the real test output.
/// </summary>
internal sealed class CapturingTestOutputHelper : ITestOutputHelper
{
    private readonly ITestOutputHelper inner;
    private readonly List<string> lines = new();

    public CapturingTestOutputHelper(ITestOutputHelper inner)
    {
        this.inner = inner;
    }

    public IReadOnlyList<string> Lines => this.lines;

    public void WriteLine(string message)
    {
        this.lines.Add(message);
        this.inner.WriteLine(message);
    }

    public void WriteLine(string format, params object[] args)
    {
        var formatted = string.Format(format, args);
        this.lines.Add(formatted);
        this.inner.WriteLine(format, args);
    }
}
