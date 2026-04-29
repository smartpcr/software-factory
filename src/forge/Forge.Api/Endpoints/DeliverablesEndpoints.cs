// -----------------------------------------------------------------------
// <copyright file="DeliverablesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.5 — deliverable read + dispute endpoints. Reads are open to
/// any signed-in role (so the score panel can render); the dispute path
/// is gated to the deliverable's <c>generatorAgentId</c> or that agent's
/// crew lead, and refuses if a judge has already scored the row.
/// </summary>
public static class DeliverablesEndpoints
{
    public static WebApplication MapDeliverablesEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/deliverables").WithTags("Deliverables");
        grp.MapGet("/{id:long}", Get).WithName("GetDeliverable");
        grp.MapPost("/{id:long}/dispute", Dispute).WithName("DisputeDeliverable");

        // Convenience: list all iterations for a work item.
        app.MapGet("/api/work-items/{workItemId}/deliverables", ListByWorkItem)
            .WithTags("Deliverables")
            .WithName("ListDeliverablesForWorkItem");

        return app;
    }

    private static async Task<IResult> Get(long id, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.Deliverables.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Deliverable {id} not found", statusCode: 404);
        return Results.Ok(MapToDto(row));
    }

    private static async Task<IResult> ListByWorkItem(string workItemId, ForgeDbContext db, CancellationToken ct)
    {
        var rows = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync(ct))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Iter)
            .ToList();
        return Results.Ok(rows.Select(MapToDto));
    }

    /// <summary>
    /// Phase 14.5 — submit a judge re-score against a deliverable.
    /// <para>
    /// Body: <c>{ judgeAgentId, judgeScore (0..100), judgeNotes }</c>.
    /// One dispute per row — repeat calls return 409. The caller must be
    /// the deliverable's generator agent OR the lead of that agent's crew
    /// (Q14.5 lead-gate). On <c>judgeScore ≥ 90</c> the linked work item
    /// flips to <c>closed</c> retroactively.
    /// </para>
    /// </summary>
    private static async Task<IResult> Dispute(
        long id,
        [FromBody] DeliverableDisputeRequest req,
        HttpContext ctx,
        ForgeDbContext db,
        CancellationToken ct)
    {
        if (req is null) return Results.Problem(title: "Body is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.JudgeAgentId))
        {
            return Results.Problem(title: "judgeAgentId is required", statusCode: 400);
        }
        if (req.JudgeScore < 0 || req.JudgeScore > 100)
        {
            return Results.Problem(title: "judgeScore must be 0..100", statusCode: 400);
        }

        var row = await db.Deliverables.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Deliverable {id} not found", statusCode: 404);

        // One dispute per deliverable.
        if (!string.IsNullOrEmpty(row.JudgeAgentId))
        {
            return Results.Problem(
                type: "deliverable.already_disputed",
                title: $"Deliverable {id} has already been judged by '{row.JudgeAgentId}' (score {row.JudgeScore}).",
                statusCode: 409);
        }

        // Authority: caller must be the generator agent OR the lead of that
        // agent's crew. Single-user-dev pass-through (RequireRole = null) is
        // tolerated to match the existing assign / patch endpoints' style.
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == row.CrewId, ct);
        var role = RequireRole.ResolveRoleForProduct(ctx, db, crew?.ProductId);
        var isPo = role is null || string.Equals(role, RequireRole.ProductOwner, StringComparison.OrdinalIgnoreCase);
        if (role is not null)
        {
            var callerUser = string.IsNullOrEmpty(caller) ? null
                : await db.Users.AsNoTracking()
                    .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
                    .Select(u => new { u.Id })
                    .FirstOrDefaultAsync(ct);
            var callerAgentId = callerUser is null ? null
                : await db.Agents.AsNoTracking()
                    .Where(a => a.Id == callerUser.Id)
                    .Select(a => (string?)a.Id)
                    .FirstOrDefaultAsync(ct);
            var isGenerator = !string.IsNullOrEmpty(callerAgentId) && callerAgentId == row.GeneratorAgentId;
            var isLead = crew?.Lead is not null && callerAgentId == crew.Lead;
            if (!isGenerator && !isLead && !isPo)
            {
                return Results.Problem(
                    type: "deliverable.dispute_forbidden",
                    title: "Forbidden — only the deliverable's generator agent, the crew lead, or a product-owner can dispute",
                    statusCode: 403);
            }
        }

        // Validate the judge agent exists.
        var judgeExists = await db.Agents.AsNoTracking().AnyAsync(a => a.Id == req.JudgeAgentId, ct);
        if (!judgeExists)
        {
            return Results.Problem(title: $"Agent '{req.JudgeAgentId}' not found", statusCode: 400);
        }

        // Apply the dispute.
        row.JudgeAgentId = req.JudgeAgentId;
        row.JudgeScore = req.JudgeScore;
        row.JudgeNotes = (req.JudgeNotes ?? string.Empty).Length > 800
            ? (req.JudgeNotes ?? string.Empty).Substring(0, 800)
            : req.JudgeNotes ?? string.Empty;

        // Retroactive pass — judge ≥ 90 flips the linked work item to closed.
        var retroPass = req.JudgeScore >= ScoringConstants.PassThreshold;
        if (retroPass)
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
            if (wi is not null && !string.Equals(wi.State, "closed", StringComparison.OrdinalIgnoreCase))
            {
                wi.State = "closed";
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(MapToDto(row));
    }

    private static Deliverable MapToDto(DeliverableRow r)
    {
        Dictionary<string, int>? dims = null;
        try
        {
            dims = string.IsNullOrWhiteSpace(r.DimsJson)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, int>>(r.DimsJson);
        }
        catch (JsonException) { dims = null; }

        return new Deliverable(
            Id: r.Id,
            WorkItemId: r.WorkItemId,
            CrewId: r.CrewId,
            GeneratorAgentId: r.GeneratorAgentId,
            EvaluatorAgentId: r.EvaluatorAgentId,
            Iter: r.Iter,
            Score: r.Score,
            Dims: dims ?? new Dictionary<string, int>(),
            Verdict: r.Verdict,
            Notes: r.Notes,
            GenCostUsd: r.GenCostUsd,
            EvalCostUsd: r.EvalCostUsd,
            JudgeAgentId: r.JudgeAgentId,
            JudgeScore: r.JudgeScore,
            JudgeNotes: r.JudgeNotes,
            CreatedAt: r.CreatedAt);
    }
}
