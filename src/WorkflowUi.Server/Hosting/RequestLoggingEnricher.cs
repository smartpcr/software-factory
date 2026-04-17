// -----------------------------------------------------------------------
// <copyright file="RequestLoggingEnricher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using System.Diagnostics;

/// <summary>
/// Middleware that wraps the request pipeline in a logger scope
/// containing the correlation ID, method, path, and logs elapsed time on completion.
/// </summary>
public sealed class RequestLoggingEnricher
{
    private readonly RequestDelegate next;
    private readonly ILogger<RequestLoggingEnricher> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestLoggingEnricher"/> class.
    /// </summary>
    public RequestLoggingEnricher(RequestDelegate next, ILogger<RequestLoggingEnricher> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    /// <summary>
    /// Processes the request with structured logging scope.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Items["CorrelationId"]?.ToString() ?? "unknown";
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";
        var sw = Stopwatch.StartNew();

        using (this.logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId,
            ["method"] = method,
            ["path"] = path,
        }))
        {
            try
            {
                await this.next(context);
            }
            finally
            {
                sw.Stop();
                this.logger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                    method,
                    path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds);
            }
        }
    }
}
