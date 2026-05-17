// -----------------------------------------------------------------------
// <copyright file="ProductScope.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 8.9 — resolves the requested product scope for a read endpoint.
/// Resolution order:
///   1) explicit <c>productId</c> query string,
///   2) <c>X-Forge-Product-Id</c> header,
///   3) implicit fallback = the default <c>atlas</c> product (Phase 8.3 backfill).
/// The literal value <c>"all"</c> bypasses scoping entirely (Phase 10 will gate
/// this behind the Product Owner role; today every signed-in caller may use it).
/// </summary>
public static class ProductScope
{
    public const string HeaderName = "X-Forge-Product-Id";

    public const string QueryName = "productId";

    public const string AllScope = "all";

    /// <summary>Reads the requested scope ("all" / a productId / null when unset).</summary>
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
    /// Returns the set of repo ids that belong to the resolved scope, or null
    /// when the caller asked for <c>all</c> (no filtering required).
    /// </summary>
    /// <remarks>
    /// When the requested productId is unknown, this returns an empty set —
    /// callers should treat that as "filter to nothing" rather than failing the
    /// request. The aggregate <see cref="ResolveAsync"/> result also exposes
    /// the resolved productId so handlers can echo it in responses.
    /// </remarks>
    public static async Task<ResolvedScope> ResolveAsync(HttpContext ctx, ControlDbContext db, CancellationToken ct = default)
    {
        var raw = Read(ctx);
        if (string.Equals(raw, AllScope, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedScope(AllScope, null);
        }

        // When no product header/query is sent, show all products instead
        // of defaulting to a hardcoded product id.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResolvedScope(AllScope, null);
        }

        var productId = raw;
        var repoIds = await db.Set<RepoRow>().AsNoTracking()
            .Where(r => r.ProductId == productId)
            .Select(r => r.Id)
            .ToListAsync(ct);
        return new ResolvedScope(productId, repoIds);
    }
}

/// <summary>
/// Result of <see cref="ProductScope.ResolveAsync"/>.
/// </summary>
/// <param name="ProductId">The resolved product id, or "all" for cross-product reads.</param>
/// <param name="RepoIds">
/// Repo ids in scope. <c>null</c> means "no filtering" (the caller asked for "all").
/// An empty list means "filter to nothing" (unknown productId, or product with no repos).
/// </param>
public sealed record ResolvedScope(string ProductId, IReadOnlyList<string>? RepoIds)
{
    public bool IsAll => this.RepoIds is null;
}
