// -----------------------------------------------------------------------
// <copyright file="CorrelationIdMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using System.Text.RegularExpressions;

/// <summary>
/// Reads or generates a correlation ID and propagates it in headers and HttpContext.Items.
/// </summary>
public sealed partial class CorrelationIdMiddleware
{
    /// <summary>
    /// The correlation ID header name.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 128;
    private readonly RequestDelegate next;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    /// <summary>
    /// Processes the request, ensuring a correlation ID is present.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId;

        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            correlationId = Sanitize(headerValue.ToString());
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items["CorrelationId"] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await this.next(context);
    }

    private static string Sanitize(string value)
    {
        // Strip CR/LF to prevent header injection
        var cleaned = ControlCharRegex().Replace(value, string.Empty);

        // Truncate to max length, regenerate if too long or empty after sanitization
        if (cleaned.Length > MaxLength || string.IsNullOrWhiteSpace(cleaned))
        {
            return Guid.NewGuid().ToString("N");
        }

        return cleaned;
    }

    [GeneratedRegex(@"[\r\n\x00-\x1f]")]
    private static partial Regex ControlCharRegex();
}
