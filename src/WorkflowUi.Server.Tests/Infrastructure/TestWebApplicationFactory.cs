// -----------------------------------------------------------------------
// <copyright file="TestWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Infrastructure;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Auth;

/// <summary>
/// Shared test application factory for integration tests.
/// Seeds in-memory configuration, overrides environment, and provides log capture.
/// Configures a test authentication scheme that auto-authenticates as an Editor+Viewer user.
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
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowUi:Auth:AllowDevBypass"] = "true",
            });
        });
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(this.LogCapture);
            logging.SetMinimumLevel(LogLevel.Debug);
        });
        builder.ConfigureServices(services =>
        {
            // Override authentication to use a test scheme that always succeeds
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }

    /// <summary>
    /// Test auth handler that auto-authenticates as an Editor+Viewer user.
    /// </summary>
    private class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim(ClaimTypes.Role, AuthPolicies.EditorRole),
                new Claim(ClaimTypes.Role, AuthPolicies.ViewerRole),
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
