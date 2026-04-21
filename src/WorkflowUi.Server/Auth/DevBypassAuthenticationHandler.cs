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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Authentication handler that auto-authenticates requests as a synthetic dev-user
/// when the environment is Development and AllowDevBypass is true, or when the
/// server is hosted on localhost. Requests must come from a loopback address.
/// </summary>
public class DevBypassAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevBypass";

    private readonly AuthOptions authOptions;
    private readonly IHostEnvironment environment;
    private readonly IServer server;

    public DevBypassAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions,
        IHostEnvironment environment,
        IServer server)
        : base(options, logger, encoder)
    {
        this.authOptions = authOptions.Value;
        this.environment = environment;
        this.server = server;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var isLocalhostBinding = IsListeningOnLocalhost(this.server);
        var devBypass = this.environment.IsDevelopment() && this.authOptions.AllowDevBypass;

        if (!devBypass && !isLocalhostBinding)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var remoteIp = this.Context.Connection.RemoteIpAddress;
        if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
        {
            return Task.FromResult(AuthenticateResult.Fail("Bypass requires loopback address."));
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

    /// <summary>
    /// Returns true when all server addresses are bound to localhost / 127.0.0.1.
    /// </summary>
    internal static bool IsListeningOnLocalhost(IServer server)
    {
        var addressFeature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        if (addressFeature == null || !addressFeature.Addresses.Any())
        {
            return false;
        }

        return addressFeature.Addresses.All(addr =>
        {
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
            {
                return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || uri.Host == "127.0.0.1"
                    || uri.Host == "::1";
            }

            return false;
        });
    }
}
