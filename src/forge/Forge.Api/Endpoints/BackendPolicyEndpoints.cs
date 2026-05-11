// -----------------------------------------------------------------------
// <copyright file="BackendPolicyEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Phase 47d — read-only endpoint that returns the operator's current
/// <see cref="ForgeBackendPolicy"/> so the UI's Edit-Agent modal can grey
/// out backend / invoke-mode options that aren't in the allow-list. The
/// values are sourced from the same <c>Forge:BackendPolicy</c>
/// configuration section the seed pass uses, so UI and server stay in
/// lockstep.
///
/// <para>The endpoint is intentionally anonymous — the policy is a
/// configuration fact, not a secret, and the UI fetches it before any
/// auth flow. No write counterpart: changing the policy means editing
/// <c>appsettings.json</c> and restarting Forge.Api.</para>
/// </summary>
public static class BackendPolicyEndpoints
{
    public static WebApplication MapBackendPolicyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/backend-policy", GetBackendPolicy)
            .WithTags("Settings")
            .WithName("GetBackendPolicy");
        return app;
    }

    private static IResult GetBackendPolicy(IConfiguration config)
    {
        var policy = ForgeBackendPolicy.FromConfiguration(config);
        return Results.Ok(new
        {
            canonical = policy.Canonical,
            canonicalInvokeMode = policy.CanonicalInvokeMode,
            allowedBackends = policy.AllowedBackends,
            allowedInvokeModes = policy.AllowedInvokeModes,
        });
    }
}
