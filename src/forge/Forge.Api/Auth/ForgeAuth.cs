// -----------------------------------------------------------------------
// <copyright file="ForgeAuth.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 7.6 — pluggable auth.
///
/// Default mode: <c>Mode = "single-user"</c>. Writes must present BOTH
///   * <c>X-Forge-User</c>     — caller identity (already enforced by ForgeUser)
///   * <c>X-Forge-Secret</c>   — local shared secret matching <c>Forge:Auth:Secret</c>
/// When <c>Forge:Auth:Secret</c> is empty the secret check is skipped (legacy
/// behaviour, suitable for trusted dev environments behind a reverse proxy).
///
/// <c>Mode = "azure-ad"</c> is a placeholder — wires the standard JWT bearer
/// middleware so deployments can swap to Entra ID without code edits. The
/// JWT path is configured via <c>Forge:Auth:AzureAd:*</c> standard keys.
/// </summary>
public class ForgeAuthOptions
{
    public const string SectionName = "Forge:Auth";

    /// <summary>"single-user" (default) or "azure-ad".</summary>
    public string Mode { get; set; } = "single-user";

    /// <summary>Shared secret for single-user mode. Empty → no secret check.</summary>
    public string Secret { get; set; } = string.Empty;

    public AzureAdOptions AzureAd { get; set; } = new();

    public class AzureAdOptions
    {
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;
    }
}

/// <summary>Verifies the X-Forge-Secret header against <c>Forge:Auth:Secret</c>.</summary>
public class ForgeSecretMiddleware
{
    public const string HeaderName = "X-Forge-Secret";

    private readonly RequestDelegate next;
    private readonly ForgeAuthOptions options;

    public ForgeSecretMiddleware(RequestDelegate next, IOptions<ForgeAuthOptions> options)
    {
        this.next = next;
        this.options = options.Value;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Health probe + read methods bypass — only writes are gated.
        if (HttpMethods.IsGet(ctx.Request.Method)
            || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method)
            || ctx.Request.Path.StartsWithSegments("/api/health")
            || ctx.Request.Path.StartsWithSegments("/api/webhooks")
            || ctx.Request.Path.StartsWithSegments("/hubs"))
        {
            await this.next(ctx);
            return;
        }

        if (!string.Equals(this.options.Mode, "single-user", StringComparison.OrdinalIgnoreCase))
        {
            // Other modes (e.g. azure-ad) handle auth via the platform middleware.
            await this.next(ctx);
            return;
        }
        if (string.IsNullOrEmpty(this.options.Secret))
        {
            // Trusted dev: no secret configured → skip the check.
            await this.next(ctx);
            return;
        }

        var presented = ctx.Request.Headers[HeaderName].FirstOrDefault();
        if (!CryptographicEquals(presented, this.options.Secret))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                title = "Unauthorized",
                detail = $"Provide the {HeaderName} header. Configure Forge:Auth:Secret on the server.",
                status = 401,
            });
            return;
        }

        await this.next(ctx);
    }

    private static bool CryptographicEquals(string? a, string b)
    {
        if (a is null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
