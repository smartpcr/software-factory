// -----------------------------------------------------------------------
// <copyright file="WebhooksEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.IO;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 5.8 — webhook receivers for ADO Service Hooks and GitHub.
/// Bodies are not parsed in detail today; the receiver just (a) validates
/// the signature, (b) marks the provider's webhook activity, and
/// (c) kicks an immediate provider sync so the next poll round arrives
/// instantly via SignalR.
/// </summary>
public static class WebhooksEndpoints
{
    public static WebApplication MapWebhooksEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhooks/github", HandleGitHub).WithTags("Webhooks").WithName("GitHubWebhook");
        app.MapPost("/api/webhooks/ado", HandleAdo).WithTags("Webhooks").WithName("AdoWebhook");
        return app;
    }

    private static async Task<IResult> HandleGitHub(
        HttpContext ctx,
        IOptions<GitHubOptions> ghOpts,
        WebhookActivity activity,
        IEnumerable<IForgeProvider> providers,
        ProviderSyncService sync,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("forge.webhook.github");
        var secret = ghOpts.Value.WebhookSecret ?? string.Empty;

        // Read body once for HMAC verification.
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var raw = ms.ToArray();

        var sigHeader = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
        if (!string.IsNullOrEmpty(secret) &&
            !WebhookSignature.VerifyGitHub(sigHeader, secret, raw))
        {
            logger.LogWarning("github webhook rejected: bad signature");
            return Results.Unauthorized();
        }

        activity.Mark("github");
        var ghProvider = providers.FirstOrDefault(p => p.Kind == "github");
        if (ghProvider is not null)
        {
            // Fire and forget — let the sync service drive the actual upserts.
            _ = Task.Run(() => sync.RunOneAsync(ghProvider, CancellationToken.None));
        }

        var eventName = ctx.Request.Headers["X-GitHub-Event"].ToString();
        logger.LogInformation("github webhook received event={Event} bytes={Bytes}", eventName, raw.Length);
        return Results.Ok(new { ok = true, kicked = ghProvider is not null });
    }

    private static async Task<IResult> HandleAdo(
        HttpContext ctx,
        IOptions<AdoOptions> adoOpts,
        WebhookActivity activity,
        IEnumerable<IForgeProvider> providers,
        ProviderSyncService sync,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("forge.webhook.ado");
        var secret = adoOpts.Value.WebhookSecret ?? string.Empty;
        var headerSecret = ctx.Request.Headers["X-Forge-Hook-Secret"].ToString();

        if (!string.IsNullOrEmpty(secret) &&
            !WebhookSignature.VerifyAdo(headerSecret, secret))
        {
            logger.LogWarning("ado webhook rejected: bad shared secret");
            return Results.Unauthorized();
        }

        // Discard body — Phase 5 doesn't need to parse the event payload.
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);

        activity.Mark("ado");
        var adoProvider = providers.FirstOrDefault(p => p.Kind == "ado");
        if (adoProvider is not null)
        {
            _ = Task.Run(() => sync.RunOneAsync(adoProvider, CancellationToken.None));
        }

        logger.LogInformation("ado webhook received bytes={Bytes}", ms.Length);
        return Results.Ok(new { ok = true, kicked = adoProvider is not null });
    }
}
