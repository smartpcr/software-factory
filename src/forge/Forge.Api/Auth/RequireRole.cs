// -----------------------------------------------------------------------
// <copyright file="RequireRole.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 10.9 — minimal-API role gate.
///
/// Resolves the calling user via the existing X-Forge-User header (Phase 7.6)
/// and then looks them up in the <c>users</c> table. When no user matches
/// (single-user mode pre-Phase-9 sign-in), Forge falls back to the operator
/// bootstrap behaviour — the lone seeded user is treated as a Product Owner
/// so localhost development isn't blocked by the gate.
/// </summary>
public static class RequireRole
{
    public const string ProductOwner = "product-owner";

    public static IResult? Check(HttpContext ctx, DbContext db, params string[] roles)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var role = ResolveRole(ctx, db);
        if (role is null) return null; // single-user dev — pass-through.
        if (Array.IndexOf(roles, role) >= 0) return null;

        return Results.Problem(
            title: "Forbidden",
            detail: $"This action requires role: {string.Join(", ", roles)}. You are: {role}.",
            statusCode: 403);
    }

    /// <summary>
    /// Phase 13 / Q13.2 — product-scoped role gate. Resolves the caller's role
    /// against <paramref name="productId"/> regardless of which product they
    /// happen to have selected in the UI. Use this when an action targets a
    /// specific crew / repo / work item; <see cref="Check"/> is fine for
    /// global-scope writes that don't bind to a product.
    /// </summary>
    public static IResult? CheckForProduct(HttpContext ctx, DbContext db, string? productId, params string[] roles)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var role = ResolveRoleForProduct(ctx, db, productId);
        if (role is null) return null; // single-user dev — pass-through.
        if (Array.IndexOf(roles, role) >= 0) return null;

        return Results.Problem(
            title: "Forbidden",
            detail: productId is null
                ? $"This action requires role: {string.Join(", ", roles)}. You are: {role}."
                : $"This action requires role {string.Join(" / ", roles)} on product {productId}. You are: {role}.",
            statusCode: 403);
    }

    /// <summary>
    /// Sync resolver for the product-scoped role. Looks up the user, then a
    /// matching <c>UserProductRoleRow</c>, then falls back to the user's
    /// global <c>UserRow.Role</c>. Pre-sign-in (no users yet) returns null.
    /// </summary>
    public static string? ResolveRoleForProduct(HttpContext ctx, DbContext db, string? productId)
    {
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caller)) return null;

        var user = db.Set<UserRow>().AsNoTracking()
            .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
            .Select(u => new { u.Id, u.Role })
            .FirstOrDefault();
        if (user is null)
        {
            if (!db.Set<UserRow>().AsNoTracking().Any()) return null;
            return "human";
        }

        if (!string.IsNullOrEmpty(productId))
        {
            var over = db.Set<UserProductRoleRow>().AsNoTracking()
                .Where(r => r.UserId == user.Id && r.ProductId == productId)
                .Select(r => r.Role)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(over)) return over;
        }
        return user.Role;
    }

    /// <summary>
    /// Sync helper — looks up the calling user's role, honoring the
    /// per-product override when X-Forge-Product-Id is present. Returns
    /// null when no signed-in users exist yet (default = open).
    /// </summary>
    public static string? ResolveRole(HttpContext ctx, DbContext db)
    {
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caller)) return null;

        // Match by email (e.g. "lingxd@gmail.com") or login or display name
        // because the X-Forge-User header isn't tied to a specific column today.
        var user = db.Set<UserRow>()
            .AsNoTracking()
            .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
            .Select(u => new { u.Id, u.Role })
            .FirstOrDefault();
        if (user is null)
        {
            // Bootstrap: if no users yet, treat as open (single-user dev).
            if (!db.Set<UserRow>().AsNoTracking().Any()) return null;
            return "human";
        }

        // Phase 9.12 — product-scoped override.
        var productId = ctx.Request.Headers["X-Forge-Product-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(productId) && productId != "all")
        {
            var over = db.Set<UserProductRoleRow>().AsNoTracking()
                .Where(r => r.UserId == user.Id && r.ProductId == productId)
                .Select(r => r.Role)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(over)) return over;
        }
        return user.Role;
    }

    /// <summary>Async sibling of <see cref="ResolveRole"/> for handlers that already do async DB work.</summary>
    public static async Task<string?> ResolveRoleAsync(HttpContext ctx, DbContext db, CancellationToken ct = default)
    {
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caller)) return null;
        var user = await db.Set<UserRow>()
            .AsNoTracking()
            .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
            .Select(u => new { u.Id, u.Role })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            if (!await db.Set<UserRow>().AsNoTracking().AnyAsync(ct)) return null;
            return "human";
        }

        var productId = ctx.Request.Headers["X-Forge-Product-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(productId) && productId != "all")
        {
            var over = await db.Set<UserProductRoleRow>().AsNoTracking()
                .Where(r => r.UserId == user.Id && r.ProductId == productId)
                .Select(r => r.Role)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(over)) return over;
        }
        return user.Role;
    }
}
