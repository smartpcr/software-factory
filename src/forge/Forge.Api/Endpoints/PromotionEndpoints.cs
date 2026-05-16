// -----------------------------------------------------------------------
// <copyright file="PromotionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.7 — manual / on-demand entry points for the promotion job.
/// The hosted nightly scheduler (one tick per crew timezone) lands in a
/// follow-up; the endpoints below let operators (and tests) drive the
/// runner explicitly.
/// </summary>
public static class PromotionEndpoints
{
    public static WebApplication MapPromotionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/promotion/run", Run)
            .WithTags("Promotion")
            .WithName("RunPromotionJob");

        app.MapGet("/api/agents/{agentId}/level-history", GetHistory)
            .WithTags("Promotion")
            .WithName("GetAgentLevelHistory");

        return app;
    }

    /// <summary>
    /// Run the promotion algorithm across every agent. Gated to
    /// <c>RequireRole.ProductOwner</c> at the global scope (no product id
    /// — the job operates on the whole DB). Single-user-dev pass-through
    /// (no users in DB) lets the endpoint be hit without auth, matching
    /// the rest of the admin surface.
    /// </summary>
    private static async Task<IResult> Run(
        HttpContext ctx,
        ProductDbContext db,
        IPromotionRunner runner,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var result = await runner.RunAsync(ct);

        var resp = new PromotionRunResponse(
            AgentsScanned: result.AgentsScanned,
            Promoted: result.Promoted,
            Demoted: result.Demoted,
            At: result.At,
            Decisions: result.Decisions
                .Select(d => new PromotionDecision(d.AgentId, d.OldLevel, d.NewLevel, d.RollingAvg, d.Reason, d.WindowSize))
                .ToList());
        return Results.Ok(resp);
    }

    private static async Task<IResult> GetHistory(string agentId, ProductDbContext db, CancellationToken ct)
    {
        // SQLite can't sort DateTimeOffset at the SQL layer; sort client-side.
        var rows = (await db.AgentLevelHistory.AsNoTracking()
                .Where(h => h.AgentId == agentId)
                .ToListAsync(ct))
            .OrderByDescending(h => h.CreatedAt)
            .ToList();
        return Results.Ok(rows);
    }
}
