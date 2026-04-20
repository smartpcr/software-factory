// -----------------------------------------------------------------------
// <copyright file="FilesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System.IO;
using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkflowUi.Server.Auth;
using WorkflowUi.Server.Files;
using WorkflowUi.Server.Persistence;

/// <summary>
/// Minimal-API endpoints for workspace file operations:
/// tree projection, import/export, rename, and duplicate.
/// </summary>
public static class FilesEndpoints
{
    public static WebApplication MapFilesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/files").WithTags("Files");

        // GET /api/files — workspace tree
        group.MapGet("/", async (
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await store.ListAsync(user, ct);
                var tree = TreeProjector.Project(summaries);
                return Results.Ok(tree);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("GetWorkspaceTree");

        // POST /api/files — create empty workflow
        group.MapPost("/", async (
            [FromBody] CreateWorkflowRequest request,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var definition = $"id: {request.Id}\nname: {request.Id}\nversion: '1.0'\nnodes: []\nconnections: []\n";
                var result = await store.SaveAsync(request.Id, definition, "{}", null, user, ct);
                return result == SaveResult.Created
                    ? Results.Created($"/api/workflows/{request.Id}", null)
                    : Results.Conflict();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("CreateWorkflow");

        // PATCH /api/files/{*id} — rename workflow
        group.MapPatch("/{*id}", async (
            string id,
            [FromBody] RenameWorkflowRequest request,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                // Check if target already exists
                var existing = await store.GetAsync(request.NewId, user, ct);
                if (existing != null)
                {
                    return Results.Conflict();
                }

                var source = await store.GetAsync(id, user, ct);
                if (source == null)
                {
                    return Results.NotFound();
                }

                // Save under new id, delete old
                await store.SaveAsync(request.NewId, source.Definition, source.SidecarJson, null, user, ct);
                await store.DeleteAsync(id, source.ETag, user, ct);
                return Results.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("RenameWorkflow");

        // DELETE /api/files/{*id} — delete workflow
        group.MapDelete("/{*id}", async (
            string id,
            [FromHeader(Name = "If-Match")] string? ifMatch,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                var record = await store.GetAsync(id, user, ct);
                if (record == null)
                {
                    return Results.NotFound();
                }

                var etag = ifMatch ?? record.ETag;
                var deleted = await store.DeleteAsync(id, etag, user, ct);
                return deleted ? Results.NoContent() : Results.StatusCode(412);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("DeleteFile");

        // POST /api/files/duplicate — duplicate workflow (with optional targetId + template placement)
        // Uses body-provided sourceId instead of path parameter to avoid catch-all route conflicts
        group.MapPost("/duplicate", async (
            [FromBody] DuplicateRequest request,
            IWorkflowStore store,
            IdGenerator idGen,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrEmpty(request?.SourceId))
                {
                    return Results.BadRequest("sourceId is required");
                }

                var source = await store.GetAsync(request.SourceId, user, ct);
                if (source == null)
                {
                    return Results.NotFound();
                }

                string newId;
                if (!string.IsNullOrEmpty(request.TargetId))
                {
                    newId = request.TargetId;
                }
                else
                {
                    // Template placement: source under "templates/" → place copy at root
                    var baseId = request.SourceId.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)
                        ? request.SourceId.Split('/')[^1]
                        : request.SourceId;
                    newId = await idGen.GenerateUniqueIdAsync(baseId, user, ct);
                }

                var definition = source.Definition.Replace(request.SourceId, newId);
                await store.SaveAsync(newId, definition, source.SidecarJson, null, user, ct);
                return Results.Created($"/api/workflows/{newId}", new { id = newId });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("DuplicateWorkflow");

        // POST /api/files/export — export workflows as zip
        group.MapPost("/export", async (
            [FromBody] ExportRequest request,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                using var ms = new MemoryStream();
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var workflowId in request.Ids)
                    {
                        var record = await store.GetAsync(workflowId, user, ct);
                        if (record == null)
                        {
                            continue;
                        }

                        var defEntry = zip.CreateEntry($"{workflowId}.json");
                        using (var writer = new StreamWriter(defEntry.Open()))
                        {
                            await writer.WriteAsync(record.Definition);
                        }

                        var sidecarEntry = zip.CreateEntry($"{workflowId}.ui.json");
                        using (var writer = new StreamWriter(sidecarEntry.Open()))
                        {
                            await writer.WriteAsync(record.SidecarJson);
                        }
                    }
                }

                ms.Position = 0;
                return Results.File(ms.ToArray(), "application/zip", "workflows.zip");
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("ExportWorkflows");

        // POST /api/files/import — import workflows from zip
        group.MapPost("/import", async (
            HttpRequest httpRequest,
            IWorkflowStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                using var ms = new MemoryStream();
                await httpRequest.Body.CopyToAsync(ms, ct);
                ms.Position = 0;

                var imported = new List<string>();
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    var defEntries = zip.Entries
                        .Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            && !e.FullName.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var defEntry in defEntries)
                    {
                        if (!ZipEntryNameSanitiser.IsSafe(defEntry.FullName))
                        {
                            continue;
                        }

                        using var reader = new StreamReader(defEntry.Open());
                        var definition = await reader.ReadToEndAsync(ct);

                        var id = defEntry.FullName
                            .Replace(".json", string.Empty, StringComparison.OrdinalIgnoreCase);

                        var sidecarName = defEntry.FullName
                            .Replace(".json", ".ui.json", StringComparison.OrdinalIgnoreCase);
                        var sidecarEntry = zip.GetEntry(sidecarName);
                        var sidecar = "{}";
                        if (sidecarEntry != null)
                        {
                            using var sidecarReader = new StreamReader(sidecarEntry.Open());
                            sidecar = await sidecarReader.ReadToEndAsync(ct);
                        }

                        await store.SaveAsync(id, definition, sidecar, null, user, ct);
                        imported.Add(id);
                    }
                }

                return Results.Ok(new { imported });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).WithName("ImportWorkflows");

        return app;
    }
}

public record CreateWorkflowRequest(string Id);

public record RenameWorkflowRequest(string NewId);

public record ExportRequest(string[] Ids);

public record DuplicateRequest(string? SourceId, string? TargetId);
