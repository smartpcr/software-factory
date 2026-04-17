// -----------------------------------------------------------------------
// <copyright file="WorkflowEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkflowUi.Server.Auth;
using WorkflowUi.Server.Persistence;

/// <summary>
/// Minimal-API endpoints for workflow CRUD operations backed by <see cref="IWorkflowStore"/>.
/// </summary>
public static class WorkflowEndpoints
{
    public static WebApplication MapWorkflowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Workflows");

        group.MapGet("/", async (
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await store.ListAsync(user, ct);
                return Results.Ok(summaries);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("ListWorkflows");

        group.MapGet("/{id}", async (
            string id,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var record = await store.GetAsync(id, user, ct);
                return record == null ? Results.NotFound() : Results.Ok(record);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("GetWorkflow");

        group.MapPut("/{id}", async (
            string id,
            [FromBody] SaveWorkflowRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var result = await store.SaveAsync(
                    id,
                    request.Yaml,
                    request.SidecarJson ?? "{}",
                    ifMatch,
                    user,
                    ct);

                return result switch
                {
                    SaveResult.Created => Results.Created($"/api/workflows/{id}", null),
                    SaveResult.Updated => Results.Ok(),
                    SaveResult.PreconditionFailed => Results.StatusCode(412),
                    _ => Results.StatusCode(500),
                };
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("SaveWorkflow");

        group.MapDelete("/{id}", async (
            string id,
            [FromHeader(Name = "If-Match")] string? ifMatch,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrEmpty(ifMatch))
                {
                    return Results.BadRequest("If-Match header required for delete");
                }

                var deleted = await store.DeleteAsync(id, ifMatch, user, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("DeleteWorkflow");

        return app;
    }
}

/// <summary>
/// Request body for saving a workflow.
/// </summary>
public record SaveWorkflowRequest(string Yaml, string? SidecarJson);
