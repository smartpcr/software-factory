// -----------------------------------------------------------------------
// <copyright file="AgentAssignmentsEndpoints.cs" company="Microsoft Corp.">
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
/// Phase 22.4 — endpoints for the <c>AssignedAgent</c> field and the
/// <c>agent_assignments</c> history table.
///
/// <list type="bullet">
///   <item><c>GET  /api/work-items/{id}/assigned-agent</c> — current runtime assignment.</item>
///   <item><c>GET  /api/work-items/{id}/assignments</c> — paginated history.</item>
///   <item><c>POST /api/work-items/{id}/assignments</c> — manual override (PO/lead).</item>
///   <item><c>DELETE /api/work-items/{id}/assigned-agent</c> — clear (PO only).</item>
/// </list>
/// </summary>
public static class AgentAssignmentsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "generator", "evaluator", "reviewer", "judge", "manual",
    };

    public static WebApplication MapAgentAssignmentsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/work-items").WithTags("WorkItems");

        grp.MapGet("/{id}/assigned-agent", GetAssignedAgent)
            .WithName("GetAssignedAgent");
        grp.MapGet("/{id}/assignments", GetAssignmentHistory)
            .WithName("GetAssignmentHistory");
        grp.MapPost("/{id}/assignments", PostAssignment)
            .WithName("PostAssignment");
        grp.MapDelete("/{id}/assigned-agent", DeleteAssignedAgent)
            .WithName("DeleteAssignedAgent");

        return app;
    }

    // ── GET /api/work-items/{id}/assigned-agent ─────────────────────────

    private static async Task<IResult> GetAssignedAgent(
        string id,
        ProductDbContext db,
        CancellationToken ct)
    {
        var row = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        if (string.IsNullOrWhiteSpace(row.AssignedAgentJson))
            return Results.NoContent();

        try
        {
            var agent = JsonSerializer.Deserialize<AssignedAgent>(row.AssignedAgentJson, JsonOpts);
            return Results.Ok(agent);
        }
        catch
        {
            // Malformed JSON — treat as null.
            return Results.NoContent();
        }
    }

    // ── GET /api/work-items/{id}/assignments ────────────────────────────

    private static async Task<IResult> GetAssignmentHistory(
        string id,
        [FromQuery] int? limit,
        ProductDbContext db,
        CancellationToken ct)
    {
        var exists = await db.WorkItems.AsNoTracking().AnyAsync(w => w.Id == id, ct);
        if (!exists) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // SQLite doesn't support DateTimeOffset in ORDER BY; materialize then sort client-side.
        var allRows = await db.AgentAssignments.AsNoTracking()
            .Where(a => a.WorkItemId == id)
            .ToListAsync(ct);

        var rows = allRows
            .OrderByDescending(a => a.At)
            .Take(effectiveLimit)
            .ToList();

        var entries = rows.Select(MapEntry).ToList();
        return Results.Ok(entries);
    }

    // ── POST /api/work-items/{id}/assignments ───────────────────────────

    private sealed record PostAssignmentRequest(
        IReadOnlyList<string> Agents,
        string Role,
        string? Team = null,
        string? CrewId = null,
        string? Reason = null);

    private static async Task<IResult> PostAssignment(
        HttpContext ctx,
        string id,
        [FromBody] PostAssignmentRequest req,
        ProductDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req.Agents is null || req.Agents.Count == 0)
            return Results.Problem(title: "agents[] must contain at least one entry", statusCode: 400);

        if (string.IsNullOrWhiteSpace(req.Role) || !ValidRoles.Contains(req.Role))
            return Results.Problem(title: $"Invalid role '{req.Role}'. Valid: {string.Join(", ", ValidRoles)}", statusCode: 400);

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Validate agent IDs exist.
        var allAgents = await db.Agents.AsNoTracking()
            .Select(a => a.Id)
            .ToListAsync(ct);
        var unknown = req.Agents.Where(a => !allAgents.Contains(a)).ToList();
        if (unknown.Count > 0)
            return Results.Problem(title: $"Unknown agent(s): {string.Join(", ", unknown)}", statusCode: 400);

        var userId = ctx.Request.Headers.TryGetValue(ForgeUser.HeaderName, out var uv) ? uv.FirstOrDefault() : null;
        var now = DateTimeOffset.UtcNow;

        // Determine history kind based on role.
        var kind = req.Role is "reviewer"
            ? AgentAssignmentKinds.ReviewerAssigned
            : AgentAssignmentKinds.Reassigned;

        // Write the field.
        var assignedAgent = new
        {
            agents = req.Agents,
            team = req.Team,
            crewId = req.CrewId,
            role = req.Role,
            assignedAt = now.ToString("o"),
            assignedBy = userId,
        };
        row.AssignedAgentJson = JsonSerializer.Serialize(assignedAgent, JsonOpts);
        row.UpdatedAt = now;

        // Append history.
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = id,
            Kind = kind,
            AgentIdsJson = JsonSerializer.Serialize(req.Agents, JsonOpts),
            Team = req.Team,
            CrewId = req.CrewId,
            Role = req.Role,
            AssignedBy = userId,
            Reason = req.Reason,
            At = now,
        });

        await db.SaveChangesAsync(ct);
        return Results.Ok(JsonSerializer.Deserialize<AssignedAgent>(row.AssignedAgentJson, JsonOpts));
    }

    // ── DELETE /api/work-items/{id}/assigned-agent ──────────────────────

    private sealed record DeleteAssignedAgentRequest(string? Reason = null);

    private static async Task<IResult> DeleteAssignedAgent(
        HttpContext ctx,
        string id,
        ProductDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Parse optional body (reason).
        string? reason = null;
        try
        {
            ctx.Request.EnableBuffering();
            ctx.Request.Body.Position = 0;
            var body = await JsonSerializer.DeserializeAsync<DeleteAssignedAgentRequest>(ctx.Request.Body, JsonOpts, ct);
            reason = body?.Reason;
        }
        catch { /* no body or malformed — fine */ }

        var agentIds = WorkItemsEndpoints.ParseAgentIds(row.AssignedAgentJson);
        var userId = ctx.Request.Headers.TryGetValue(ForgeUser.HeaderName, out var uv) ? uv.FirstOrDefault() : null;

        // Append history (even if already null — audit completeness).
        db.AgentAssignments.Add(new AgentAssignmentRow
        {
            WorkItemId = id,
            Kind = AgentAssignmentKinds.ClearedManual,
            AgentIdsJson = JsonSerializer.Serialize(agentIds, JsonOpts),
            AssignedBy = userId,
            Reason = reason,
            At = DateTimeOffset.UtcNow,
        });

        row.AssignedAgentJson = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { cleared = true });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentAssignmentEntry MapEntry(AgentAssignmentRow r)
    {
        List<string> agentIds;
        try { agentIds = JsonSerializer.Deserialize<List<string>>(r.AgentIdsJson, JsonOpts) ?? new(); }
        catch { agentIds = new(); }

        return new AgentAssignmentEntry(
            r.Id, r.WorkItemId, r.Kind, agentIds,
            r.Team, r.CrewId, r.Role,
            r.FromExecutionStatus, r.ToExecutionStatus,
            r.AssignedBy, r.Reason, r.At);
    }
}
