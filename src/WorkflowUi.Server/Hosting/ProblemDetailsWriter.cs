// -----------------------------------------------------------------------
// <copyright file="ProblemDetailsWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Custom <see cref="IProblemDetailsWriter"/> that injects correlationId into RFC-7807 payloads.
/// </summary>
public sealed class CorrelationIdProblemDetailsWriter : IProblemDetailsWriter
{
    /// <inheritdoc/>
    public bool CanWrite(ProblemDetailsContext context) => true;

    /// <inheritdoc/>
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var httpContext = context.HttpContext;
        var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? "unknown";

        context.ProblemDetails.Extensions["correlationId"] = correlationId;
        context.ProblemDetails.Instance ??= httpContext.Request.Path;

        if (context.ProblemDetails.Status is null or 0)
        {
            context.ProblemDetails.Status = httpContext.Response.StatusCode;
        }

        if (string.IsNullOrEmpty(context.ProblemDetails.Type))
        {
            context.ProblemDetails.Type = $"https://workflow-ui/problems/{GetProblemCode(context.ProblemDetails.Status.Value)}";
        }

        httpContext.Response.ContentType = "application/problem+json";
        return new ValueTask(httpContext.Response.WriteAsJsonAsync(
            context.ProblemDetails,
            context.HttpContext.RequestAborted));
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

/// <summary>
/// Configures RFC-7807 problem-details responses with correlation ID.
/// </summary>
public static class ProblemDetailsConfiguration
{
    /// <summary>
    /// Adds and configures the problem details service with custom writer.
    /// </summary>
    public static WebApplicationBuilder AddProblemDetailsServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<IProblemDetailsWriter, CorrelationIdProblemDetailsWriter>();

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

                var problemJson = JsonSerializer.Serialize(new
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
}
