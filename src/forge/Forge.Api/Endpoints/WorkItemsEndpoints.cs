// -----------------------------------------------------------------------
// <copyright file="WorkItemsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 12.3 — Work Item Tracking CRUD + move + reparent. Local-only here;
/// provider sync (push to ADO / GitHub) lands with Phase 12.4 in Batch B.
/// </summary>
public static class WorkItemsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static WebApplication MapWorkItemsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/work-items").WithTags("WorkItems");

        grp.MapGet("/", List).WithName("ListWorkItems");
        grp.MapGet("/{id}", Get).WithName("GetWorkItem");
        grp.MapPost("/", Create).WithName("CreateWorkItem");
        grp.MapPatch("/{id}", Patch).WithName("PatchWorkItem");
        grp.MapDelete("/{id}", Delete).WithName("DeleteWorkItem");
        grp.MapPost("/{id}/move", Move).WithName("MoveWorkItem");
        grp.MapPost("/{id}/parent", Reparent).WithName("ReparentWorkItem");
        grp.MapPost("/sync/{repoId}", Sync).WithName("SyncWorkItems");
        grp.MapPost("/{id}/resync", Resync).WithName("ResyncWorkItem");
        grp.MapPost("/{id}/create-pr", CreatePr).WithName("CreateWorkItemPr");

        return app;
    }

    // ----- SYNC (Phase 12.4 / 12.10) -------------------------------------

    public sealed record SyncWorkItemsRequest(
        string? AreaPath = null,
        IReadOnlyList<string>? Types = null,
        string? AssignedTo = null,
        DateTimeOffset? UpdatedFrom = null,
        DateTimeOffset? UpdatedTo = null,
        bool IgnoreThrottle = false);

    private static async Task<IResult> Sync(
        HttpContext ctx,
        string repoId,
        [FromBody] SyncWorkItemsRequest? req,
        ForgeDbContext db,
        WorkItemSyncService syncService,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null) return Results.Problem(title: $"Repo {repoId} not found", statusCode: 404);

        var filters = new WorkItemSyncFilters(
            AreaPath: req?.AreaPath,
            Types: req?.Types,
            AssignedTo: req?.AssignedTo,
            UpdatedFrom: req?.UpdatedFrom,
            UpdatedTo: req?.UpdatedTo);

        // Ignoring the throttle is PO-only — operators with the role can
        // force-sync after a webhook miss without waiting 60 s.
        var ignoreThrottle = req?.IgnoreThrottle ?? false;
        if (ignoreThrottle)
        {
            var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
            if (gate is not null) return gate;
        }

        var result = await syncService.SyncAsync(repo, filters, ignoreThrottle, ct);
        if (result.Error is not null && result.Pulled == 0 && result.Inserted == 0)
        {
            // Throttle / config errors → 429 / 400; provider HTTP errors → 502.
            var status = result.Error.StartsWith("Rate limit", StringComparison.OrdinalIgnoreCase) ? 429
                : result.Error.Contains("not configured", StringComparison.OrdinalIgnoreCase) ? 400
                : result.Error.Contains("No provider registered", StringComparison.OrdinalIgnoreCase) ? 400
                : 502;
            return Results.Problem(title: result.Error, statusCode: status);
        }

        return Results.Ok(new
        {
            repoId,
            pulled = result.Pulled,
            inserted = result.Inserted,
            updated = result.Updated,
            skipped = result.Skipped,
            error = result.Error,
        });
    }

    // ----- LIST -----------------------------------------------------------

    private static async Task<IResult> List(
        HttpContext ctx,
        ForgeDbContext db,
        string? repoId,
        string? iterationRef,
        string? state,
        string? type,
        string? track,
        string? parentId,
        string? assignedTo,
        string? tag,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var query = db.WorkItems.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(repoId))       query = query.Where(w => w.RepoId == repoId);
        if (!string.IsNullOrWhiteSpace(iterationRef)) query = query.Where(w => w.IterationRef == iterationRef);
        if (!string.IsNullOrWhiteSpace(parentId))     query = query.Where(w => w.ParentId == parentId);
        if (!string.IsNullOrWhiteSpace(track))        query = query.Where(w => w.Track == track);

        // Repeated query keys (?state=active&state=proposed) come through as
        // a single comma-joined string from minimal-API binding; split on ','.
        // Use List<string> so EF picks the IEnumerable.Contains overload it
        // can translate (string[] resolves to a ReadOnlySpan-based overload
        // on .NET 8 that the SQLite provider rejects).
        if (!string.IsNullOrWhiteSpace(state))
        {
            var states = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            query = query.Where(w => states.Contains(w.State));
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            var types = type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            query = query.Where(w => types.Contains(w.Type));
        }

        // Materialise and finish filtering in memory for the JSON-string columns
        // and the assignedTo lookup; the indexed columns above already cut the
        // working set down. SQLite EF can't translate `JsonContains`-style
        // predicates anyway.
        var rows = await query.OrderBy(w => w.Id).Take(pageSize is int ps ? Math.Clamp(ps, 1, 500) * Math.Max(1, page ?? 1) : 500).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(assignedTo))
        {
            rows = rows.Where(r => MatchesAssignee(r.AssignedToJson, assignedTo)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            rows = rows.Where(r => MatchesTag(r.TagsJson, tag)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.ToLowerInvariant();
            rows = rows.Where(r =>
                r.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.DescriptionMd.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Page slice (in memory; volume is bounded by pageSize cap above).
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 100, 1, 500);
        var paged = rows.Skip((p - 1) * size).Take(size).ToList();

        return Results.Ok(paged.Select(MapWorkItem).ToList());
    }

    // ----- GET ------------------------------------------------------------

    private static async Task<IResult> Get(string id, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return row is null
            ? Results.Problem(title: $"Work item {id} not found", statusCode: 404)
            : Results.Ok(MapWorkItem(row));
    }

    // ----- CREATE ---------------------------------------------------------

    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateWorkItemRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Title))
        {
            return Results.Problem(title: "id and title are required", statusCode: 400);
        }
        if (!WorkItemRules.AllTypes.Contains(req.Type))
        {
            return Results.Problem(title: $"Unknown type '{req.Type}'", statusCode: 400);
        }
        if (!WorkItemRules.AllTracks.Contains(req.Track))
        {
            return Results.Problem(title: $"Unknown track '{req.Track}'", statusCode: 400);
        }
        if (WorkItemRules.TrackOf(req.Type) != req.Track)
        {
            return Results.Problem(
                title: $"Type '{req.Type}' belongs to track '{WorkItemRules.TrackOf(req.Type)}', not '{req.Track}'.",
                statusCode: 400);
        }
        if (await db.WorkItems.AnyAsync(w => w.Id == req.Id, ct))
        {
            return Results.Problem(title: $"Work item {req.Id} already exists", statusCode: 409);
        }

        var now = DateTimeOffset.UtcNow;
        var row = new WorkItemRow
        {
            Id = req.Id,
            RepoId = req.RepoId,
            Provider = string.IsNullOrWhiteSpace(req.Provider) ? "local" : req.Provider!,
            ProviderRef = req.ProviderRef,
            Track = req.Track,
            Type = req.Type,
            State = string.IsNullOrWhiteSpace(req.State) ? "proposed" : req.State!,
            Title = req.Title,
            DescriptionMd = req.DescriptionMd ?? string.Empty,
            ParentId = string.IsNullOrWhiteSpace(req.ParentId) ? null : req.ParentId,
            FeatureRef = req.FeatureRef,
            IterationRef = req.IterationRef ?? string.Empty,
            AreaPath = req.AreaPath ?? string.Empty,
            AssignedToJson = req.AssignedTo is null ? null : JsonSerializer.Serialize(req.AssignedTo, JsonOpts),
            Points = req.Points ?? 0,
            Priority = req.Priority ?? "med",
            TagsJson = JsonSerializer.Serialize(req.Tags ?? Array.Empty<string>(), JsonOpts),
            CreatedAt = now,
            UpdatedAt = now,
            RemoteRevision = NewRevision(),
            ExpectedFileChanges = req.ExpectedFileChanges,
        };

        // Validate the parent invariants before committing.
        if (row.ParentId is not null)
        {
            var parent = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(p => p.Id == row.ParentId, ct);
            if (parent is null) return Results.Problem(title: $"Parent {row.ParentId} not found", statusCode: 400);
            var err = WorkItemRules.ValidateParent(row, parent);
            if (err is not null) return Results.Problem(title: err, statusCode: 400);

            // Phase 16.5 — plan-tree fan-out caps (≤20 stages/phase, ≤20 steps/stage).
            var budget = await WorkItemRules.ValidatePlanTreeBudgetAsync(db, row, parent, ct);
            if (budget is not null)
            {
                return Results.Problem(
                    type: "work_item.tree_over_capacity",
                    title: budget,
                    statusCode: 400);
            }
        }
        if (!WorkItemRules.AllStates.Contains(row.State))
        {
            return Results.Problem(title: $"Unknown state '{row.State}'", statusCode: 400);
        }

        // Phase 16.5 — per-step file-change cap (≤20 files).
        var stepBudget = WorkItemRules.ValidateStepFileBudget(row);
        if (stepBudget is not null)
        {
            return Results.Problem(
                type: "work_item.step_over_file_budget",
                title: stepBudget,
                statusCode: 400);
        }

        db.WorkItems.Add(row);
        await db.SaveChangesAsync(ct);

        // Phase 17.3 — team or crew assignees enter the persistent queue;
        // direct-to-agent assignees still flow through the Phase 16
        // dispatcher path. The queue service is resolved via DI but is
        // optional so older callers / tests without DI scope still work.
        var queue = ctx.RequestServices.GetService<Forge.Api.Services.ITaskQueueService>();
        if (queue is not null && req.AssignedTo is not null && !string.IsNullOrEmpty(req.AssignedTo.Id))
        {
            if (req.AssignedTo.Kind == "team")
            {
                await queue.EnqueueAsync(row.Id, "team", req.AssignedTo.Id, ct);
            }
            else if (req.AssignedTo.Kind == "crew")
            {
                await queue.EnqueueAsync(row.Id, "crew", req.AssignedTo.Id, ct);
            }
        }

        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "created", workItem = MapWorkItem(row) }, ct);
        return Results.Created($"/api/work-items/{row.Id}", MapWorkItem(row));
    }

    // ----- PATCH ----------------------------------------------------------

    private static async Task<IResult> Patch(
        HttpContext ctx,
        string id,
        [FromBody] PatchWorkItemRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Phase 5.9 conflict policy: caller may pin via If-Match; on mismatch
        // we 409 with currentRevision + X-Forge-Overwrite to override.
        var ifMatch = ctx.Request.Headers["If-Match"].FirstOrDefault();
        var allowOverwrite = ctx.Request.Headers["X-Forge-Overwrite"].FirstOrDefault() == "true";
        if (!string.IsNullOrEmpty(ifMatch) && !allowOverwrite && ifMatch != row.RemoteRevision)
        {
            return Results.Json(
                new { error = "remote_changed", currentRevision = row.RemoteRevision },
                statusCode: 409);
        }

        if (req.Title is not null) row.Title = req.Title;
        if (req.DescriptionMd is not null) row.DescriptionMd = req.DescriptionMd;
        if (req.State is not null)
        {
            if (!WorkItemRules.AllStates.Contains(req.State))
                return Results.Problem(title: $"Unknown state '{req.State}'", statusCode: 400);
            row.State = req.State;
        }
        if (req.IterationRef is not null) row.IterationRef = req.IterationRef;
        if (req.AreaPath is not null) row.AreaPath = req.AreaPath;
        if (req.AssignedTo is not null)
        {
            row.AssignedToJson = JsonSerializer.Serialize(req.AssignedTo, JsonOpts);
            // Phase 17.3 — assigning to a team OR a crew enqueues so the
            // routing is observable in /inbox. Idempotency guard (Q17.6)
            // returns the existing open row when one is already in flight,
            // so duplicate PATCHes with the same assignee are safe.
            //  * kind=team  → state='awaiting-ack', 10s lead-ack SLO.
            //  * kind=crew  → state='queued' directly (skips ack stage).
            //  * kind=agent / human → no enqueue (Phase 12 direct path).
            var queue = ctx.RequestServices.GetService<Forge.Api.Services.ITaskQueueService>();
            if (queue is not null && req.AssignedTo is not null && !string.IsNullOrEmpty(req.AssignedTo.Id))
            {
                if (req.AssignedTo.Kind == "team")
                {
                    await queue.EnqueueAsync(row.Id, "team", req.AssignedTo.Id, ct);
                }
                else if (req.AssignedTo.Kind == "crew")
                {
                    await queue.EnqueueAsync(row.Id, "crew", req.AssignedTo.Id, ct);
                }
            }
        }
        if (req.ClearAssignedTo == true) row.AssignedToJson = null;
        if (req.Points is int p)
        {
            if (p < 0) return Results.Problem(title: "Points must be ≥ 0", statusCode: 400);
            row.Points = p;
        }
        if (req.Priority is not null)
        {
            if (!WorkItemRules.AllPriorities.Contains(req.Priority))
                return Results.Problem(title: $"Unknown priority '{req.Priority}'", statusCode: 400);
            row.Priority = req.Priority;
        }
        if (req.Tags is not null) row.TagsJson = JsonSerializer.Serialize(req.Tags, JsonOpts);
        if (req.FeatureRef is not null) row.FeatureRef = req.FeatureRef;
        if (req.ClearFeatureRef == true) row.FeatureRef = null;
        if (!string.IsNullOrWhiteSpace(req.RepoId))
        {
            // Refuse if the repo isn't attached to any product (FK would dangle).
            var exists = await db.Repos.AsNoTracking().AnyAsync(r => r.Id == req.RepoId, ct);
            if (!exists) return Results.Problem(title: $"Repo {req.RepoId} not found", statusCode: 400);
            row.RepoId = req.RepoId;
        }
        if (req.ClearRepoId == true) row.RepoId = null;
        if (!string.IsNullOrWhiteSpace(req.Type))
        {
            if (!WorkItemRules.AllTypes.Contains(req.Type))
                return Results.Problem(title: $"Unknown type '{req.Type}'", statusCode: 400);
            // Auto-flip track per trackOf() so cross-ladder edits stay consistent
            // (epic/feature/task/bug → dev; phase/stage/step → design).
            row.Type = req.Type;
            row.Track = WorkItemRules.TrackOf(req.Type);
        }
        if (req.ExpectedFileChanges is int efc)
        {
            row.ExpectedFileChanges = efc;
            // Validate immediately so a 21-file PATCH refuses cleanly before persist.
            var stepBudget = WorkItemRules.ValidateStepFileBudget(row);
            if (stepBudget is not null)
            {
                return Results.Problem(
                    type: "work_item.step_over_file_budget",
                    title: stepBudget,
                    statusCode: 400);
            }
        }
        if (req.ClearExpectedFileChanges == true) row.ExpectedFileChanges = null;

        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.RemoteRevision = NewRevision();
        await db.SaveChangesAsync(ct);

        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "updated", workItem = MapWorkItem(row) }, ct);
        return Results.Ok(MapWorkItem(row));
    }

    // ----- DELETE ---------------------------------------------------------

    private static async Task<IResult> Delete(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        bool? force,
        CancellationToken ct)
    {
        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        if (force == true)
        {
            // Hard delete — gated to PO.
            var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
            if (gate is not null) return gate;

            db.WorkItems.Remove(row);
            // Cascade removes the children's parent ref (we don't recursively
            // delete; the operator can re-parent or hard-delete those too).
            var children = await db.WorkItems.Where(w => w.ParentId == id).ToListAsync(ct);
            foreach (var c in children)
            {
                c.ParentId = null;
                c.UpdatedAt = DateTimeOffset.UtcNow;
                c.RemoteRevision = NewRevision();
            }
            // Drop any links pointing to or from this item.
            var links = await db.WorkItemLinks.Where(l => l.FromId == id || l.ToId == id).ToListAsync(ct);
            db.WorkItemLinks.RemoveRange(links);

            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("workitem.changed", new { kind = "deleted", id }, ct);
            return Results.NoContent();
        }

        // Default = soft-delete: flip to closed.
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        row.State = "closed";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.RemoteRevision = NewRevision();
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "closed", workItem = MapWorkItem(row) }, ct);
        return Results.Ok(MapWorkItem(row));
    }

    // ----- MOVE -----------------------------------------------------------

    private static async Task<IResult> Move(
        HttpContext ctx,
        string id,
        [FromBody] MoveWorkItemRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);
        if (req is null) return Results.Problem(title: "body is required", statusCode: 400);

        if (req.State is not null)
        {
            if (!WorkItemRules.AllStates.Contains(req.State))
                return Results.Problem(title: $"Unknown state '{req.State}'", statusCode: 400);
            row.State = req.State;
        }
        if (req.IterationRef is not null) row.IterationRef = req.IterationRef;

        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.RemoteRevision = NewRevision();
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "moved", workItem = MapWorkItem(row) }, ct);
        return Results.Ok(MapWorkItem(row));
    }

    // ----- REPARENT -------------------------------------------------------

    private static async Task<IResult> Reparent(
        HttpContext ctx,
        string id,
        [FromBody] ReparentWorkItemRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        WorkItemRow? newParent = null;
        if (!string.IsNullOrEmpty(req?.ParentId))
        {
            newParent = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ParentId, ct);
            if (newParent is null) return Results.Problem(title: $"Parent {req.ParentId} not found", statusCode: 404);
        }

        // Cycle check.
        var cycleAt = await WorkItemRules.DetectCycleAsync(db, row.Id, newParent?.Id, ct);
        if (cycleAt is not null)
        {
            return Results.Problem(
                title: $"Reparent would create a cycle through '{cycleAt}'",
                statusCode: 409);
        }

        // Rank + cross-track validation.
        var validateError = WorkItemRules.ValidateParent(row, newParent);
        if (validateError is not null) return Results.Problem(title: validateError, statusCode: 400);

        row.ParentId = newParent?.Id;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.RemoteRevision = NewRevision();
        await db.SaveChangesAsync(ct);

        await hub.Clients.All.SendAsync("workitem.linked", new { id = row.Id, parentId = row.ParentId }, ct);
        return Results.Ok(MapWorkItem(row));
    }

    // ----- RESYNC (per-item, bypasses WIQL change filter + revision skip) -

    /// <summary>
    /// Phase 14 follow-up — force-refresh a single work item from its
    /// upstream provider. The repo-level <see cref="Sync"/> call uses a
    /// WIQL change filter (so unchanged items aren't pulled) and a
    /// revision-skip in <see cref="WorkItemSyncService.UpsertAsync"/>
    /// (so identical revisions don't re-write the row). This handler
    /// bypasses both: it fetches the WI directly by provider ref and
    /// applies the remote fields verbatim, which is what the operator
    /// wants when the local schema evolved (e.g. the ADO mapper now
    /// composes Description + ReproSteps + SystemInfo + Acceptance into
    /// <c>descriptionMd</c>) and the existing row needs to be remapped.
    /// </summary>
    private static async Task<IResult> Resync(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);
        if (string.Equals(row.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(title: "Local-only work items have no upstream to re-sync from.", statusCode: 400);
        }
        if (row.ProviderRef is null)
        {
            return Results.Problem(title: "Work item has no provider reference recorded.", statusCode: 400);
        }

        var provider = providers.FirstOrDefault(p => string.Equals(p.Kind, row.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return Results.Problem(title: $"No provider registered for '{row.Provider}'.", statusCode: 400);
        }

        WorkItem? remote;
        try
        {
            remote = await provider.GetWorkItemForSyncAsync(row.ProviderRef.Value.ToString(), ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(title: $"Provider fetch failed: {ex.Message}", statusCode: 502);
        }
        if (remote is null)
        {
            return Results.Problem(title: $"Item {id} not found on the {row.Provider} side.", statusCode: 404);
        }

        // Apply remote fields. Skip ParentId when remote omits one (matches
        // WorkItemSyncService — preserves operator's local re-parent).
        row.Track = remote.Track;
        row.Type = remote.Type;
        row.State = remote.State;
        row.Title = remote.Title;
        row.DescriptionMd = remote.DescriptionMd;
        if (!string.IsNullOrEmpty(remote.ParentId)) row.ParentId = remote.ParentId;
        row.IterationRef = remote.IterationRef;
        row.AreaPath = remote.AreaPath;
        if (remote.AssignedTo is not null)
        {
            row.AssignedToJson = JsonSerializer.Serialize(remote.AssignedTo, JsonOpts);
        }
        row.Points = remote.Points;
        row.Priority = remote.Priority;
        row.TagsJson = JsonSerializer.Serialize(remote.Tags, JsonOpts);
        row.UpdatedAt = remote.UpdatedAt > DateTimeOffset.MinValue ? remote.UpdatedAt : DateTimeOffset.UtcNow;
        row.RemoteRevision = remote.RemoteRevision ?? NewRevision();

        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("workitem.changed",
            new { kind = "updated", workItem = MapWorkItem(row) }, ct);

        return Results.Ok(MapWorkItem(row));
    }

    // ----- Phase 19 — agent-callable PR creation --------------------------

    /// <summary>Request body for POST /api/work-items/{id}/create-pr.</summary>
    public sealed record CreatePrRequest(
        string Branch,
        string BaseBranch,
        string Title,
        string? Body = null,
        /// <summary>ADO work item ID to link to the PR (numeric).</summary>
        int? WorkItemId = null);

    /// <summary>
    /// Phase 19 — lightweight endpoint the delivery-phase agent calls via
    /// <c>curl</c> to create a PR through the configured provider. This keeps
    /// PR creation agent-initiated while reusing the existing provider
    /// abstraction (ADO / GitHub) for auth + API wiring.
    /// </summary>
    private static async Task<IResult> CreatePr(
        string id,
        [FromBody] CreatePrRequest req,
        ForgeDbContext db,
        IEnumerable<IForgeProvider> providers,
        Forge.Api.Realtime.TaskChannel taskChannel,
        CancellationToken ct)
    {
        if (req is null) return Results.Problem(title: "Request body is required", statusCode: 400);

        // Phase 19 — required-field gate. The user explicitly listed the
        // four conditions a Forge PR must satisfy before reaching the
        // provider: branch + baseBranch + title + a body that explains the
        // change. Reject early so we never push half-formed PRs to the
        // remote and have to clean them up later.
        if (string.IsNullOrWhiteSpace(req.Branch))
            return Results.Problem(title: "PR validation failed: branch is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.BaseBranch))
            return Results.Problem(title: "PR validation failed: baseBranch is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Title))
            return Results.Problem(title: "PR validation failed: title is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Body))
            return Results.Problem(title: "PR validation failed: body (change description) is required", statusCode: 400);

        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Resolve the provider from the work item's repo.
        var repoKind = "ado";
        string? prRepoName = null;
        string? prProjectName = null;
        if (!string.IsNullOrEmpty(wi.RepoId))
        {
            var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            if (!string.IsNullOrEmpty(repo?.Kind)) repoKind = repo.Kind;
            if (repo is not null && repoKind == "ado")
            {
                prRepoName = ExtractAdoRepoName(repo.RemoteUrl);
                if (!string.IsNullOrEmpty(repo.AdoProject))
                    prProjectName = repo.AdoProject;
            }
        }

        var provider = providers.FirstOrDefault(p => p.Kind == repoKind && p.IsConfigured)
            ?? providers.FirstOrDefault(p => p.IsConfigured);
        if (provider is null)
            return Results.Problem(title: $"No configured provider for kind '{repoKind}'", statusCode: 503);

        // Resolve work item ref for linking. ADO requires a numeric work
        // item id; for ADO repos refuse to ship a PR that wouldn't be
        // associated with the work item.
        int? wiRef = req.WorkItemId ?? wi.ProviderRef;
        if (string.Equals(repoKind, "ado", StringComparison.OrdinalIgnoreCase) && wiRef is null)
        {
            return Results.Problem(
                title: "PR validation failed: ADO PR requires a work item id (req.workItemId or wi.providerRef).",
                statusCode: 400);
        }

        var prReq = new NewPullRequestRequest(
            req.Branch, req.BaseBranch, req.Title, req.Body!,
            RepositoryName: prRepoName, ProjectName: prProjectName,
            WorkItemRef: wiRef);

        var pr = await provider.CreatePullRequestAsync(prReq, ProviderCallContext.Anonymous, ct);

        // Emit event so the live transcript shows the PR.
        await taskChannel.EmitAsync(id, "task.pr-opened", new
        {
            prId = pr.Id,
            prTitle = pr.Title,
            branch = req.Branch,
            baseBranch = req.BaseBranch,
        }, ct: ct);

        return Results.Ok(new { prId = pr.Id, prTitle = pr.Title });
    }

    /// <summary>Extract the ADO repository name from a remote URL.</summary>
    private static string? ExtractAdoRepoName(string? remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)) return null;
        // https://dev.azure.com/org/project/_git/reponame
        var idx = remoteUrl.LastIndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? remoteUrl[(idx + 6)..] : null;
    }

    // ----- helpers --------------------------------------------------------

    private static string NewRevision() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Map a <see cref="WorkItemRow"/> to the public <see cref="WorkItem"/> contract.
    /// Internal so background services (CrewQueueWorker) can build the same
    /// shape when broadcasting <c>workitem.changed</c> from outside this class.
    /// </summary>
    internal static WorkItem MapWorkItemRow(WorkItemRow r) => MapWorkItem(r);

    private static WorkItem MapWorkItem(WorkItemRow r)
    {
        var assignee = r.AssignedToJson is null
            ? null
            : JsonSerializer.Deserialize<WorkItemAssignee>(r.AssignedToJson, JsonOpts);
        var tags = string.IsNullOrWhiteSpace(r.TagsJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(r.TagsJson, JsonOpts) ?? new List<string>());
        return new WorkItem(
            r.Id, r.RepoId, r.Provider, r.ProviderRef, r.Track, r.Type, r.State,
            r.Title, r.DescriptionMd, r.ParentId, r.FeatureRef, r.IterationRef, r.AreaPath,
            assignee, r.Points, r.Priority, tags, r.CreatedAt, r.UpdatedAt, r.RemoteRevision,
            r.AutoDispatchedAt, r.ExpectedFileChanges, r.ExecutionStatus, r.TrialCount);
    }

    private static bool MatchesAssignee(string? json, string filter)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            var a = JsonSerializer.Deserialize<WorkItemAssignee>(json, JsonOpts);
            if (a is null) return false;
            return string.Equals(a.Id, filter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.DisplayName, filter, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool MatchesTag(string? json, string tag)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            return tags is not null && tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
