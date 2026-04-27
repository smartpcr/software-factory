// -----------------------------------------------------------------------
// <copyright file="RunEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
            IOptions<PersistenceOptions> persistenceOptions,
            IWebHostEnvironment env,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var record = await store.GetAsync(request.WorkflowId, user, ct);
            if (record == null)
            {
                return Results.NotFound(new { message = $"Workflow '{request.WorkflowId}' not found." });
            }

            var workflowsRoot = Path.Combine(env.ContentRootPath, persistenceOptions.Value.WorkflowsRoot);
            var workflow = WorkflowDefinitionMapper.MapFromJson(
                record.Definition,
                workflowsRoot);

            // If a test payload is provided (for webhook testing), pass it as initial variables
            Dictionary<string, object>? initialVars = null;
            if (!string.IsNullOrWhiteSpace(request.TestPayload))
            {
                try
                {
                    var payloadDoc = System.Text.Json.JsonDocument.Parse(request.TestPayload);
                    initialVars = new Dictionary<string, object> { ["__webhookBody"] = request.TestPayload };
                    foreach (var prop in payloadDoc.RootElement.EnumerateObject())
                    {
                        initialVars[$"body.{prop.Name}"] = prop.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => prop.Value.GetString()!,
                            System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => prop.Value.GetRawText(),
                        };
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return Results.BadRequest(new { message = "Invalid test payload JSON." });
                }
            }

            var runId = await runManager.StartRunAsync(workflow, initialVars);
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

        group.MapGet("/{runId:guid}/status", (
            Guid runId,
            RunManager runManager) =>
        {
            var run = runManager.GetRun(runId);
            if (run == null)
            {
                return Results.NotFound(new { message = $"Run '{runId}' not found." });
            }

            return Results.Ok(new
            {
                run.RunId,
                run.Status,
            });
        })
        .WithName("GetRunStatus");

        group.MapGet("/{runId:guid}", (
            Guid runId,
            RunManager runManager,
            RunHistoryBuffer historyBuffer,
            RunHistoryReader historyReader) =>
        {
            var run = runManager.GetRun(runId);
            if (run == null)
            {
                return Results.NotFound(new { message = $"Run '{runId}' not found." });
            }

            // Try in-memory buffer first (for active runs); fall back to JSONL
            // file when buffer is truncated or empty (completed/evicted runs).
            var replay = historyBuffer.Replay(runId, 0);
            RunEvent[] events;
            if (replay.NotFound || replay.Truncated || replay.Events.Count == 0)
            {
                // Buffer lost early events or run is no longer tracked — read full history from disk
                var fromFile = historyReader.Read(runId);
                events = fromFile.Count > 0 ? fromFile.ToArray() : (replay.NotFound ? Array.Empty<RunEvent>() : replay.Events.ToArray());
            }
            else
            {
                events = replay.Events.ToArray();
            }

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
public record RunRequest(string WorkflowId, string? TestPayload = null);