// -----------------------------------------------------------------------
// <copyright file="DevBypassAuthenticationHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Authentication handler that auto-authenticates requests as a synthetic dev-user
/// when the environment is Development, AllowDevBypass is true, and the request
/// comes from a loopback address.
/// </summary>
public class DevBypassAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevBypass";

    private readonly AuthOptions authOptions;
    private readonly IHostEnvironment environment;

    public DevBypassAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        this.authOptions = authOptions.Value;
        this.environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!this.environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!this.authOptions.AllowDevBypass)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var remoteIp = this.Context.Connection.RemoteIpAddress;
        if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
        {
            return Task.FromResult(AuthenticateResult.Fail("Dev bypass requires loopback address."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim(ClaimTypes.Name, "Developer"),
            new Claim(ClaimTypes.Role, AuthPolicies.EditorRole),
            new Claim(ClaimTypes.Role, AuthPolicies.ViewerRole),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
