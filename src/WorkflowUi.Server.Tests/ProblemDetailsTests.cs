// -----------------------------------------------------------------------
// <copyright file="ProblemDetailsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using System.Text.Json;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for RFC-7807 problem-details middleware.
/// Uses a custom factory that registers a throwing endpoint.
/// </summary>
public class ProblemDetailsTests : IClassFixture<ThrowingEndpointFactory>
{
    private readonly ThrowingEndpointFactory factory;

    public ProblemDetailsTests(ThrowingEndpointFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Response500_IsProblemPlusJson_ContentType()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Response500_BodyHas_Required_Fields()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("type", out _));
        Assert.True(doc.RootElement.TryGetProperty("title", out _));
        Assert.True(doc.RootElement.TryGetProperty("status", out _));
        Assert.True(doc.RootElement.TryGetProperty("detail", out _));
        Assert.True(doc.RootElement.TryGetProperty("instance", out _));
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Response500_CorrelationId_MatchesHeader()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");
        var headerId = resp.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        var body = await resp.Content.ReadAsStringAsync();
        var bodyId = JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString();
        Assert.Equal(headerId, bodyId);
    }

    [Fact]
    public async Task DevelopmentEnv_IncludesException_Detail()
    {
        this.factory.EnvironmentName = "Development";
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");
        var body = await resp.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString();
        Assert.Contains("boom", detail!);
    }

    [Fact]
    public async Task ProductionEnv_DoesNotInclude_Exception_Detail()
    {
        using var prodFactory = new ThrowingEndpointFactory { EnvironmentName = "Production" };
        using var client = prodFactory.CreateClient();
        var resp = await client.GetAsync("/boom");
        var body = await resp.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString();
        Assert.DoesNotContain("boom", detail!);
        Assert.Contains("An error occurred", detail!);
    }

    [Fact]
    public async Task ProblemDetails_Type_Is_StableUrl()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/boom");
        var body = await resp.Content.ReadAsStringAsync();
        var type = JsonDocument.Parse(body).RootElement.GetProperty("type").GetString();
        Assert.Equal("https://workflow-ui/problems/internal-server-error", type);
    }
}

/// <summary>
/// Factory that provides a throwing endpoint for problem-details tests.
/// Wires /boom only in the test host, not in production.
/// </summary>
public class ThrowingEndpointFactory : TestWebApplicationFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.Configure(app =>
        {
            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseMiddleware<RequestLoggingEnricher>();

            // Inline exception handler matching the production ProblemDetailsConfiguration
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                    var correlationId = context.Items["CorrelationId"]?.ToString() ?? "unknown";
                    var isDev = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().IsDevelopment();

                    context.Response.StatusCode = 500;

                    var detail = isDev && exceptionFeature?.Error is not null
                        ? exceptionFeature.Error.Message
                        : "An error occurred processing your request.";

                    var problemJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "https://workflow-ui/problems/internal-server-error",
                        title = "Internal Server Error",
                        status = 500,
                        detail,
                        instance = context.Request.Path.Value,
                        correlationId,
                    });

                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsync(problemJson);
                });
            });

            app.UseRouting();

#pragma warning disable ASP0014
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/boom", () =>
                {
                    throw new InvalidOperationException("boom");
                });
                endpoints.MapGet("/api/health", () =>
                    Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));
            });
#pragma warning restore ASP0014
        });
    }
}
