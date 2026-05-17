// -----------------------------------------------------------------------
// <copyright file="QueueEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
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
/// Phase 17.5 — REST surface for the persistent task queue. List + ack +
/// route + reassign + cancel + strain-confirm; convenience endpoints for
/// per-team, per-crew, and per-crew-stats queries.
/// </summary>
public static class QueueEndpoints
{
    public static WebApplication MapQueueEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/queue").WithTags("Queue");
        grp.MapGet("/", List).WithName("ListQueue");
        grp.MapGet("/{id:long}", Get).WithName("GetQueueRow");
        grp.MapGet("/{id:long}/history", GetHistory).WithName("GetQueueHistory");
        grp.MapPost("/{id:long}/ack", Acknowledge).WithName("AcknowledgeQueueRow");
        grp.MapPost("/{id:long}/route", Route).WithName("RouteQueueRow");
        grp.MapPost("/{id:long}/reassign", Reassign).WithName("ReassignQueueRow");
        grp.MapPost("/{id:long}/cancel", Cancel).WithName("CancelQueueRow");
        grp.MapPost("/{id:long}/strain-confirm", ConfirmStrain).WithName("ConfirmQueueStrain");

        // Convenience routes
        app.MapGet("/api/teams/{teamId}/queue", ListForTeam).WithTags("Queue").WithName("ListTeamQueue");
        app.MapGet("/api/crews/{crewId}/queue-stats", GetCrewStats).WithTags("Queue").WithName("GetCrewQueueStats");
        app.MapGet("/api/crews/{crewId}/queue", ListForCrew).WithTags("Queue").WithName("ListCrewQueue");
        return app;
    }

    private static async Task<IResult> List(
        ControlDbContext control,
        ProductDbContext db,
        string? targetKind,
        string? targetId,
        string? state,
        CancellationToken ct)
    {
        IQueryable<TaskQueueRow> q = db.TaskQueue.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(targetKind)) q = q.Where(r => r.TargetKind == targetKind);
        if (!string.IsNullOrWhiteSpace(targetId))   q = q.Where(r => r.TargetId == targetId);
        if (!string.IsNullOrWhiteSpace(state))
        {
            var states = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            q = q.Where(r => states.Contains(r.State));
        }
        else
        {
            // Default — open states.
            var open = TaskQueueStates.OpenStates.ToList();
            q = q.Where(r => open.Contains(r.State));
        }
        var rows = await q.ToListAsync(ct);
        var ordered = rows.OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt).ToList();
        return Results.Ok(ordered.Select(MapRow).ToList());
    }

    private static async Task<IResult> Get(long id, ITaskQueueService queue, CancellationToken ct)
    {
        var row = await queue.GetAsync(id, ct);
        return row is null
            ? Results.Problem(title: $"Queue row {id} not found", statusCode: 404)
            : Results.Ok(MapRow(row));
    }

    private static async Task<IResult> GetHistory(long id, ITaskQueueService queue, CancellationToken ct)
    {
        var rows = await queue.GetHistoryAsync(id, ct);
        return Results.Ok(rows.Select(MapHistory).ToList());
    }

    private static async Task<IResult> Acknowledge(
        HttpContext ctx,
        long id,
        ITaskQueueService queue,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var row = await queue.AcknowledgeAsync(id, caller, auto: false, ct);
        if (row is null) return Results.Problem(title: $"Queue row {id} not in awaiting-ack", statusCode: 409);
        await BroadcastChangedAsync(hub, row, "acked", ct);
        return Results.Ok(MapRow(row));
    }

    private static async Task<IResult> Route(
        HttpContext ctx,
        long id,
        [FromBody] RouteQueueRequest req,
        ITaskQueueService queue,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.CrewId))
        {
            return Results.Problem(title: "crewId required", statusCode: 400);
        }
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var row = await queue.RouteToCrewAsync(id, req.CrewId, caller, ct);
        if (row is null) return Results.Problem(title: $"Queue row {id} not in awaiting-route", statusCode: 409);
        await BroadcastChangedAsync(hub, row, "routed", ct);
        return Results.Ok(MapRow(row));
    }

    private static async Task<IResult> Reassign(
        HttpContext ctx,
        long id,
        [FromBody] ReassignQueueRequest req,
        ITaskQueueService queue,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.NewCrewId))
        {
            return Results.Problem(title: "newCrewId required", statusCode: 400);
        }
        // Authority: lead of the source-crew or PO of its product.
        var existing = await queue.GetAsync(id, ct);
        if (existing is null) return Results.Problem(title: $"Queue row {id} not found", statusCode: 404);
        var gateError = await CheckLeadOrPoForCrewAsync(ctx, control, db, existing.RoutedToCrewId, ct);
        if (gateError is not null) return gateError;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "system";
        var row = await queue.ReassignAsync(id, req.NewCrewId, caller, ct);
        if (row is null) return Results.Problem(title: $"Queue row {id} not in a reassignable state", statusCode: 409);
        await BroadcastChangedAsync(hub, row, "reassigned", ct);
        return Results.Ok(MapRow(row));
    }

    private static async Task<IResult> Cancel(
        HttpContext ctx,
        long id,
        [FromBody] CancelQueueRequest? req,
        ITaskQueueService queue,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var existing = await queue.GetAsync(id, ct);
        if (existing is null) return Results.Problem(title: $"Queue row {id} not found", statusCode: 404);
        var gateError = await CheckLeadOrPoForCrewAsync(ctx, control, db, existing.RoutedToCrewId, ct);
        if (gateError is not null) return gateError;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "system";
        var row = await queue.CancelAsync(id, caller, req?.Reason, ct);
        if (row is null) return Results.Problem(title: $"Queue row {id} already closed", statusCode: 409);
        await BroadcastChangedAsync(hub, row, "cancelled", ct);

        // Phase 19 — also fan out workitem.changed so the ExecBadge updates
        // immediately. CancelAsync flips ExecutionStatus to "cancelled".
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
        if (wi is not null)
        {
            var (prId, prCol) = await WorkItemsEndpoints.LookupPrimaryPrAsync(db, row.WorkItemId, ct);
            await hub.Clients.All.SendAsync(
                "workitem.changed",
                new { kind = "exec-cancelled", workItem = WorkItemsEndpoints.MapWorkItemRow(wi, prId, prCol) },
                ct);
        }
        return Results.Ok(MapRow(row));
    }

    private static async Task<IResult> ConfirmStrain(
        HttpContext ctx,
        long id,
        ITaskQueueService queue,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "system";
        var row = await queue.ConfirmStrainAsync(id, caller, ct);
        return row is null
            ? Results.Problem(title: $"Queue row {id} not found", statusCode: 404)
            : Results.Ok(MapRow(row));
    }

    private static async Task<IResult> ListForTeam(string teamId, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
        var open = TaskQueueStates.OpenStates.ToList();
        var rows = await db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == "team" && r.TargetId == teamId && open.Contains(r.State))
            .ToListAsync(ct);
        var ordered = rows.OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt).ToList();
        return Results.Ok(ordered.Select(MapRow).ToList());
    }

    private static async Task<IResult> ListForCrew(string crewId, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
        var open = TaskQueueStates.OpenStates.ToList();
        var rows = await db.TaskQueue.AsNoTracking()
            .Where(r => r.RoutedToCrewId == crewId && open.Contains(r.State))
            .ToListAsync(ct);
        var ordered = rows.OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt).ToList();
        return Results.Ok(ordered.Select(MapRow).ToList());
    }

    private static async Task<IResult> GetCrewStats(string crewId, ITaskQueueService queue, CancellationToken ct)
    {
        var stats = await queue.GetCrewStatsAsync(crewId, ct);
        return Results.Ok(stats);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<IResult?> CheckLeadOrPoForCrewAsync(HttpContext ctx, ControlDbContext control, ProductDbContext db, string? crewId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(crewId)) return null; // multi-team paths can skip the crew-specific gate
        var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
        // PO covers all crews; otherwise the crew's own
        // lead can mutate. Single-user dev passes through.
        var gate = RequireRole.CheckForProduct(ctx, control, null, RequireRole.ProductOwner);
        return gate; // null means caller passes (PO or single-user dev); concrete result short-circuits.
    }

    private static async Task BroadcastChangedAsync(IHubContext<ForgeHub> hub, TaskQueueRow row, string kind, CancellationToken ct)
    {
        await hub.Clients.All.SendAsync("queue.changed", new { kind, row = MapRow(row) }, ct);
    }

    internal static TaskQueueEntry MapRow(TaskQueueRow r) => new(
        r.Id, r.WorkItemId, r.TargetKind, r.TargetId, r.State, r.PriorityRank,
        r.EnqueuedAt, r.AckRequiredBy, r.AckedAt, r.AckByUserId, r.AutoAcked,
        r.RoutedToCrewId, r.PickedUpAt, r.PickedUpByAgent, r.CompletedAt, r.LastUpdatedAt);

    internal static TaskQueueHistoryEntry MapHistory(TaskQueueHistoryRow h)
    {
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(h.PayloadJson) ? "{}" : h.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        return new TaskQueueHistoryEntry(h.Id, h.QueueId, h.Kind, h.FromValue, h.ToValue, h.ByUserId, h.AtDepth, h.EtaMinutes, payload, h.At);
    }
}
