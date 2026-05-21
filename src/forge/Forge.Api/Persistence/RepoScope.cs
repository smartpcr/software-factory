// -----------------------------------------------------------------------
// <copyright file="RepoScope.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Phase 80.E — the repo-level read scope, layered under
/// <see cref="ProductScope"/>. A request may carry an active repo via the
/// <c>X-Forge-Repo-Id</c> header (or <c>repoId</c> query). When set, it is the
/// <i>narrower</i> scope: list endpoints filter to that one repo, and — because
/// a repo id resolves its owning product through the control
/// <see cref="RepoRegistryRow"/> — the request's product DB is resolved from it
/// even when no <c>X-Forge-Product-Id</c> is sent (handled by
/// <see cref="RepoScopeMiddleware"/>).
///
/// <para>The literal <c>"all"</c> (or an absent header) means "no repo filter"
/// — every repo in the product scope. Endpoints don't read this type directly;
/// <see cref="ProductScope.ResolveAsync"/> consults it and narrows its
/// <see cref="ResolvedScope.RepoIds"/> so the existing product-scope filter in
/// each endpoint transparently scopes to the active repo.</para>
/// </summary>
public static class RepoScope
{
    public const string HeaderName = "X-Forge-Repo-Id";

    public const string QueryName = "repoId";

    public const string AllScope = "all";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which
    /// <see cref="RepoScopeMiddleware"/> stashes the resolved (registry-validated)
    /// active repo id for the current request.
    /// </summary>
    public const string RepoIdItemKey = "Forge.ActiveRepoId";

    /// <summary>Reads the requested repo scope ("all" / a repoId / null when unset).</summary>
    public static string? Read(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue(QueryName, out var qv) && qv.Count > 0 && !string.IsNullOrWhiteSpace(qv[0]))
        {
            return qv[0];
        }
        if (ctx.Request.Headers.TryGetValue(HeaderName, out var hv) && hv.Count > 0 && !string.IsNullOrWhiteSpace(hv[0]))
        {
            return hv[0];
        }
        return null;
    }

    /// <summary>
    /// The registry-validated active repo id for this request (stashed by
    /// <see cref="RepoScopeMiddleware"/>), or <c>null</c> when none / "all" /
    /// unknown. Endpoints that need the active repo id directly (rather than via
    /// <see cref="ProductScope.ResolveAsync"/>) read it here.
    /// </summary>
    public static string? ActiveRepoId(HttpContext ctx) =>
        ctx.Items.TryGetValue(RepoIdItemKey, out var v) ? v as string : null;
}
