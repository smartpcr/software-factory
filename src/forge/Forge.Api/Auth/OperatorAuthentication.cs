// -----------------------------------------------------------------------
// <copyright file="OperatorAuthentication.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Globalization;
using System.Net;
using System.Security.Claims;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class OperatorAuthenticationOptions
{
    public string[] RemoteBrowserOrigins { get; set; } = [];

    public string[] ApiOrigins { get; set; } = ["http://localhost:5180", "http://127.0.0.1:5180", "http://[::1]:5180"];
    public string[] BrowserOrigins { get; set; } = ["http://localhost:5181", "http://127.0.0.1:5181", "http://[::1]:5181",
        "http://localhost:5180", "http://127.0.0.1:5180", "http://[::1]:5180"];
}

public static class OperatorAuthentication
{
    public const string CsrfHeader = "X-Forge-CSRF";
    public static void AddOperatorAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new HostIdentityStore(
            sp.GetRequiredService<IConfiguration>()["Forge:Identity:StorePath"] ?? HostIdentityStore.DefaultPath(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<OperatorSessions>();
        services.AddSingleton<ILocalBrowserIdentity, LocalBrowserIdentity>();
        services.AddSingleton<OperatorCodeDelivery>();
        services.AddScoped<ForgeActorResolver>();
        services.AddScoped<ForgeCapabilityAuthorizer>();
        services.AddScoped<ForgeActionContext>();
        services.AddOptions<OperatorAuthenticationOptions>().Bind(configuration.GetSection("Forge:Identity"))
            .Validate(o => o.ApiOrigins.Length > 0 && o.BrowserOrigins.Length > 0
                && o.ApiOrigins.Concat(o.BrowserOrigins).All(IsLoopbackOrigin), "Only explicit loopback origins are supported.")
            .Validate(o => o.RemoteBrowserOrigins.All(IsExplicitOrigin), "Remote browser origins must be explicit HTTP(S) origins.")
            .ValidateOnStart();
        services.AddAuthentication(OperatorSessions.Scheme).AddCookie(OperatorSessions.Scheme, o =>
        {
            o.Cookie.Name = "Forge.Operator";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = false;
            o.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; },
                OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; },
                OnValidatePrincipal = async c =>
                {
                    var resolver = c.HttpContext.RequestServices.GetRequiredService<ForgeActorResolver>();
                    if (c.Principal is null || await resolver.FromBrowserAsync(c.Principal, c.HttpContext.RequestAborted) is null)
                        c.RejectPrincipal();
                },
            };
        });
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(o =>
        {
            o.HeaderName = CsrfHeader;
            o.Cookie.Name = "Forge.CSRF";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
    }

    public static bool IsExplicitOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && !uri.Host.Contains('*') && origin.TrimEnd('/') == uri.GetLeftPart(UriPartial.Authority);

    public static bool IsLoopbackOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.IsLoopback && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && origin.TrimEnd('/') == uri.GetLeftPart(UriPartial.Authority);
}

/// <summary>Reject rebinding/cross-origin requests before authentication or application state access.</summary>
public sealed class OperatorOriginMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, IOptions<OperatorAuthenticationOptions> options)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        var origin = ctx.Request.Headers.Origin;
        var expected = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        if ((remote is not null && !IPAddress.IsLoopback(remote))
            || !options.Value.ApiOrigins.Contains(expected, StringComparer.OrdinalIgnoreCase)
            || origin.Count > 1 || (origin.Count == 1 && !options.Value.BrowserOrigins.Contains(origin[0], StringComparer.OrdinalIgnoreCase)
                && !options.Value.RemoteBrowserOrigins.Contains(origin[0], StringComparer.OrdinalIgnoreCase)))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { title = "Untrusted host or origin", status = 403 });
            return;
        }
        await next(ctx);
    }
}

/// <summary>Runs before scope resolution, idempotency, audit, and endpoint body binding.</summary>
public sealed class OperatorRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ForgeActorResolver actors, HostIdentityStore identities, IAntiforgery antiforgery)
    {
        var path = ctx.Request.Path.Value ?? "";
        var safe = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method);
        var publicRoute = path is "/api/operator/status" or "/api/operator/csrf" or "/api/operator/pair" or "/api/operator/local-sign-in"
            or "/api/operator/request-pairing-code"
            or "/health" or "/api/health";
        var actor = await actors.FromBrowserAsync(ctx.User, ctx.RequestAborted);
        if (!publicRoute && (path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/hubs/", StringComparison.Ordinal)) && actor is null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { title = identities.HasEnrollment ? "Pair browser" : "Needs operator enrollment", status = 401 });
            return;
        }
        if (!safe)
        {
            if (ctx.Request.Headers.Origin.Count != 1)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { title = "A trusted Origin is required", status = 403 });
                return;
            }
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { title = "CSRF validation failed", status = 403 });
                return;
            }
            if (string.Equals(ProductScope.Read(ctx), "all", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { title = "Select a specific product for mutations", status = 403 });
                return;
            }
        }
        try { await next(ctx); }
        catch (ForgeAccessDeniedException)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { title = "Capability denied", status = 403 });
        }
    }
}

/// <summary>
/// Host-wide throttle for out-of-band pairing codes. One code per window: a newer code
/// supersedes the previous one, so this is what stops a request flood from starving the
/// operator of a usable code.
/// </summary>
public sealed class OperatorCodeDelivery(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private DateTimeOffset lastSentAt = DateTimeOffset.MinValue;

    public bool TryBegin(out TimeSpan retryAfter)
    {
        lock (this.gate)
        {
            var elapsed = clock.GetUtcNow() - this.lastSentAt;
            if (elapsed < Window)
            {
                retryAfter = Window - elapsed;
                return false;
            }
            this.lastSentAt = clock.GetUtcNow();
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

public static class OperatorSessionEndpoints
{
    private static readonly SemaphoreSlim LocalEnrollmentGate = new(1, 1);

    public static void MapOperatorSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/status", async (HttpContext ctx, HostIdentityStore identities, ForgeActorResolver actors,
            ControlDbContext control, IOptionsMonitor<ChatBridgeOptions> chat) =>
        {
            var actor = await actors.FromBrowserAsync(ctx.User, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            var enrolledIds = identities.EnrolledUserIds;
            var enrolled = await control.Users.AnyAsync(u => enrolledIds.Contains(u.Id), ctx.RequestAborted);
            return Results.Ok(new { state = actor is not null ? "Paired" : enrolled ? "Pair browser" : "Needs operator enrollment", userId = actor?.UserId,
                localSignInAvailable = OperatingSystem.IsWindows() && (!identities.HasEnrollmentHistory
                    || await LocalEnrollmentAsync(identities, control, ctx.RequestAborted) is not null),
                codeDeliveryAvailable = enrolled && DeliveryChatId(chat.CurrentValue) is not null });
        }).AllowAnonymous();
        app.MapGet("/api/operator/csrf", (HttpContext ctx, IAntiforgery anti) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = anti.GetAndStoreTokens(ctx).RequestToken });
        }).AllowAnonymous();
        app.MapPost("/api/operator/pair", async (PairBrowserRequest req, HttpContext ctx, HostIdentityStore identities,
            OperatorSessions sessions, ControlDbContext control, IAntiforgery anti) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var userId = req.Challenge is null ? null : identities.ChallengeUserId(req.Challenge, "browser");
            var enrollment = userId is not null && await control.Users.AnyAsync(u => u.Id == userId, ctx.RequestAborted)
                ? identities.RedeemBrowserChallenge(req.Challenge!) : null;
            if (enrollment is null || !await control.Users.AnyAsync(u => u.Id == enrollment.UserId, ctx.RequestAborted))
                return Results.Problem(title: "Invalid, expired, or used pairing challenge", statusCode: 403);
            return await SignInAsync(ctx, enrollment, sessions, anti);
        }).AllowAnonymous();
        app.MapPost("/api/operator/local-sign-in", async (HttpContext ctx, HostIdentityStore identities,
            ILocalBrowserIdentity browser, OperatorSessions sessions, ControlDbContext control, IAntiforgery anti) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            // A loopback proxy must never lend its Windows identity to another caller.
            if (ctx.Request.Headers.ContainsKey("X-Forge-Proxy") || ctx.Request.Headers.ContainsKey("Forwarded")
                || ctx.Request.Headers.ContainsKey("X-Forwarded-For")
                || !OperatorAuthentication.IsLoopbackOrigin(ctx.Request.Headers.Origin.ToString()) || !browser.IsHostUser(ctx))
                return Results.Problem(title: "Connect directly from the Forge host user's browser", statusCode: 403);
            var enrollment = await EnsureLocalEnrollmentAsync(identities, control, ctx.RequestAborted);
            if (enrollment is null)
                return Results.Problem(title: "Local sign-in requires exactly one enrolled Forge account. Use browser pairing to select an account.", statusCode: 403);
            return await SignInAsync(ctx, enrollment, sessions, anti);
        }).AllowAnonymous();
        // Lets any browser on a trusted origin ASK for a pairing code, while the code itself
        // travels out of band to the operator's own chat. The requester never sees it, so
        // reaching this endpoint proves nothing and grants nothing; possession of the chat
        // account is the actual gate. The code is sent directly rather than through the
        // notification outbox, which would persist a live secret in the control DB and keep
        // retrying it for a day.
        app.MapPost("/api/operator/request-pairing-code", async (HttpContext ctx, HostIdentityStore identities,
            ControlDbContext control, IChatTransport transport, IOptionsMonitor<ChatBridgeOptions> chat,
            OperatorCodeDelivery delivery) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (DeliveryChatId(chat.CurrentValue) is not { } chatId)
                return Results.Problem(title: "Out-of-band code delivery is not configured on this host", statusCode: 503);
            // Issuing a code invalidates the previous one, so an unthrottled endpoint would
            // let anyone on the subnet keep the operator's own code from ever arriving.
            if (!delivery.TryBegin(out var retryAfter))
            {
                ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Problem(title: "A pairing code was just sent. Use that one, or wait before asking again.", statusCode: 429);
            }
            var enrollment = await LocalEnrollmentAsync(identities, control, ctx.RequestAborted);
            if (enrollment is null)
                return Results.Problem(title: "Requesting a code requires exactly one enrolled Forge account", statusCode: 403);
            var origin = ctx.Request.Headers.Origin.Count == 1 ? ctx.Request.Headers.Origin[0] : "an unknown origin";
            var caller = ctx.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";
            var sent = await transport.SendAsync(chatId,
                $"Forge pairing code: {identities.IssueBrowserChallenge(enrollment.UserId)}\n\n"
                + $"Requested from {origin} ({caller}). Valid for two minutes, single use.\n"
                + "If you did not request this, ignore it and it expires on its own.",
                null, ctx.RequestAborted);
            return sent.Ok
                ? Results.Accepted(value: new { delivered = true, platform = transport.Platform })
                : Results.Problem(title: "Could not deliver the pairing code to chat", statusCode: 502);
        }).AllowAnonymous();
        app.MapPost("/api/operator/logout", async (HttpContext ctx, OperatorSessions sessions) =>
        {
            sessions.Revoke(ctx.User.FindFirstValue(OperatorSessions.SessionClaim));
            await ctx.SignOutAsync(OperatorSessions.Scheme);
            return Results.NoContent();
        });
        app.MapPost("/api/operators/{userId}/revoke", async (string userId, HttpContext ctx, ForgeActionContext action, HostIdentityStore identities, OperatorSessions sessions) =>
        {
            await action.RequireAsync(ForgeCapabilities.IdentityManage, new(), ctx.RequestAborted);
            identities.RevokeEnrollment(userId);
            sessions.RevokeUser(userId);
            return Results.NoContent();
        });
        app.MapPut("/api/operators/{userId}/roles/{productId}", async (string userId, string productId, OperatorRoleRequest req,
            HttpContext ctx, ForgeActionContext action, ControlDbContext control) =>
        {
            await action.RequireAsync(ForgeCapabilities.IdentityManage, new(), ctx.RequestAborted);
            if (!ForgeCapabilities.Roles.Contains(req.Role) || productId == "all") return Results.BadRequest();
            if (!await control.Users.AnyAsync(u => u.Id == userId, ctx.RequestAborted)
                || !await control.Products.AnyAsync(p => p.Id == productId, ctx.RequestAborted)) return Results.NotFound();
            var role = await control.UserProductRoles.FindAsync(new object[] { userId, productId }, ctx.RequestAborted);
            if (role is null) { role = new() { UserId = userId, ProductId = productId }; control.UserProductRoles.Add(role); }
            role.Role = req.Role;
            role.UpdatedAt = DateTimeOffset.UtcNow;
            await control.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Destination for out-of-band codes, or null when chat is disabled or unconfigured.</summary>
    private static string? DeliveryChatId(ChatBridgeOptions options)
    {
        if (!options.Enabled) return null;
        var chatId = options.Telegram.AllowedChatIds.FirstOrDefault();
        return chatId == 0 ? null : chatId.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<OperatorEnrollment?> LocalEnrollmentAsync(HostIdentityStore identities, ControlDbContext control, CancellationToken ct)
    {
        var ids = identities.EnrolledUserIds.ToList();
        var users = await control.Users.Where(u => ids.Contains(u.Id)).Select(u => u.Id).Take(2).ToListAsync(ct);
        return users.Count == 1 && identities.GetEnrollment(users[0]) is { Enabled: true } enrollment ? enrollment : null;
    }

    private static async Task<OperatorEnrollment?> EnsureLocalEnrollmentAsync(HostIdentityStore identities, ControlDbContext control, CancellationToken ct)
    {
        await LocalEnrollmentGate.WaitAsync(ct);
        try
        {
            // Only the verified host browser can bootstrap. Never undo a revocation or
            // replace a missing/ambiguous enrolled account with a new identity.
            if (identities.HasEnrollmentHistory) return await LocalEnrollmentAsync(identities, control, ct);
            if (!OperatingSystem.IsWindows()) return null;
            using var windows = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (windows.User is null) return null;
            var localId = "windows:" + windows.User.Value;
            var name = windows.Name;
            var shortName = name.Split('\\').Last();
            var user = await control.Users.SingleOrDefaultAsync(u => u.Id == localId, ct);
            if (user is null)
            {
                var users = await control.Users.Where(u => u.DisplayName == name || u.DisplayName == shortName).Take(2).ToListAsync(ct);
                if (users.Count == 1) user = users[0];
            }
            if (user is null)
            {
                user = new UserRow { Id = localId, DisplayName = name, Role = "human" };
                control.Users.Add(user);
                await control.SaveChangesAsync(ct);
            }
            identities.Enroll(user.Id, hostAdmin: false);
            return identities.GetEnrollment(user.Id);
        }
        finally { LocalEnrollmentGate.Release(); }
    }

    private static async Task<IResult> SignInAsync(HttpContext ctx, OperatorEnrollment enrollment, OperatorSessions sessions, IAntiforgery anti)
    {
        sessions.Revoke(ctx.User.FindFirstValue(OperatorSessions.SessionClaim));
        var principal = sessions.Issue(enrollment);
        await ctx.SignInAsync(OperatorSessions.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
        });
        ctx.User = principal;
        return Results.Ok(new { state = "Paired", userId = enrollment.UserId, token = anti.GetAndStoreTokens(ctx).RequestToken });
    }

    public sealed record PairBrowserRequest(string? Challenge);
    public sealed record OperatorRoleRequest(string Role);
}
