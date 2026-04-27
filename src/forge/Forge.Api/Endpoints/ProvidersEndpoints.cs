// -----------------------------------------------------------------------
// <copyright file="ProvidersEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Phase 5.4 + 5.11 — provider config + health.
/// Read paths: GET /api/providers (lists kinds + configured + last-known health)
/// and GET /api/providers/{kind}/test (live probe).
/// </summary>
public static class ProvidersEndpoints
{
    public static WebApplication MapProvidersEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/providers").WithTags("Providers");
        grp.MapGet("/", List).WithName("ListProviders");
        grp.MapGet("/{kind}/test", Test).WithName("TestProvider");
        grp.MapGet("/{kind}/repos", ListRepos).WithName("ListProviderRepos");
        grp.MapPost("/{kind}/sync", SyncNow).WithName("SyncProviderNow");
        return app;
    }

    // POST /api/providers/{kind}/sync — manual trigger for a full upsert run.
    // Mirrors what `ProviderSyncService` does on the 60s background poll, but
    // bypasses the polling cadence so the operator can pull on demand.
    private static async Task<IResult> SyncNow(
        HttpContext ctx,
        string kind,
        [FromServices] IEnumerable<IForgeProvider> providers,
        [FromServices] ProviderSyncService sync,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var p = providers.FirstOrDefault(x => x.Kind == kind);
        if (p is null) return Results.Problem(title: $"Unknown provider {kind}", statusCode: 404);
        if (!p.IsConfigured)
        {
            return Results.Problem(
                title: $"Provider {kind} is not configured",
                detail: $"Set Forge:{(kind == "ado" ? "Ado" : "GitHub")}:* secrets and try again.",
                statusCode: 400);
        }

        await sync.RunOneAsync(p, ct);
        // Return the freshly-updated health snapshot so the UI can render
        // last-sync / error-count / rate-limit without a second round-trip.
        return Results.Ok(await p.TestConnectionAsync(ct));
    }

    // GET /api/providers/{kind}/repos — Phase 8.5 server-driven add-repo picker.
    private static async Task<IResult> ListRepos(string kind, [FromServices] IEnumerable<IForgeProvider> providers, CancellationToken ct)
    {
        var p = providers.FirstOrDefault(x => x.Kind == kind);
        if (p is null) return Results.Problem(title: $"Unknown provider {kind}", statusCode: 404);
        var repos = await p.ListRepositoriesAsync(ct);
        return Results.Ok(repos);
    }

    private static async Task<IResult> List([FromServices] IEnumerable<IForgeProvider> providers, CancellationToken ct)
    {
        var snapshots = new List<ProviderHealth>();
        foreach (var p in providers)
        {
            // TestConnectionAsync returns a snapshot when not configured too.
            snapshots.Add(await p.TestConnectionAsync(ct));
        }
        return Results.Ok(snapshots);
    }

    private static async Task<IResult> Test(string kind, [FromServices] IEnumerable<IForgeProvider> providers, CancellationToken ct)
    {
        var p = providers.FirstOrDefault(x => x.Kind == kind);
        if (p is null) return Results.Problem(title: $"Unknown provider {kind}", statusCode: 404);
        return Results.Ok(await p.TestConnectionAsync(ct));
    }
}
