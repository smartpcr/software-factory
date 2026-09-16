// -----------------------------------------------------------------------
// <copyright file="RequireRole.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Compatibility role gates using authenticated stable IDs and explicit product roles.</summary>
public static class RequireRole
{
    public const string ProductOwner = "product-owner";
    // xiaodoli's existing stable account ID; display names cannot confer authority.
    public const string BuiltInProductOwnerId = "user-e34bb713f38e45768d79547e40a62e61";

    public static IResult? Check(HttpContext ctx, ControlDbContext db, params string[] roles) =>
        CheckForProduct(ctx, db, ProductScope.Read(ctx) ?? ctx.Items[ProductDbContextFactory.SlugItemKey] as string, roles);

    public static IResult? CheckForProduct(HttpContext ctx, ControlDbContext db, string? productId, params string[] roles)
    {
        if (ForgeUser.RequireWriter(ctx) is { } denied) return denied;
        // Host-only legacy admin routes used the product-owner label. Their
        // replacement authority is host enrollment, never UserRow.Role.
        if (string.IsNullOrEmpty(productId) && ctx.RequestServices.GetService<HostIdentityStore>()?
            .GetEnrollment(ForgeUser.UserId(ctx)!) is { Enabled: true, HostAdmin: true }) return null;
        var role = ResolveRoleForProduct(ctx, db, productId);
        return role is not null && roles.Contains(role, StringComparer.Ordinal) ? null
            : Results.Problem(title: "Capability denied", statusCode: 403);
    }

    public static string? ResolveRoleForProduct(HttpContext ctx, ControlDbContext db, string? productId)
    {
        var id = ForgeUser.UserId(ctx);
        if (id is null || string.IsNullOrEmpty(productId) || productId == "all"
            || !db.Users.AsNoTracking().Any(u => u.Id == id)) return null;
        var product = db.Products.AsNoTracking().SingleOrDefault(p => p.Id == productId || p.Slug == productId);
        if (product is null) return null;
        if (id == BuiltInProductOwnerId) return ProductOwner;
        return db.UserProductRoles.AsNoTracking().Where(r => r.UserId == id && r.ProductId == product.Id).Select(r => r.Role).SingleOrDefault();
    }

    public static string? ResolveRole(HttpContext ctx, ControlDbContext db) =>
        ResolveRoleForProduct(ctx, db, ProductScope.Read(ctx) ?? ctx.Items[ProductDbContextFactory.SlugItemKey] as string);

    public static Task<string?> ResolveRoleAsync(HttpContext ctx, ControlDbContext db, CancellationToken ct = default) =>
        Task.FromResult(ResolveRole(ctx, db));
}
