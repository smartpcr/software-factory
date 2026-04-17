// -----------------------------------------------------------------------
// <copyright file="ProblemDetailsWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using Microsoft.AspNetCore.Diagnostics;

/// <summary>
/// Configures RFC-7807 problem-details responses with correlation ID.
/// </summary>
public static class ProblemDetailsConfiguration
{
    /// <summary>
    /// Adds and configures the problem details service and exception handler.
    /// </summary>
    public static WebApplicationBuilder AddProblemDetailsServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var correlationId = context.HttpContext.Items["CorrelationId"]?.ToString() ?? "unknown";
                context.ProblemDetails.Extensions["correlationId"] = correlationId;
                context.ProblemDetails.Instance = context.HttpContext.Request.Path;

                if (context.ProblemDetails.Status is null or 0)
                {
                    context.ProblemDetails.Status = context.HttpContext.Response.StatusCode;
                }

                if (string.IsNullOrEmpty(context.ProblemDetails.Type))
                {
                    context.ProblemDetails.Type = $"https://workflow-ui/problems/{GetProblemCode(context.ProblemDetails.Status.Value)}";
                }
            };
        });

        return builder;
    }

    /// <summary>
    /// Adds the exception handler middleware that produces problem-details responses.
    /// </summary>
    public static WebApplication UseProblemDetailsExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                var correlationId = context.Items["CorrelationId"]?.ToString() ?? "unknown";
                var isDev = context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();

                if (exceptionFeature?.Error is not null)
                {
                    logger.LogError(
                        exceptionFeature.Error,
                        "Unhandled exception for {CorrelationId}",
                        correlationId);
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;

                var detail = isDev && exceptionFeature?.Error is not null
                    ? exceptionFeature.Error.Message
                    : "An error occurred processing your request.";

                var problemJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "https://workflow-ui/problems/internal-server-error",
                    title = "Internal Server Error",
                    status = 500,
                    detail,
                    instance = context.Request.Path.Value,
                    correlationId,
                });

                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(problemJson);
            });
        });

        return app;
    }

    private static string GetProblemCode(int statusCode) => statusCode switch
    {
        400 => "bad-request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not-found",
        500 => "internal-server-error",
        _ => $"error-{statusCode}",
    };
}
