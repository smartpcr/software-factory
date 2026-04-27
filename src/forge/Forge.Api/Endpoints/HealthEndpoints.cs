// -----------------------------------------------------------------------
// <copyright file="HealthEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

/// <summary>
/// Health endpoints for the Forge API.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps GET /api/health to a small ok+timestamp payload.
    /// </summary>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
            {
                status = "ok",
                timestamp = DateTimeOffset.UtcNow,
            }))
            .WithTags("Health")
            .WithName("GetHealth");

        return app;
    }
}
