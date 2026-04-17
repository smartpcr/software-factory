// -----------------------------------------------------------------------
// <copyright file="EngineVersionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

/// <summary>
/// Minimal-API engine version endpoints.
/// </summary>
public static class EngineVersionEndpoints
{
    /// <summary>
    /// Maps the engine version endpoint to the application.
    /// </summary>
    public static WebApplication MapEngineVersionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/engine/version", (EngineAssemblyVersionProvider provider) =>
            Results.Ok(provider.GetVersion()))
            .WithTags("Engine")
            .WithName("GetEngineVersion");

        return app;
    }
}
