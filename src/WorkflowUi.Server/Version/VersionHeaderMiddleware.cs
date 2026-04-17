// -----------------------------------------------------------------------
// <copyright file="VersionHeaderMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Version;

/// <summary>
/// Middleware that adds X-Workflow-Ui-Version header to every response.
/// </summary>
public class VersionHeaderMiddleware
{
    private readonly RequestDelegate next;
    private readonly string version;

    public VersionHeaderMiddleware(RequestDelegate next)
    {
        this.next = next;
        this.version = VersionInfo.Current.Version;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Workflow-Ui-Version"] = this.version;
            return Task.CompletedTask;
        });

        await this.next(context);
    }
}
