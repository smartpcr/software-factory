// -----------------------------------------------------------------------
// <copyright file="VersionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Version;

/// <summary>
/// Minimal-API version endpoint returning build metadata.
/// Anonymous-accessible (health-adjacent).
/// </summary>
public static class VersionEndpoints
{
    public static WebApplication MapVersionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/version", () => Results.Ok(VersionInfo.Current))
            .WithTags("Version")
            .WithName("GetVersion")
            .AllowAnonymous();

        return app;
    }
}
