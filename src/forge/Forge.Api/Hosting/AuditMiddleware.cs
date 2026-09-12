// -----------------------------------------------------------------------
// <copyright file="AuditMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Hosting;

using System.Text;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Retain the audit action without buffering or persisting a credential-bearing request body.</summary>
public sealed record SensitiveRequestBody;

/// <summary>
/// Phase 6.10 — records every write request to the <c>forge_actions</c>
/// table after the response is produced. Read methods skip; write methods
/// always record the user, method, path, status, idempotency key (if any)
/// and a 256-char snippet of the request body.
/// </summary>
public class AuditMiddleware
{
    private const int BodySnippetMax = 256;

    private readonly RequestDelegate next;

    public AuditMiddleware(RequestDelegate next) { this.next = next; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api/operator")
            || ctx.Request.Path == "/api/chat/link-token"
            || HttpMethods.IsGet(ctx.Request.Method)
            || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method))
        {
            await this.next(ctx);
            return;
        }

        // Buffer-read the body so we can audit a snippet AND let the endpoint read it.
        string? snippet = null;
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<SensitiveRequestBody>() is not null)
            snippet = "[redacted]";
        else if (ctx.Request.ContentLength is > 0)
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var buf = new char[BodySnippetMax];
            var read = await reader.ReadAsync(buf, 0, BodySnippetMax);
            snippet = new string(buf, 0, read);
            ctx.Request.Body.Position = 0;
        }

        await this.next(ctx);

        var user = ForgeUser.UserId(ctx) ?? "anonymous";
        var key = ctx.Request.Headers[HttpUserContext.IdempotencyHeader].FirstOrDefault();
        var control = ctx.RequestServices.GetRequiredService<ControlDbContext>();
        var db = ctx.RequestServices.GetRequiredService<ProductDbContext>();
        control.Audit.Add(new AuditRow
        {
            At = DateTimeOffset.UtcNow,
            User = user,
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? string.Empty,
            Status = ctx.Response.StatusCode,
            IdempotencyKey = key,
            RequestBodySnippet = snippet,
        });
        try { await control.SaveChangesAsync(ctx.RequestAborted); }
        catch { /* never break the response on audit failure */ }
    }
}
