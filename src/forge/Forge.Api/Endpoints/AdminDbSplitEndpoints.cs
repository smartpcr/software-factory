// -----------------------------------------------------------------------
// <copyright file="AdminDbSplitEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Phase B — admin endpoints for the multi-DB split tooling.
///
/// <para>All endpoints are gated by:</para>
/// <list type="bullet">
///   <item>The <c>Forge:Db:SplitEnabled</c> config flag (default <c>false</c>).
///         Endpoints return <c>403</c> when the flag is off so an unguarded
///         operator click can't trigger the analyzer in production.</item>
///   <item><see cref="RequireRole.ProductOwner"/>. Non-PO callers see <c>403</c>.</item>
/// </list>
///
/// <para>Today only the dry-run is shipped; the split-apply lands in
/// Phase F together with the auto-split-on-first-boot migration.</para>
/// </summary>
public static class AdminDbSplitEndpoints
{
    public static WebApplication MapAdminDbSplitEndpoints(this WebApplication app)
    {
        // POST /api/admin/db/split-dry-run — read-only; returns the
        // partition/orphan report. Idempotent.
        app.MapPost("/api/admin/db/split-dry-run", async (
            HttpContext ctx,
            IConfiguration config,
            ProductDbContext db,
            SplitDryRunService analyzer,
            CancellationToken ct) =>
        {
            if (RequireRole.Check(ctx, db, RequireRole.ProductOwner) is { } gate) return gate;

            if (!IsEnabled(config))
            {
                return Results.Problem(
                    title: "Split tooling disabled",
                    detail: "Set Forge:Db:SplitEnabled=true in appsettings.json (or via env) to enable the multi-DB split tooling. This guards the analyzer from being triggered accidentally.",
                    statusCode: 403);
            }

            var report = await analyzer.AnalyzeAsync(ct);
            return Results.Ok(report);
        })
            .WithTags("Admin")
            .WithName("AdminDbSplitDryRun");

        return app;
    }

    private static bool IsEnabled(IConfiguration config)
    {
        var raw = config["Forge:Db:SplitEnabled"];
        return bool.TryParse(raw, out var v) && v;
    }
}
