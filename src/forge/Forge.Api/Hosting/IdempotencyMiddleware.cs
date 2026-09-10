// -----------------------------------------------------------------------
// <copyright file="IdempotencyMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Hosting;

using System.Text;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 6.12 — replays the cached response when a write request carries
/// an <c>Idempotency-Key</c> header that the server has seen before.
/// Cache is per-method+path+key (so the same key can be reused across
/// different endpoints without collisions). Entries live for 24h.
/// Read methods (GET/HEAD/OPTIONS) bypass the middleware entirely.
/// </summary>
public class IdempotencyMiddleware
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly RequestDelegate next;
    private readonly ILogger<IdempotencyMiddleware> logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

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

        var key = ctx.Request.Headers[HttpUserContext.IdempotencyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            await this.next(ctx);
            return;
        }
        // Scope by method + path so the same key on different endpoints is independent.
        var scoped = $"{ForgeUser.UserId(ctx)} {ProductScope.Read(ctx)} {ctx.Request.Method} {ctx.Request.Path} {key}";
        var control = ctx.RequestServices.GetRequiredService<ControlDbContext>();
        var db = ctx.RequestServices.GetRequiredService<ProductDbContext>();

        var existing = await db.Idempotency.FirstOrDefaultAsync(i => i.Key == scoped, ctx.RequestAborted);
        if (existing is not null && DateTimeOffset.UtcNow - existing.At < Ttl)
        {
            this.logger.LogInformation("forge.idempotency.replay key={Key} status={Status}", key, existing.Status);
            ctx.Response.StatusCode = existing.Status;
            ctx.Response.ContentType = existing.ContentType;
            ctx.Response.Headers["Idempotent-Replay"] = "true";
            await ctx.Response.WriteAsync(existing.Body, ctx.RequestAborted);
            return;
        }

        // Buffer the downstream response so we can persist it.
        var originalBody = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await this.next(ctx);
        }
        finally
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, ctx.RequestAborted);
            ctx.Response.Body = originalBody;
        }

        if (ctx.Response.StatusCode is >= 200 and < 300)
        {
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            db.Idempotency.Add(new IdempotencyRow
            {
                Key = scoped,
                At = DateTimeOffset.UtcNow,
                Status = ctx.Response.StatusCode,
                ContentType = ctx.Response.ContentType ?? "application/json",
                Body = body,
            });
            try { await db.SaveChangesAsync(ctx.RequestAborted); }
            catch (DbUpdateException) { /* concurrent insert by another retry — ignore */ }
        }
    }
}
