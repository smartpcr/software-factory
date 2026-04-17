// -----------------------------------------------------------------------
// <copyright file="AuditMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Middleware that logs audit entries for write operations (POST, PUT, PATCH, DELETE).
/// </summary>
public class AuditMiddleware
{
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH", "DELETE",
    };

    private readonly RequestDelegate next;
    private readonly IAuditLog auditLog;
    private readonly ILogger<AuditMiddleware> logger;

    public AuditMiddleware(RequestDelegate next, IAuditLog auditLog, ILogger<AuditMiddleware> logger)
    {
        this.next = next;
        this.auditLog = auditLog;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await this.next(context);

        if (!WriteMethods.Contains(context.Request.Method))
        {
            return;
        }

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var action = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";
        var outcomeCode = context.Response.StatusCode;

        var entry = new AuditLogEntry(
            DateTime.UtcNow,
            userId,
            action,
            path,
            ETag: null,
            outcomeCode);

        this.auditLog.Record(entry);

        this.logger.LogInformation(
            "Audit: {Action} {Path} by {UserId} → {OutcomeCode}",
            action,
            path,
            userId,
            outcomeCode);
    }
}
