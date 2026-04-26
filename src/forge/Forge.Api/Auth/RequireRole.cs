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

    public static IResult? Check(HttpContext ctx, ForgeDbContext db, params string[] roles)
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
    /// Sync helper — looks up the calling user's role. Returns null when no
    /// signed-in users exist yet (default = open).
    /// </summary>
    public static string? ResolveRole(HttpContext ctx, ForgeDbContext db)
    {
        // Sync EF call inside a request handler; SQLite is fast enough that
        // this is fine for a per-request lookup.
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caller)) return null;

        // Match by email (e.g. "lingxd@gmail.com") or login or display name
        // because the X-Forge-User header isn't tied to a specific column today.
        var row = db.Users
            .AsNoTracking()
            .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
            .Select(u => new { u.Role })
            .FirstOrDefault();
        if (row is not null) return row.Role;

        // Bootstrap: if no users yet, treat as open (single-user dev).
        if (!db.Users.AsNoTracking().Any()) return null;
        return "human";
    }

    /// <summary>Async sibling of <see cref="ResolveRole"/> for handlers that already do async DB work.</summary>
    public static async Task<string?> ResolveRoleAsync(HttpContext ctx, ForgeDbContext db, CancellationToken ct = default)
    {
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caller)) return null;
        var role = await db.Users
            .AsNoTracking()
            .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct);
        if (role is not null) return role;
        if (!await db.Users.AsNoTracking().AnyAsync(ct)) return null;
        return "human";
    }
}
