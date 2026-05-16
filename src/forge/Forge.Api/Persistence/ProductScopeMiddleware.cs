// -----------------------------------------------------------------------
// <copyright file="ProductScopeMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase D — reads <c>X-Forge-Product-Id</c> (header) / <c>productId</c>
/// (query) on every request, resolves it against the control DB
/// (<see cref="ControlDbContext.Products"/>), and stashes the resolved
/// slug on <see cref="HttpContext.Items"/> so the
/// <see cref="ProductDbContextFactory"/> can pick the right per-product
/// DB without re-querying.
///
/// <para>If the caller sends a <see cref="ProductScope.AllScope"/> value
/// ("all"), no slug is stashed and product-DB-bound endpoints must
/// either enumerate every product (analytics paths) or refuse.</para>
///
/// <para>The middleware is non-fatal: unknown IDs leave the slug unset so
/// the endpoint can produce its own 404. Auth and role gating are
/// handled upstream of this middleware.</para>
/// </summary>
public sealed class ProductScopeMiddleware
{
    private readonly RequestDelegate next;

    public ProductScopeMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext ctx, ControlDbContext control)
    {
        var raw = ProductScope.Read(ctx);
        if (!string.IsNullOrEmpty(raw) &&
            !string.Equals(raw, ProductScope.AllScope, StringComparison.OrdinalIgnoreCase))
        {
            // Resolve the request value (could be productId OR slug) into
            // a slug. The product registry is small enough that scanning
            // for either match is cheap; the result is cached per-request.
            var lower = raw.ToLowerInvariant();
            var slug = await control.Products
                .AsNoTracking()
                .Where(p => p.Id == lower || p.Slug == lower)
                .Select(p => p.Slug)
                .FirstOrDefaultAsync(ctx.RequestAborted);
            if (!string.IsNullOrEmpty(slug))
            {
                ctx.Items[ProductDbContextFactory.SlugItemKey] = slug;
            }
        }

        await this.next(ctx);
    }
}
