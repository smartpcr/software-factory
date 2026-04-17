// -----------------------------------------------------------------------
// <copyright file="HealthEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

/// <summary>
/// Minimal-API health endpoints.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps the health endpoint to the application.
    /// </summary>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () =>
            Results.Ok(new
            {
                status = "ok",
                timestamp = DateTimeOffset.UtcNow,
            }))
            .WithTags("Health")
            .WithName("GetHealth")
            .AllowAnonymous();

        return app;
    }
}
