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
///
/// <para><b>Phase 59 follow-up (2026-05-19):</b> when no scope is sent
/// AND exactly one product exists in the registry, this middleware
/// <b>auto-binds to that single product</b>. Solves the "silent empty
/// data" bug the user hit on <c>/api/work-items/.../execution</c>: pre-
/// fix, missing scope fell through to
/// <see cref="ProductDbContextFactory.CreateForCurrentRequest"/>'s
/// control-DB fallback (which has no per-product tables post-Phase-F),
/// and endpoints returned empty rows. Auto-bind makes the
/// single-product case Just Work without the operator needing to know
/// about the header. Multi-product installs without an explicit scope
/// still pass through unchanged (the UI sets the header from
/// <c>localStorage.forge.activeProductId</c>, and direct API callers
/// without a scope on a multi-product install get the same
/// silent-empty behaviour as before — preferable to a 400 that would
/// break the UI's cold-start path).</para>
///
/// <para>A response header
/// <see cref="ScopeResolvedResponseHeader"/> exposes the resolved
/// value (<c>{slug}</c> / <c>all</c> / <c>(none)</c> /
/// <c>(auto-{slug})</c>) so diagnostic tools + operators can see at a
/// glance what scope the server actually applied — and notice when
/// they expected something else.</para>
/// </summary>
public sealed class ProductScopeMiddleware
{
    /// <summary>
    /// Name of the response header that echoes what scope the middleware
    /// resolved. Always set so callers + diagnostics can confirm the
    /// server interpretation matched their intent.
    /// </summary>
    public const string ScopeResolvedResponseHeader = "X-Forge-Scope-Resolved";

    private readonly RequestDelegate next;

    public ProductScopeMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext ctx, ControlDbContext control)
    {
        var raw = ProductScope.Read(ctx);
        var isAll = string.Equals(raw, ProductScope.AllScope, StringComparison.OrdinalIgnoreCase);
        string resolvedTag = "(none)";

        if (isAll)
        {
            resolvedTag = ProductScope.AllScope;
        }
        else if (!string.IsNullOrEmpty(raw))
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
                resolvedTag = slug;
            }
            else
            {
                // Unknown id/slug — leave unset so the endpoint can
                // 404 with its own message. Tag reflects the
                // unmatched value so the operator notices.
                resolvedTag = $"(unknown:{raw})";
            }
        }
        else
        {
            // No scope sent. Phase 59 follow-up — auto-bind when there's
            // exactly one product in the registry. Two-product installs
            // and beyond keep the legacy silent-fallback path so the UI's
            // cold-start (where localStorage.forge.activeProductId may not
            // be set yet) keeps working. The X-Forge-Scope-Resolved
            // header makes the resolution visible regardless.
            var productSlugs = await control.Products
                .AsNoTracking()
                .Select(p => p.Slug)
                .Take(2) // we only care whether it's 0 / 1 / 2+
                .ToListAsync(ctx.RequestAborted);
            if (productSlugs.Count == 1 && !string.IsNullOrEmpty(productSlugs[0]))
            {
                ctx.Items[ProductDbContextFactory.SlugItemKey] = productSlugs[0];
                resolvedTag = $"(auto-{productSlugs[0]})";
            }
            // 0 products → control DB only; legacy admin paths still work.
            // 2+ products → leave unset; endpoints continue current
            //   behaviour. Operators with multi-product installs MUST send
            //   the header (the UI does this automatically once an
            //   active product is selected).
        }

        ctx.Response.Headers[ScopeResolvedResponseHeader] = resolvedTag;

        await this.next(ctx);
    }
}
