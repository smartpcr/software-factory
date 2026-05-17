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
/// Phase B + F — admin endpoints for the multi-DB split tooling.
///
/// <para>All endpoints are gated by:</para>
/// <list type="bullet">
///   <item>The <c>Forge:Db:SplitEnabled</c> config flag (default <c>false</c>).
///         Endpoints return <c>403</c> when the flag is off so an unguarded
///         operator click can't trigger the analyzer / apply in production.</item>
///   <item><see cref="RequireRole.ProductOwner"/>. Non-PO callers see <c>403</c>.</item>
/// </list>
///
/// <para>The dry-run is the Phase B analyzer; the apply is Phase F's
/// actual split operation. Operators inspect the dry-run report (Phase C)
/// before invoking the apply.</para>
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
            ControlDbContext control,
            ProductDbContext db,
            SplitDryRunService analyzer,
            CancellationToken ct) =>
        {
            if (RequireRole.Check(ctx, control, RequireRole.ProductOwner) is { } gate) return gate;

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

        // POST /api/admin/db/split-apply — Phase F. WRITES to forge.db
        // and creates per-product {slug}.db files. Backs up first. Idempotent
        // (returns AlreadyApplied=true on subsequent runs). Operator-only
        // and feature-flag-gated; do NOT run without reviewing the dry-run
        // report first (Phase C).
        app.MapPost("/api/admin/db/split-apply", async (
            HttpContext ctx,
            IConfiguration config,
            ControlDbContext control,
            ProductDbContext db,
            SplitApplyService applier,
            CancellationToken ct) =>
        {
            if (RequireRole.Check(ctx, control, RequireRole.ProductOwner) is { } gate) return gate;

            if (!IsEnabled(config))
            {
                return Results.Problem(
                    title: "Split tooling disabled",
                    detail: "Set Forge:Db:SplitEnabled=true to enable.",
                    statusCode: 403);
            }

            var result = await applier.ApplyAsync(ct);
            return Results.Ok(result);
        })
            .WithTags("Admin")
            .WithName("AdminDbSplitApply");

        return app;
    }

    private static bool IsEnabled(IConfiguration config)
    {
        var raw = config["Forge:Db:SplitEnabled"];
        return bool.TryParse(raw, out var v) && v;
    }
}
