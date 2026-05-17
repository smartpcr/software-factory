// -----------------------------------------------------------------------
// <copyright file="BuildsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 27 — REST surface for the build sync +
/// repair loop. Read endpoints support the "Builds" tab on the live
/// execution view; write endpoints (manual sync, annotate, promote-now)
/// are gated to lead/PO callers.
/// </summary>
public static class BuildsEndpoints
{
    public static WebApplication MapBuildsEndpoints(this WebApplication app)
    {
        var prGroup = app.MapGroup("/api/prs/{prId}/builds").WithTags("Builds");
        prGroup.MapGet("/", ListBuildsForPr).WithName("ListBuildsForPr");

        var bGroup = app.MapGroup("/api/builds").WithTags("Builds");
        bGroup.MapGet("/{id:long}", GetBuild).WithName("GetBuild");
        bGroup.MapGet("/{id:long}/stages/{stageId:long}/logs", GetStageLogs).WithName("GetBuildStageLogs");
        bGroup.MapGet("/{id:long}/artifacts", ListArtifacts).WithName("ListBuildArtifacts");
        bGroup.MapGet("/{id:long}/artifacts/{artifactId:long}/files", ListArtifactFiles).WithName("ListBuildArtifactFiles");
        bGroup.MapPost("/{id:long}/sync", ManualSync).WithName("ManualSyncBuild");
        bGroup.MapPost("/{id:long}/annotate", AnnotateBuild).WithName("AnnotateBuild");
        bGroup.MapPost("/{id:long}/rerun", RerunBuild).WithName("RerunBuild");

        app.MapPost("/api/queue/build-fix-pending/{id:long}/promote-now", PromoteBuildFixNow)
            .WithName("PromoteBuildFixNow")
            .WithTags("Builds");

        return app;
    }

    // GET /api/prs/{prId}/builds?limit=50
    private static async Task<IResult> ListBuildsForPr(
        string prId,
        ControlDbContext control,
        ProductDbContext db,
        [FromQuery] int limit = 50)
    {
        prId = Uri.UnescapeDataString(prId);
        if (!await db.Prs.AnyAsync(p => p.Id == prId))
            return Results.Problem(title: $"PR {prId} not found", statusCode: 404);

        if (limit <= 0) limit = 50;
        if (limit > 500) limit = 500;

        var builds = await db.Builds.AsNoTracking()
            .Where(b => b.PrId == prId)
            .OrderByDescending(b => b.StartedAt ?? string.Empty)
            .Take(limit)
            .ToListAsync();
        return Results.Ok(new { total = builds.Count, items = builds });
    }

    // GET /api/builds/{id} — build + stages + failure points joined.
    private static async Task<IResult> GetBuild(long id, ControlDbContext control, ProductDbContext db)
    {
        var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (build is null) return Results.Problem(title: $"Build {id} not found", statusCode: 404);
        var stages = await db.BuildStages.AsNoTracking()
            .Where(s => s.BuildId == id)
            .OrderBy(s => s.StageOrder)
            .ToListAsync();
        var stageIds = stages.Select(s => s.Id).ToList();
        var failurePoints = await db.BuildFailurePoints.AsNoTracking()
            .Where(f => stageIds.Contains(f.StageId))
            .ToListAsync();
        return Results.Ok(new
        {
            build,
            stages,
            failurePoints,
        });
    }

    // GET /api/builds/{id}/stages/{stageId}/logs?from=&limit=
    private static async Task<IResult> GetStageLogs(
        long id, long stageId, ControlDbContext control, ProductDbContext db,
        [FromQuery] int from = 0,
        [FromQuery] int limit = 200)
    {
        var stage = await db.BuildStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stageId && s.BuildId == id);
        if (stage is null) return Results.Problem(title: $"Stage {stageId} not found on build {id}", statusCode: 404);

        var log = await db.BuildStageLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.StageId == stageId && l.LogIndex == 0);
        if (log is null) return Results.Ok(new { stage, lines = Array.Empty<string>(), totalLines = 0 });

        if (limit <= 0) limit = 200;
        if (limit > 10000) limit = 10000;
        var allLines = log.Content.Split('\n');
        var slice = allLines.Skip(from).Take(limit).ToArray();
        return Results.Ok(new
        {
            stage,
            lines = slice,
            totalLines = allLines.Length,
            from,
            limit,
            truncatedFromBytes = log.TruncatedFromBytes,
        });
    }

    private static async Task<IResult> ListArtifacts(long id, ControlDbContext control, ProductDbContext db)
    {
        if (!await db.Builds.AnyAsync(b => b.Id == id))
            return Results.Problem(title: $"Build {id} not found", statusCode: 404);
        var rows = await db.BuildArtifacts.AsNoTracking()
            .Where(a => a.BuildId == id)
            .OrderBy(a => a.Id)
            .ToListAsync();
        return Results.Ok(new { items = rows });
    }

    private static async Task<IResult> ListArtifactFiles(long id, long artifactId, ControlDbContext control, ProductDbContext db)
    {
        var art = await db.BuildArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId && a.BuildId == id);
        if (art is null) return Results.Problem(title: $"Artifact {artifactId} not found on build {id}", statusCode: 404);
        var files = await db.BuildArtifactFiles.AsNoTracking()
            .Where(f => f.ArtifactId == artifactId)
            .OrderBy(f => f.RelPath)
            .ToListAsync();
        return Results.Ok(new { artifact = art, files });
    }

    // POST /api/builds/{id}/sync?force=true — manual trigger.
    private static async Task<IResult> ManualSync(
        long id,
        ControlDbContext control,
        ProductDbContext db,
        BuildSyncJob job,
        IUserContext user,
        [FromQuery] bool force = false,
        CancellationToken ct = default)
    {
        var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (build is null) return Results.Problem(title: $"Build {id} not found", statusCode: 404);
        if (force && !IsLeadOrPo(user)) return Results.Forbid();
        if (!string.IsNullOrEmpty(build.PrId))
        {
            await job.RunSweepForAsync(build.PrId!, ct);
        }
        return Results.Accepted();
    }

    // POST /api/builds/{id}/annotate — operator categorisation.
    private static async Task<IResult> AnnotateBuild(
        long id,
        ControlDbContext control,
        ProductDbContext db,
        IUserContext user,
        IHubContext<ForgeHub> hub,
        [FromBody] AnnotateRequest body,
        CancellationToken ct = default)
    {
        if (!IsLeadOrPo(user)) return Results.Forbid();
        var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (build is null) return Results.Problem(title: $"Build {id} not found", statusCode: 404);

        if (string.IsNullOrEmpty(body.Category)) return Results.BadRequest(new { error = "category required" });
        var allowed = new[] { "infra", "flaky-test", "env", "code" };
        if (!allowed.Contains(body.Category, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"category must be one of {string.Join(", ", allowed)}" });
        }

        var row = new BuildAnnotationRow
        {
            BuildId = id,
            Category = body.Category!,
            RootCause = body.RootCause,
            SuggestedFiles = body.SuggestedFiles is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(body.SuggestedFiles),
            ByUserId = user.UserId,
            At = DateTimeOffset.UtcNow.ToString("o"),
        };
        db.BuildAnnotations.Add(row);
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("pr.changed",
            new { kind = "build", buildId = id, prId = build.PrId, annotated = body.Category }, ct);
        return Results.Ok(row);
    }

    public sealed class AnnotateRequest
    {
        public string? RootCause { get; set; }
        public string[]? SuggestedFiles { get; set; }
        public string? Category { get; set; }
    }

    // POST /api/builds/{id}/rerun — Phase R placeholder; not implemented at v0.1.
    private static IResult RerunBuild(long id) => Results.Problem(
        title: "Manual build re-trigger not implemented at v0.1",
        statusCode: StatusCodes.Status501NotImplemented,
        type: "build.rerun_not_implemented_v0_1",
        detail: "Phase 27 Q27.9 — builds re-run naturally on push. Manual re-trigger is forge_v2.md territory.");

    // POST /api/queue/build-fix-pending/{id}/promote-now — operator override.
    private static async Task<IResult> PromoteBuildFixNow(
        long id,
        ControlDbContext control,
        ProductDbContext db,
        ITaskQueueService queue,
        ForgeMetrics metrics,
        IUserContext user,
        IHubContext<ForgeHub> hub,
        CancellationToken ct = default)
    {
        if (!IsLeadOrPo(user)) return Results.Forbid();
        var pending = await db.BuildFixPending.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pending is null)
        {
            return Results.Problem(
                title: $"build_fix_pending {id} not found",
                statusCode: StatusCodes.Status409Conflict,
                type: "queue.build_fix_pending_not_found");
        }

        // Cancel any in-flight comment-response or coding row on this work item.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var blocking = await db.TaskQueue
            .Where(r => r.WorkItemId == pending.WorkItemId
                && (r.Kind == TaskQueueKinds.CommentResponse
                    || r.Kind == TaskQueueKinds.Coding
                    || r.Kind == TaskQueueKinds.Work)
                && openStates.Contains(r.State))
            .ToListAsync(ct);
        foreach (var b in blocking)
        {
            await queue.CancelAsync(b.Id, user.UserId ?? "system:promote-now", "build-fix-promote", ct);
        }

        // Resolve crew + preferred agent and enqueue the build-fix.
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == pending.WorkItemId, ct);
        if (wi is null) return Results.Problem(title: "Work item missing", statusCode: 410);
        var (crewId, preferredAgentId) = ResolveAssignments(wi.AssignedAgentJson);
        if (string.IsNullOrEmpty(crewId)) return Results.Problem(title: "No crew on work item", statusCode: 409);

        var row = await queue.EnqueueAsync(
            pending.WorkItemId,
            "crew",
            crewId!,
            ct,
            kind: TaskQueueKinds.BuildFix,
            preferredAgentId: preferredAgentId);

        db.BuildFixPending.Remove(pending);
        await db.SaveChangesAsync(ct);
        metrics.BuildFixPromotedNowAdd();
        await hub.Clients.All.SendAsync("task.build-fix-promoted",
            new { workItemId = pending.WorkItemId, queueId = row.Id, byUserId = user.UserId }, ct);
        return Results.Ok(new { queueId = row.Id });
    }

    private static (string? CrewId, string? PreferredAgentId) ResolveAssignments(string? assignedAgentJson)
    {
        if (string.IsNullOrEmpty(assignedAgentJson)) return (null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assignedAgentJson);
            string? crewId = null, agentId = null;
            if (doc.RootElement.TryGetProperty("crewId", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                crewId = c.GetString();
            else if (doc.RootElement.TryGetProperty("CrewId", out var c2) && c2.ValueKind == System.Text.Json.JsonValueKind.String)
                crewId = c2.GetString();

            foreach (var name in new[] { "agents", "Agents" })
            {
                if (doc.RootElement.TryGetProperty(name, out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            agentId = el.GetString();
                            break;
                        }
                    }
                    if (agentId is not null) break;
                }
            }
            return (crewId, agentId);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Phase 27 — lead / PO authority gate. The single-user dev path treats
    /// any signed-in user as authorized; production deployments override
    /// via the auth middleware. We surface "is authorised" as a single
    /// helper so the test surface can stub it cleanly.
    /// </summary>
    private static bool IsLeadOrPo(IUserContext user)
    {
        // Anonymous calls are 401 (handled by middleware); we accept any
        // identified user here. Tighter role-based gating lives in the
        // auth layer (Phase 9 / 10), which we intentionally don't duplicate.
        return !string.IsNullOrEmpty(user.UserId);
    }
}
