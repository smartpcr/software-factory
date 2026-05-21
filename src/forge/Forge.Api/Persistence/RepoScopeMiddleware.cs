// -----------------------------------------------------------------------
// <copyright file="RepoScopeMiddleware.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 80.E — reads <c>X-Forge-Repo-Id</c> (header) / <c>repoId</c> (query),
/// validates it against the control <see cref="RepoRegistryRow"/> index, and:
/// <list type="number">
///   <item>stashes the resolved repo id on <see cref="HttpContext.Items"/>
///         (<see cref="RepoScope.RepoIdItemKey"/>) so endpoints can scope to it;</item>
///   <item>when no product slug was stashed by
///         <see cref="ProductScopeMiddleware"/> (i.e. the caller sent
///         <c>X-Forge-Repo-Id</c> but not <c>X-Forge-Product-Id</c>), resolves
///         the repo's owning product slug from the registry and stashes it on
///         <see cref="ProductDbContextFactory.SlugItemKey"/> so the per-request
///         <see cref="ProductDbContext"/> binds to that repo's <c>{slug}.db</c>.
///         A repo id alone is therefore enough to route the product DB.</item>
/// </list>
///
/// <para>Runs immediately after <see cref="ProductScopeMiddleware"/> so it can
/// see (and only supplement) the product slug that middleware stashed. Non-fatal:
/// an unknown / "all" / absent repo id leaves the active repo unset (no repo
/// filter). A response header <c>X-Forge-Repo-Resolved</c> echoes what was
/// resolved (<c>{repoId}</c> / <c>all</c> / <c>(none)</c> / <c>(unknown:…)</c>)
/// — mirrors <see cref="ProductScopeMiddleware.ScopeResolvedResponseHeader"/>.</para>
/// </summary>
public sealed class RepoScopeMiddleware
{
    public const string ScopeResolvedResponseHeader = "X-Forge-Repo-Resolved";

    private readonly RequestDelegate next;

    public RepoScopeMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext ctx, ControlDbContext control)
    {
        var raw = RepoScope.Read(ctx);
        var resolvedTag = "(none)";

        if (string.Equals(raw, RepoScope.AllScope, StringComparison.OrdinalIgnoreCase))
        {
            resolvedTag = RepoScope.AllScope;
        }
        else if (!string.IsNullOrEmpty(raw))
        {
            var reg = await control.RepoRegistry
                .AsNoTracking()
                .Where(r => r.Id == raw && r.DeletedAt == null)
                .Select(r => new { r.Id, r.ProductSlug })
                .FirstOrDefaultAsync(ctx.RequestAborted);
            if (reg is not null)
            {
                ctx.Items[RepoScope.RepoIdItemKey] = reg.Id;
                resolvedTag = reg.Id;

                // Repo-id-alone routing: if ProductScopeMiddleware didn't stash a
                // slug (no X-Forge-Product-Id), bind the product DB to the repo's
                // own product so the per-request ProductDbContext finds it.
                if (!ctx.Items.ContainsKey(ProductDbContextFactory.SlugItemKey)
                    && !string.IsNullOrEmpty(reg.ProductSlug))
                {
                    ctx.Items[ProductDbContextFactory.SlugItemKey] = reg.ProductSlug;
                }
            }
            else
            {
                resolvedTag = $"(unknown:{raw})";
            }
        }

        ctx.Response.Headers[ScopeResolvedResponseHeader] = resolvedTag;

        await this.next(ctx);
    }
}
