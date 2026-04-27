// -----------------------------------------------------------------------
// <copyright file="HumanGateEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Domain;
using Microsoft.AspNetCore.Http;

/// <summary>Phase 6.9 — exposes the live human-gate rule list (read-only).</summary>
public static class HumanGateEndpoints
{
    public static WebApplication MapHumanGateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/human-gate/rules", (IHumanGateEvaluator e) => Results.Ok(e.Rules()))
            .WithTags("HumanGate")
            .WithName("GetHumanGateRules");
        return app;
    }
}
