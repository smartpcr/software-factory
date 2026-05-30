// -----------------------------------------------------------------------
// <copyright file="PromotionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.7 — manual / on-demand entry points for the promotion job.
/// The hosted nightly scheduler (one tick per crew timezone) lands in a
/// follow-up; the endpoints below let operators (and tests) drive the
/// runner explicitly.
///
/// <para>
/// Phase 89 — per-agent <b>PO override</b> actions (promote / demote / hold)
/// that wire up the Performance view's <c>DecisionCard</c> buttons. These
/// are explicit overrides on top of the authoritative nightly job: they
/// always carry a stale-level guard (<see cref="OperatorDecisionRequest.ExpectedFromLevel"/>)
/// so two concurrent clicks can't double-apply, and operator-promote rows
/// share the cooldown clock with nightly-job rows
/// (see <see cref="PromotionPolicy.PromotionCountingReasons"/>).
/// </para>
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

        // Phase 89 — per-agent PO override actions.
        app.MapPost("/api/agents/{agentId}/promote", PromoteAgent)
            .WithTags("Promotion")
            .WithName("PromoteAgent");

        app.MapPost("/api/agents/{agentId}/demote", DemoteAgent)
            .WithTags("Promotion")
            .WithName("DemoteAgent");

        app.MapPost("/api/agents/{agentId}/hold", HoldAgent)
            .WithTags("Promotion")
            .WithName("HoldAgent");

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
        ControlDbContext control,
        ProductDbContext db,
        IPromotionRunner runner,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
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

    private static async Task<IResult> GetHistory(string agentId, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
        // SQLite can't sort DateTimeOffset at the SQL layer; sort client-side.
        var rows = (await control.AgentLevelHistory.AsNoTracking()
                .Where(h => h.AgentId == agentId)
                .ToListAsync(ct))
            .OrderByDescending(h => h.CreatedAt)
            .ToList();
        return Results.Ok(rows);
    }

    /// <summary>
    /// Phase 89 — PO override: promote one agent by +1 level. Requires
    /// <see cref="OperatorDecisionRequest.ExpectedFromLevel"/> to match the
    /// agent's current level (otherwise 409 — stale UI). Refuses when the
    /// agent is already at <see cref="ScoringConstants.MaxLevel"/> (400) or
    /// unknown (404). On success, writes an audit row with
    /// <see cref="PromotionPolicy.ReasonOperatorPromote"/> + the caller's
    /// <c>X-Forge-User</c> header into <c>Actor</c>, recomputes hourly_usd,
    /// and broadcasts <c>agent.level.changed</c>.
    /// </summary>
    private static async Task<IResult> PromoteAgent(
        HttpContext ctx,
        string agentId,
        [FromBody] OperatorDecisionRequest? req,
        ControlDbContext control,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
        if (gate is not null) return gate;
        if (req is null) return Results.Problem(title: "Request body is required", statusCode: 400);

        var agent = await control.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        if (agent.Level != req.ExpectedFromLevel)
        {
            return Results.Problem(
                title: "agent.level.stale",
                detail: $"Expected agent at L{req.ExpectedFromLevel} but current level is L{agent.Level}. Refresh the page and retry.",
                statusCode: 409);
        }

        if (agent.Level >= ScoringConstants.MaxLevel)
        {
            return Results.Problem(
                title: "agent.level.at_cap",
                detail: $"Agent is already at the maximum level (L{ScoringConstants.MaxLevel}). Cannot promote further.",
                statusCode: 400);
        }

        var oldLevel = agent.Level;
        var newLevel = oldLevel + 1;
        var actor = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var at = DateTimeOffset.UtcNow;

        agent.Level = newLevel;
        agent.LevelUpdatedAt = at;
        agent.HourlyUsd = ScoringConstants.HourlyForLevel(newLevel);

        control.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = agentId,
            OldLevel = oldLevel,
            NewLevel = newLevel,
            WindowAvg = req.WindowAvg ?? 0m,
            Reason = PromotionPolicy.ReasonOperatorPromote,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor,
            CreatedAt = at,
        });
        await control.SaveChangesAsync(ct);

        var payload = new OperatorDecisionResult(
            AgentId: agentId, OldLevel: oldLevel, NewLevel: newLevel,
            HourlyUsd: agent.HourlyUsd,
            Reason: PromotionPolicy.ReasonOperatorPromote,
            Actor: actor, At: at);
        await hub.Clients.All.SendAsync("agent.level.changed", payload, ct);
        return Results.Ok(payload);
    }

    /// <summary>
    /// Phase 89 — PO override: demote one agent by 1 level. Same stale-level
    /// guard as promote. Refuses when at <see cref="ScoringConstants.MinLevel"/>
    /// (400). <b>Mirrors the Q14.5 lead-floor rule</b>: if the agent currently
    /// leads a crew and the new level would drop below
    /// <see cref="ScoringConstants.LeadMinLevel"/>, returns
    /// <c>409 crew.lead.level_too_low</c> — the operator must reassign the
    /// crew lead first.
    /// </summary>
    private static async Task<IResult> DemoteAgent(
        HttpContext ctx,
        string agentId,
        [FromBody] OperatorDecisionRequest? req,
        ControlDbContext control,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
        if (gate is not null) return gate;
        if (req is null) return Results.Problem(title: "Request body is required", statusCode: 400);

        var agent = await control.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        if (agent.Level != req.ExpectedFromLevel)
        {
            return Results.Problem(
                title: "agent.level.stale",
                detail: $"Expected agent at L{req.ExpectedFromLevel} but current level is L{agent.Level}. Refresh the page and retry.",
                statusCode: 409);
        }

        if (agent.Level <= ScoringConstants.MinLevel)
        {
            return Results.Problem(
                title: "agent.level.at_floor",
                detail: $"Agent is already at the minimum level (L{ScoringConstants.MinLevel}). Cannot demote further.",
                statusCode: 400);
        }

        var oldLevel = agent.Level;
        var newLevel = oldLevel - 1;

        if (newLevel < ScoringConstants.LeadMinLevel)
        {
            var crewLed = await control.Crews
                .Where(c => c.Lead == agentId)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            if (crewLed is not null)
            {
                return Results.Problem(
                    title: "crew.lead.level_too_low",
                    detail: $"Agent {agentId} leads crew {crewLed}. Demoting to L{newLevel} (< {ScoringConstants.LeadMinLevel}) would orphan the crew. Reassign the crew lead before demoting.",
                    statusCode: 409);
            }
        }

        var actor = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var at = DateTimeOffset.UtcNow;

        agent.Level = newLevel;
        agent.LevelUpdatedAt = at;
        agent.HourlyUsd = ScoringConstants.HourlyForLevel(newLevel);

        control.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = agentId,
            OldLevel = oldLevel,
            NewLevel = newLevel,
            WindowAvg = req.WindowAvg ?? 0m,
            Reason = PromotionPolicy.ReasonOperatorDemote,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor,
            CreatedAt = at,
        });
        await control.SaveChangesAsync(ct);

        var payload = new OperatorDecisionResult(
            AgentId: agentId, OldLevel: oldLevel, NewLevel: newLevel,
            HourlyUsd: agent.HourlyUsd,
            Reason: PromotionPolicy.ReasonOperatorDemote,
            Actor: actor, At: at);
        await hub.Clients.All.SendAsync("agent.level.changed", payload, ct);
        return Results.Ok(payload);
    }

    /// <summary>
    /// Phase 89 — PO override: <b>audit-only hold</b>. Writes a same-level
    /// history row (<see cref="PromotionPolicy.ReasonOperatorHold"/>) so the
    /// operator's awareness is on the record, but does <b>NOT</b> mutate the
    /// agent and does <b>NOT</b> suppress the next nightly tick. v0.1
    /// limitation; teaching the runner to honor an operator-hold window is a
    /// follow-up. Broadcasts <c>agent.promotion.acknowledged</c> (NOT
    /// <c>agent.level.changed</c>, since nothing changed).
    /// </summary>
    private static async Task<IResult> HoldAgent(
        HttpContext ctx,
        string agentId,
        [FromBody] OperatorDecisionRequest? req,
        ControlDbContext control,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var agent = await control.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        var actor = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var at = DateTimeOffset.UtcNow;

        control.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = agentId,
            OldLevel = agent.Level,
            NewLevel = agent.Level,
            WindowAvg = req?.WindowAvg ?? 0m,
            Reason = PromotionPolicy.ReasonOperatorHold,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor,
            CreatedAt = at,
        });
        await control.SaveChangesAsync(ct);

        var payload = new OperatorDecisionResult(
            AgentId: agentId, OldLevel: agent.Level, NewLevel: agent.Level,
            HourlyUsd: agent.HourlyUsd,
            Reason: PromotionPolicy.ReasonOperatorHold,
            Actor: actor, At: at);
        await hub.Clients.All.SendAsync("agent.promotion.acknowledged", payload, ct);
        return Results.Ok(payload);
    }
}

/// <summary>
/// Phase 89 — request body for the per-agent PO override endpoints.
/// <see cref="ExpectedFromLevel"/> is the stale-decision guard: the server
/// 409s if the agent's current level no longer matches what the UI was
/// showing when the operator clicked. <see cref="WindowAvg"/> is the
/// trailing-window average the UI was displaying (recorded into the audit
/// row's <see cref="AgentLevelHistoryRow.WindowAvg"/> for traceability —
/// not used for any decision).
/// </summary>
/// <param name="ExpectedFromLevel">The level the UI believes the agent is at right now.</param>
/// <param name="WindowAvg">Optional — trailing-window avg score from the UI, stored on the audit row.</param>
public sealed record OperatorDecisionRequest(int ExpectedFromLevel, decimal? WindowAvg = null);

/// <summary>
/// Phase 89 — response from the per-agent PO override endpoints. Also the
/// payload broadcast on <c>agent.level.changed</c> and
/// <c>agent.promotion.acknowledged</c>.
/// </summary>
public sealed record OperatorDecisionResult(
    string AgentId,
    int OldLevel,
    int NewLevel,
    decimal HourlyUsd,
    string Reason,
    string? Actor,
    DateTimeOffset At);
