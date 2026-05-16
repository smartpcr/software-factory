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
        if (HttpMethods.IsGet(ctx.Request.Method)
            || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method))
        {
            await this.next(ctx);
            return;
        }

        // Buffer-read the body so we can audit a snippet AND let the endpoint read it.
        ctx.Request.EnableBuffering();
        string? snippet = null;
        if (ctx.Request.ContentLength is > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var buf = new char[BodySnippetMax];
            var read = await reader.ReadAsync(buf, 0, BodySnippetMax);
            snippet = new string(buf, 0, read);
            ctx.Request.Body.Position = 0;
        }

        await this.next(ctx);

        var user = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "anonymous";
        var key = ctx.Request.Headers[HttpUserContext.IdempotencyHeader].FirstOrDefault();
        var db = ctx.RequestServices.GetRequiredService<ProductDbContext>();
        db.Audit.Add(new AuditRow
        {
            At = DateTimeOffset.UtcNow,
            User = user,
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? string.Empty,
            Status = ctx.Response.StatusCode,
            IdempotencyKey = key,
            RequestBodySnippet = snippet,
        });
        try { await db.SaveChangesAsync(ctx.RequestAborted); }
        catch { /* never break the response on audit failure */ }
    }
}
