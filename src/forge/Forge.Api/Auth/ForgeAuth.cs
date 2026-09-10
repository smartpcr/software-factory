// -----------------------------------------------------------------------
// <copyright file="ForgeAuth.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Provider OAuth configuration and retained legacy configuration keys.
/// Operator identity is established by host enrollment and paired sessions.
/// Mode, Secret, AzureAd and BootstrapPoEmail do not grant Forge authority.
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

    /// <summary>Phase 9.2 — override the MSAL public-client id used for ADO sign-in. Defaults to the Azure CLI client id.</summary>
    public string AdoClientId { get; set; } = string.Empty;

    /// <summary>Phase 9.2 — override the AAD authority. Defaults to https://login.microsoftonline.com/common.</summary>
    public string AdoAuthority { get; set; } = string.Empty;

    /// <summary>Phase 9.3 — GitHub OAuth client id (operator-registered OAuth App with PKCE enabled).</summary>
    public string GitHubClientId { get; set; } = string.Empty;

    /// <summary>Phase 9.3 — space- or comma-separated scopes. Default: repo + read:user + read:org.</summary>
    public string GitHubScopes { get; set; } = "repo read:user read:org";

    /// <summary>Phase 9.4 — "edge" (default) or "default". Non-Windows always uses the OS default browser.</summary>
    public string BrowserPreference { get; set; } = "edge";

    /// <summary>Retained legacy setting; no longer used for promotion or enrollment.</summary>
    public string BootstrapPoEmail { get; set; } = string.Empty;
}

/// <summary>Legacy shared-secret middleware, no longer registered in the Forge request pipeline.</summary>
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
