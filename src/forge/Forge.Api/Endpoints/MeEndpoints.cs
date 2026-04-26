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
/// First-run rule (Phase 9.9): the very first user to register on a fresh DB
/// is auto-promoted to <c>product-owner</c>. The bootstrap email from
/// <see cref="ForgeAuthOptions.BootstrapPoEmail"/> is also auto-promoted on
/// any visit, so the operator who registered Forge keeps PO across DB resets.
/// </summary>
public static class MeEndpoints
{
    public static WebApplication MapMeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me", GetMe)
            .WithTags("Me")
            .WithName("GetMe");
        return app;
    }

    private static async Task<IResult> GetMe(
        ForgeDbContext db,
        IEnumerable<IAuthService> authServices,
        IOptions<ForgeAuthOptions> authOptions,
        CancellationToken ct)
    {
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
            : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (row is null && !string.IsNullOrEmpty(email))
        {
            // First-run rule (9.9): no users yet → promote to product-owner.
            // Bootstrap match: also promote even if other users already exist,
            // so the operator who set up Forge keeps PO after a DB reset.
            var anyExisting = await db.Users.AnyAsync(ct);
            var anyPo = await db.Users.AnyAsync(u => u.Role == "product-owner", ct);
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
            db.Users.Add(row);
            await db.SaveChangesAsync(ct);
        }
        else if (row is not null)
        {
            // Refresh display info on every call — cheap, avoids stale name on rename.
            var updated = false;
            if (row.DisplayName != displayName) { row.DisplayName = displayName; updated = true; }
            if (github.Connected && row.Login != github.UserName) { row.Login = github.UserName; updated = true; }
            row.LastSeenAt = DateTimeOffset.UtcNow;
            if (updated) await db.SaveChangesAsync(ct);
        }

        var user = new CurrentUser(
            Id: row?.Id ?? $"anon-{primary.Kind}",
            DisplayName: row?.DisplayName ?? displayName,
            Email: row?.Email ?? email,
            Login: row?.Login ?? (github.Connected ? github.UserName : null),
            Initials: ComputeInitials(row?.DisplayName ?? displayName),
            AvatarUrl: row?.AvatarUrl,
            Role: row?.Role ?? "human",
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
