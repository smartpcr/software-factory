// -----------------------------------------------------------------------
// <copyright file="TestWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared test application factory for integration tests.
/// Seeds in-memory configuration, overrides environment, and provides log capture.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Gets or sets the environment name override. Defaults to "Development".
    /// </summary>
    public string EnvironmentName { get; set; } = "Development";

    /// <summary>
    /// Gets the capturing logger provider for asserting log output.
    /// </summary>
    public CapturingLoggerProvider LogCapture { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(this.EnvironmentName);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(this.LogCapture);
            logging.SetMinimumLevel(LogLevel.Debug);
        });
        builder.ConfigureServices(services =>
        {
            // Additional test service overrides go here
        });
    }
}
