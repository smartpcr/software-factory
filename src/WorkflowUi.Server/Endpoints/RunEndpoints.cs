// -----------------------------------------------------------------------
// <copyright file="RunEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Hubs;
using WorkflowUi.Server.Mapping;
using WorkflowUi.Server.Persistence;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Minimal-API endpoints for starting, cancelling, and querying workflow runs.
/// </summary>
public static class RunEndpoints
{
    public static WebApplication MapRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runs").WithTags("Runs");

        group.MapPost("/", async (
            RunRequest request,
            RunManager runManager,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var record = await store.GetAsync(request.WorkflowId, user, ct);
            if (record == null)
            {
                return Results.NotFound(new { message = $"Workflow '{request.WorkflowId}' not found." });
            }

            var workflow = WorkflowDefinitionMapper.MapFromJson(record.Definition);
            var runId = await runManager.StartRunAsync(workflow);
            return Results.Ok(new { runId });
        })
        .WithName("StartRun");

        group.MapPost("/{runId:guid}/cancel", (
            Guid runId,
            RunManager runManager) =>
        {
            var cancelled = runManager.CancelRun(runId);
            return cancelled
                ? Results.Ok(new { runId, status = "CancelRequested" })
                : Results.NotFound(new { message = $"Run '{runId}' not found or already finished." });
        })
        .WithName("CancelRun");

        group.MapGet("/{runId:guid}", (
            Guid runId,
            RunManager runManager,
            RunHistoryBuffer historyBuffer) =>
        {
            var run = runManager.GetRun(runId);
            if (run == null)
            {
                return Results.NotFound(new { message = $"Run '{runId}' not found." });
            }

            // Include buffered events for this run
            var replay = historyBuffer.Replay(runId, 0);
            var events = replay.NotFound ? Array.Empty<RunEvent>() : replay.Events.ToArray();

            return Results.Ok(new
            {
                run.RunId,
                run.WorkflowId,
                run.WorkflowName,
                run.Status,
                run.StartTimeUtc,
                run.EndTimeUtc,
                events,
            });
        })
        .WithName("GetRun");

        group.MapGet("/", (
            RunManager runManager,
            [FromQuery] string? workflowName) =>
        {
            var runs = runManager.GetRuns();
            if (!string.IsNullOrEmpty(workflowName))
            {
                runs = runs.Where(r => r.WorkflowName == workflowName).ToList();
            }

            var summaries = runs.Select(r => new
            {
                r.RunId,
                r.WorkflowId,
                r.WorkflowName,
                r.Status,
                r.StartTimeUtc,
                r.EndTimeUtc,
            });
            return Results.Ok(summaries);
        })
        .WithName("ListRuns");

        return app;
    }
}

/// <summary>
/// Request body for starting a new workflow run.
/// </summary>
public record RunRequest(string WorkflowId);