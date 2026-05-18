// -----------------------------------------------------------------------
// <copyright file="MeEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 9.6 / 9.9 — <c>GET /api/me</c> aggregates the per-provider auth
/// state into a single identity record and lazy-creates a <see cref="UserRow"/>
/// the first time a provider sign-in surfaces a fresh email.
///
/// First-run rule (Phase 9.9): the very first user to register on a fresh control
/// is auto-promoted to <c>product-owner</c>. The bootstrap email from
/// <see cref="ForgeAuthOptions.BootstrapPoEmail"/> is also auto-promoted on
/// any visit, so the operator who registered Forge keeps PO across control resets.
/// </summary>
public static class MeEndpoints
{
    public static WebApplication MapMeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me", GetMe)
            .WithTags("Me")
            .WithName("GetMe");
        app.MapPut("/api/me/role", PutRole)
            .WithTags("Me")
            .WithName("PutMyRole");
        app.MapPut("/api/me/avatar", PutAvatar)
            .WithTags("Me")
            .WithName("PutMyAvatar");
        app.MapPut("/api/me/display-name", PutDisplayName)
            .WithTags("Me")
            .WithName("PutMyDisplayName");
        return app;
    }

    /// <summary>Allowed role values, matched against the role list documented on UserRow.</summary>
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "human", "eng", "rev", "qa", "arch", "tech-lead", "product-owner",
    };

    private static async Task<IResult> PutRole(
        HttpContext ctx,
        SetRoleRequest req,
        ControlDbContext control,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(req.Role) || !AllowedRoles.Contains(req.Role))
        {
            return Results.Problem(title: $"Unsupported role '{req.Role}'", statusCode: 400);
        }

        // Resolve "me" from the X-Forge-User header. Fall back to the lone
        // bootstrap user if the header doesn't match any row (single-user dev).
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var me = string.IsNullOrEmpty(caller)
            ? null
            : await control.Users.FirstOrDefaultAsync(
                u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller, ct);
        me ??= (await control.Users.ToListAsync(ct)).OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (me is null)
        {
            return Results.Problem(title: "No user identity yet — sign in first", statusCode: 401);
        }

        if (string.IsNullOrEmpty(req.ProductId))
        {
            // Empty productId → set the user's global default role.
            me.Role = req.Role;
            await control.SaveChangesAsync(ct);
            return Results.Ok(new { scope = "global", userId = me.Id, role = me.Role });
        }

        // Per-product upsert.
        var productExists = await control.Products.AsNoTracking().AnyAsync(p => p.Id == req.ProductId, ct);
        if (!productExists)
        {
            return Results.Problem(title: $"Unknown product {req.ProductId}", statusCode: 404);
        }

        var row = await control.UserProductRoles
            .FirstOrDefaultAsync(r => r.UserId == me.Id && r.ProductId == req.ProductId, ct);
        if (row is null)
        {
            row = new UserProductRoleRow { UserId = me.Id, ProductId = req.ProductId };
            control.UserProductRoles.Add(row);
        }
        row.Role = req.Role;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(ct);

        return Results.Ok(new { scope = "product", userId = me.Id, productId = req.ProductId, role = row.Role });
    }

    public sealed record SetRoleRequest(string? ProductId, string Role);

    public sealed record SetAvatarRequest(string Avatar);

    public sealed record SetDisplayNameRequest(string DisplayName);

    private static async Task<IResult> PutAvatar(
        HttpContext ctx,
        SetAvatarRequest req,
        ControlDbContext control,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var me = string.IsNullOrEmpty(caller)
            ? null
            : await control.Users.FirstOrDefaultAsync(
                u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller, ct);
        me ??= (await control.Users.ToListAsync(ct)).OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (me is null)
            return Results.Problem(title: "No user identity yet — sign in first", statusCode: 401);

        // Empty string clears; otherwise store as a relative avatar path served via /api/avatars/.
        if (string.IsNullOrWhiteSpace(req.Avatar))
        {
            me.AvatarUrl = null;
        }
        else
        {
            var av = req.Avatar.Trim();
            if (av.Contains("..") || av.Contains('/') || av.Contains('\\'))
                return Results.Problem(title: "Invalid avatar filename", statusCode: 400);
            me.AvatarUrl = $"/api/avatars/{av}";
        }
        await control.SaveChangesAsync(ct);
        return Results.Ok(new { userId = me.Id, avatarUrl = me.AvatarUrl });
    }

    private static async Task<IResult> PutDisplayName(
        HttpContext ctx,
        SetDisplayNameRequest req,
        ControlDbContext control,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.Problem(title: "Display name cannot be empty", statusCode: 400);

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var me = string.IsNullOrEmpty(caller)
            ? null
            : await control.Users.FirstOrDefaultAsync(
                u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller, ct);
        me ??= (await control.Users.ToListAsync(ct)).OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (me is null)
            return Results.Problem(title: "No user identity yet — sign in first", statusCode: 401);

        me.DisplayName = req.DisplayName.Trim();
        await control.SaveChangesAsync(ct);
        return Results.Ok(new { userId = me.Id, displayName = me.DisplayName });
    }

    private static async Task<IResult> GetMe(
        HttpContext ctx,
        ControlDbContext control,
        IEnumerable<IAuthService> authServices,
        IOptions<ForgeAuthOptions> authOptions,
        CancellationToken ct)
    {
        // Phase 9.12 — when scoped to a product, the role returned reflects
        // that product's override (if any) so the UI shows the right gate.
        var productId = ctx.Request.Headers["X-Forge-Product-Id"].FirstOrDefault();
        var ado = await StatusFor(authServices, "ado", ct);
        var github = await StatusFor(authServices, "github", ct);

        // Pick whichever provider is connected first; ado wins ties because
        // ADO sign-in delivers a verified work email which is the most useful
        // identity. Fall back to github when only it's connected.
        var primary = ado.Connected ? ado : github.Connected ? github : null;

        if (primary is null)
        {
            // No provider connected yet — return a stable "unsigned" placeholder
            // so the UI can render a Sign-in CTA without blowing up.
            return Results.Ok(new CurrentUser(
                Id: string.Empty,
                DisplayName: "Sign in",
                Email: null,
                Login: null,
                Initials: "·",
                AvatarUrl: null,
                Role: "human",
                Providers: new CurrentUserProviders(
                    Ado: ToContract(ado),
                    GitHub: ToContract(github))));
        }

        var email = primary.Account; // both providers put email-ish handles in Account
        var displayName = !string.IsNullOrWhiteSpace(primary.UserName) ? primary.UserName! : email ?? "Forge user";

        // Lazy-create or refresh the user row.
        var row = string.IsNullOrEmpty(email)
            ? null
            : await control.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (row is null && !string.IsNullOrEmpty(email))
        {
            // First-run rule (9.9): no users yet → promote to product-owner.
            // Bootstrap match: also promote even if other users already exist,
            // so the operator who set up Forge keeps PO after a control reset.
            var anyExisting = await control.Users.AnyAsync(ct);
            var anyPo = await control.Users.AnyAsync(u => u.Role == "product-owner", ct);
            var isBootstrap = string.Equals(email, authOptions.Value.BootstrapPoEmail, StringComparison.OrdinalIgnoreCase);
            var role = (!anyExisting || (!anyPo && isBootstrap) || isBootstrap) ? "product-owner" : "human";

            row = new UserRow
            {
                Id = $"user-{Guid.NewGuid():N}",
                DisplayName = displayName,
                Email = email,
                Login = github.Connected ? github.UserName : null,
                Role = role,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            };
            control.Users.Add(row);
            await control.SaveChangesAsync(ct);
        }
        else if (row is not null)
        {
            // Refresh display info on every call — cheap, avoids stale name on rename.
            var updated = false;
            if (row.DisplayName != displayName) { row.DisplayName = displayName; updated = true; }
            if (github.Connected && row.Login != github.UserName) { row.Login = github.UserName; updated = true; }
            row.LastSeenAt = DateTimeOffset.UtcNow;
            if (updated) await control.SaveChangesAsync(ct);
        }

        // Phase 9.12 — product-scoped role override. Falls back to the user's
        // global role when no override exists for the current product.
        var effectiveRole = row?.Role ?? "human";
        if (row is not null && !string.IsNullOrEmpty(productId) && productId != "all")
        {
            var override_ = await control.UserProductRoles.AsNoTracking()
                .Where(r => r.UserId == row.Id && r.ProductId == productId)
                .Select(r => r.Role)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(override_)) effectiveRole = override_;
        }

        var user = new CurrentUser(
            Id: row?.Id ?? $"anon-{primary.Kind}",
            DisplayName: row?.DisplayName ?? displayName,
            Email: row?.Email ?? email,
            Login: row?.Login ?? (github.Connected ? github.UserName : null),
            Initials: ComputeInitials(row?.DisplayName ?? displayName),
            AvatarUrl: row?.AvatarUrl,
            Role: effectiveRole,
            Providers: new CurrentUserProviders(
                Ado: ToContract(ado),
                GitHub: ToContract(github)));

        return Results.Ok(user);
    }

    private static async Task<AuthStatus> StatusFor(IEnumerable<IAuthService> services, string kind, CancellationToken ct)
    {
        var svc = services.FirstOrDefault(s => s.Kind == kind);
        if (svc is null) return new AuthStatus(kind, false, null, null, null);
        try { return await svc.StatusAsync(ct); }
        catch { return new AuthStatus(kind, false, null, null, null); }
    }

    private static CurrentUserProvider ToContract(AuthStatus s) =>
        new(s.Connected, s.Account, s.ExpiresAt, s.Error);

    private static string ComputeInitials(string name)
    {
        var s = (name ?? string.Empty).Trim();
        if (s.Length == 0) return "·";
        var parts = s.Split(new[] { ' ', '\t', '_', '-', '.', '@' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0].Length >= 2
                ? (parts[0][0].ToString() + parts[0][1]).ToUpperInvariant()
                : parts[0][0].ToString().ToUpperInvariant();
        }
        return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }
}
