// -----------------------------------------------------------------------
// <copyright file="AuthEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Phase 9.6 — interactive sign-in endpoints.
/// GET  /api/auth/{kind}/status  — current cached account / connectivity (anon-readable).
/// POST /api/auth/{kind}/sign-in — trigger the WAM / browser popup. Blocks until
///                                 the user finishes consent or the request is cancelled.
/// POST /api/auth/{kind}/sign-out — drop the cached account.
/// </summary>
public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/auth").WithTags("Auth");

        grp.MapGet("/{kind}/status", async (string kind, IEnumerable<IAuthService> services, CancellationToken ct) =>
            {
                var svc = services.FirstOrDefault(s => s.Kind == kind);
                if (svc is null) return Results.Problem(title: $"Unknown auth kind {kind}", statusCode: 404);
                return Results.Ok(await svc.StatusAsync(ct));
            })
            .WithName("AuthStatus");

        grp.MapPost("/{kind}/sign-in", async (HttpContext ctx, string kind, IEnumerable<IAuthService> services, CancellationToken ct) =>
            {
                var auth = ForgeUser.RequireWriter(ctx);
                if (auth is not null) return auth;

                var svc = services.FirstOrDefault(s => s.Kind == kind);
                if (svc is null) return Results.Problem(title: $"Unknown auth kind {kind}", statusCode: 404);

                // Sign-in can take a while — the user has to consent in WAM /
                // the browser. Cap the wait at 5 minutes to avoid orphaning a
                // server thread if the popup is closed without resolving.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(5));
                var status = await svc.SignInAsync(cts.Token);
                return status.Connected
                    ? Results.Ok(status)
                    : Results.Problem(title: status.Error ?? "Sign-in failed", statusCode: 401);
            })
            .WithName("AuthSignIn");

        grp.MapPost("/{kind}/sign-out", async (HttpContext ctx, string kind, IEnumerable<IAuthService> services, CancellationToken ct) =>
            {
                var auth = ForgeUser.RequireWriter(ctx);
                if (auth is not null) return auth;

                var svc = services.FirstOrDefault(s => s.Kind == kind);
                if (svc is null) return Results.Problem(title: $"Unknown auth kind {kind}", statusCode: 404);
                await svc.SignOutAsync(ct);
                return Results.NoContent();
            })
            .WithName("AuthSignOut");

        return app;
    }
}
