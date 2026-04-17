// -----------------------------------------------------------------------
// <copyright file="TestWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Shared test application factory for integration tests.
/// Seeds in-memory configuration and overrides environment.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Gets or sets the environment name override. Defaults to "Development".
    /// </summary>
    public string EnvironmentName { get; set; } = "Development";

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(this.EnvironmentName);
        builder.ConfigureServices(services =>
        {
            // Additional test service overrides go here
        });
    }
}
