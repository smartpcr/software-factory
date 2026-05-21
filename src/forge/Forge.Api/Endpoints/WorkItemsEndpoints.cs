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
        grp.MapGet("/{id}/workstream-link", GetWorkstreamLink).WithName("GetWorkItemWorkstreamLink");

        return app;
    }

    /// <summary>
    /// Resolve a work item to its parent workstream + story spawn so the UI
    /// can deep-link from the work-items view back to the workstream view.
    /// Resolution: (1) check for a WorkstreamRow whose Id matches the work
    /// item or whose PrsJson references it; (2) walk the WorkItemRow.ParentId
    /// chain looking for a parent that's also a workstream. Returns 404 when
    /// the work item doesn't belong to any spawn (ad-hoc / external items).
    /// </summary>
    private static async Task<IResult> GetWorkstreamLink(
        string id,
        ControlDbContext control,
        ProductDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return Results.NotFound();

        // Pass (b): direct workstream match (workstream id == work item id)
        // OR PrsJson contains a PR linked to this work item.
        var direct = await db.Workstreams
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, ct);
        if (direct is null)
        {
            // Search workstreams whose PrsJson contains a PR with this WorkItemId.
            var prRow = await db.Prs.AsNoTracking()
                .FirstOrDefaultAsync(p => p.WorkItemId == id, ct);
            if (prRow is not null)
            {
                direct = await db.Workstreams.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.PrsJson.Contains(prRow.Id), ct);
            }
        }

        // Pass (a): walk ParentId chain. Cap depth to avoid runaway loops.
        if (direct is null)
        {
            var current = id;
            for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(current); depth++)
            {
                var parent = await db.WorkItems.AsNoTracking()
                    .Where(w => w.Id == current)
                    .Select(w => w.ParentId)
                    .FirstOrDefaultAsync(ct);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                var ws = await db.Workstreams.AsNoTracking().FirstOrDefaultAsync(w => w.Id == parent, ct);
                if (ws is not null) { direct = ws; break; }
                current = parent;
            }
        }

        if (direct is null) return Results.NotFound();
        if (string.IsNullOrEmpty(direct.StorySpawnId)) return Results.NotFound();

        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == direct.StorySpawnId, ct);
        if (spawn is null) return Results.NotFound();

        return Results.Ok(new
        {
            storyId = spawn.StoryId,
            workstreamId = direct.Id,
            spawnId = spawn.Id,
        });
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
        ControlDbContext control,
        ProductDbContext db,
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
            var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
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
        ControlDbContext control,
        ProductDbContext db,
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

        // Scope to repos belonging to the active product. Honors `?productId=`,
        // the `X-Forge-Product-Id` header, and the default-product fallback —
        // matches CardsEndpoints / PrsEndpoints / WorkstreamsEndpoints. Pass
        // `productId=all` to bypass scoping.
        var scope = await ProductScope.ResolveAsync(ctx, control, ct);
        if (!scope.IsAll)
        {
            var repoIds = scope.RepoIds!;
            query = query.Where(w => w.RepoId != null && repoIds.Contains(w.RepoId));
        }

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

        // Phase 25 — batch-lookup linked PRs for the page so the UI gets
        // primaryPrId / primaryPrStatus without N+1 queries.
        var pagedIds = paged.Select(r => r.Id).ToList();
        var prMap = await LookupPrimaryPrsAsync(db, pagedIds, ct);

        return Results.Ok(paged.Select(r =>
        {
            prMap.TryGetValue(r.Id, out var pr);
            return MapWorkItem(r, pr.PrId, pr.PrCol);
        }).ToList());
    }

    // ----- GET ------------------------------------------------------------

    private static async Task<IResult> Get(string id, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
        var row = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);
        var (prId, prCol) = await LookupPrimaryPrAsync(db, id, ct);
        return Results.Ok(MapWorkItem(row, prId, prCol));
    }

    // ----- CREATE ---------------------------------------------------------

    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateWorkItemRequest req,
        ControlDbContext control,
        ProductDbContext db,
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

        // Phase 80 — a product now owns one or more repos and every work item is
        // associated with one. Resolve the repo from the request → the active
        // repo (X-Forge-Repo-Id) → the scoped product's default repo, so a
        // created item picks up a repo by default instead of landing repo-less.
        var repoId = string.IsNullOrWhiteSpace(req.RepoId) ? null : req.RepoId!.Trim();
        if (string.IsNullOrEmpty(repoId)) repoId = RepoScope.ActiveRepoId(ctx);
        if (string.IsNullOrEmpty(repoId))
        {
            var scopedProductRaw = ProductScope.Read(ctx);
            ProductRow? prod;
            if (!string.IsNullOrEmpty(scopedProductRaw)
                && !string.Equals(scopedProductRaw, ProductScope.AllScope, StringComparison.OrdinalIgnoreCase))
            {
                prod = await control.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == scopedProductRaw || p.Slug == scopedProductRaw, ct);
            }
            else
            {
                prod = await control.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == ForgeSeed.DefaultProductId, ct)
                    ?? await control.Products.AsNoTracking().FirstOrDefaultAsync(ct);
            }
            repoId = prod?.DefaultRepoId;
        }
        if (!string.IsNullOrEmpty(repoId))
        {
            // Validate the resolved repo is a live registry entry (FK would dangle otherwise).
            var exists = await control.RepoRegistry.AsNoTracking()
                .AnyAsync(r => r.Id == repoId && r.DeletedAt == null, ct);
            if (!exists) return Results.Problem(title: $"Repo {repoId} not found", statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var row = new WorkItemRow
        {
            Id = req.Id,
            RepoId = string.IsNullOrEmpty(repoId) ? null : repoId,
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
            var budget = await WorkItemRules.ValidatePlanTreeBudgetAsync(control, db, row, parent, ct);
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
        ControlDbContext control,
        ProductDbContext db,
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
        var pointsChanged = false;
        var newPoints = row.Points;
        if (req.Points is int p)
        {
            if (p < 0) return Results.Problem(title: "Points must be ≥ 0", statusCode: 400);
            pointsChanged = row.Points != p;
            row.Points = p;
            newPoints = p;
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
            // Phase 80.B — the control registry is the global repo index.
            var exists = await control.RepoRegistry.AsNoTracking().AnyAsync(r => r.Id == req.RepoId && r.DeletedAt == null, ct);
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

        // Cascade: points are the user's "size of work" knob, and the
        // deliverable loop's iteration cap derives directly from points
        // (`DeliverableLoop.PointsToMaxIter`). When the operator bumps points
        // on a stalled work item, also bump MaxIterOverride on every open
        // queue row so the running loop's next tick sees the new cap.
        if (pointsChanged)
        {
            var newCap = Forge.Api.Services.DeliverableLoop.PointsToMaxIter(newPoints);
            var openQueueRows = await db.TaskQueue
                .Where(q => q.WorkItemId == row.Id && q.State != "completed")
                .ToListAsync(ct);
            foreach (var q in openQueueRows)
            {
                q.MaxIterOverride = newCap;
            }
        }

        await db.SaveChangesAsync(ct);

        var (prId, prCol) = await LookupPrimaryPrAsync(db, row.Id, ct);
        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "updated", workItem = MapWorkItem(row, prId, prCol) }, ct);
        return Results.Ok(MapWorkItem(row, prId, prCol));
    }

    // ----- DELETE ---------------------------------------------------------

    private static async Task<IResult> Delete(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        bool? force,
        CancellationToken ct)
    {
        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        if (force == true)
        {
            // Hard delete — gated to PO.
            var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
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
        var (prId, prCol) = await LookupPrimaryPrAsync(db, row.Id, ct);
        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "closed", workItem = MapWorkItem(row, prId, prCol) }, ct);
        return Results.Ok(MapWorkItem(row, prId, prCol));
    }

    // ----- MOVE -----------------------------------------------------------

    private static async Task<IResult> Move(
        HttpContext ctx,
        string id,
        [FromBody] MoveWorkItemRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Work item {id} not found", statusCode: 404);
        if (req is null) return Results.Problem(title: "body is required", statusCode: 400);

        var previousState = row.State;
        if (req.State is not null)
        {
            if (!WorkItemRules.AllStates.Contains(req.State))
                return Results.Problem(title: $"Unknown state '{req.State}'", statusCode: 400);
            row.State = req.State;
        }
        if (req.IterationRef is not null) row.IterationRef = req.IterationRef;

        // Phase 22 — state transition hooks for assignment history.
        if (req.State is not null)
        {
            RecordAssignmentTransition(db, row, previousState, req.State);
        }

        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.RemoteRevision = NewRevision();
        await db.SaveChangesAsync(ct);
        var (prId, prCol) = await LookupPrimaryPrAsync(db, row.Id, ct);
        await hub.Clients.All.SendAsync("workitem.changed", new { kind = "moved", workItem = MapWorkItem(row, prId, prCol) }, ct);
        return Results.Ok(MapWorkItem(row, prId, prCol));
    }

    // ----- REPARENT -------------------------------------------------------

    private static async Task<IResult> Reparent(
        HttpContext ctx,
        string id,
        [FromBody] ReparentWorkItemRequest req,
        ControlDbContext control,
        ProductDbContext db,
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
        var cycleAt = await WorkItemRules.DetectCycleAsync(control, db, row.Id, newParent?.Id, ct);
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
        var (prId, prCol) = await LookupPrimaryPrAsync(db, row.Id, ct);
        return Results.Ok(MapWorkItem(row, prId, prCol));
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
        ControlDbContext control,
        ProductDbContext db,
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

        // Skip update when upstream ChangedDate is not newer than our last update.
        if (remote.UpdatedAt > DateTimeOffset.MinValue
            && row.UpdatedAt >= remote.UpdatedAt)
        {
            var (prId0, prCol0) = await LookupPrimaryPrAsync(db, row.Id, ct);
            return Results.Ok(MapWorkItem(row, prId0, prCol0));
        }

        // Apply remote fields. Skip ParentId when remote omits one (matches
        // WorkItemSyncService — preserves operator's local re-parent).
        var forgeOwned = row.AutoDispatchedAt.HasValue
            || row.ExecutionStatus != ExecutionStatuses.Idle;

        // Always sync upstream-sourced metadata.
        row.Track = remote.Track;
        row.Type = remote.Type;
        row.Title = remote.Title;
        row.DescriptionMd = remote.DescriptionMd;
        if (!string.IsNullOrEmpty(remote.ParentId)) row.ParentId = remote.ParentId;
        row.IterationRef = remote.IterationRef;
        row.AreaPath = remote.AreaPath;
        row.TagsJson = JsonSerializer.Serialize(remote.Tags, JsonOpts);
        row.RemoteRevision = remote.RemoteRevision ?? NewRevision();

        // Only overwrite Forge-owned fields when Forge hasn't touched the item.
        if (!forgeOwned)
        {
            row.State = remote.State;
            if (remote.AssignedTo is not null)
            {
                row.AssignedToJson = JsonSerializer.Serialize(remote.AssignedTo, JsonOpts);
            }
            row.Points = remote.Points > 0 ? remote.Points : 2;
            row.Priority = remote.Priority;
        }

        row.UpdatedAt = remote.UpdatedAt > DateTimeOffset.MinValue ? remote.UpdatedAt : DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        var (prId, prCol) = await LookupPrimaryPrAsync(db, row.Id, ct);
        await hub.Clients.All.SendAsync("workitem.changed",
            new { kind = "updated", workItem = MapWorkItem(row, prId, prCol) }, ct);

        return Results.Ok(MapWorkItem(row, prId, prCol));
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
        ControlDbContext control,
        ProductDbContext db,
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
            (prRepoName, prProjectName) = PrCoordsResolver.Resolve(repo, repoKind);
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

        Forge.Api.Services.SystemNotify.PrCreated(id, pr.Id);
        return Results.Ok(new { prId = pr.Id, prTitle = pr.Title });
    }

    // ----- helpers --------------------------------------------------------

    private static string NewRevision() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Map a <see cref="WorkItemRow"/> to the public <see cref="WorkItem"/> contract.
    /// Internal so background services (CrewQueueWorker) can build the same
    /// shape when broadcasting <c>workitem.changed</c> from outside this class.
    /// </summary>
    internal static WorkItem MapWorkItemRow(WorkItemRow r, string? primaryPrId = null, string? primaryPrStatus = null)
        => MapWorkItem(r, primaryPrId, primaryPrStatus);

    /// <summary>
    /// Look up the primary linked PR for a single work item. Returns (prId, prCol)
    /// or (null, null) when no PR is linked. Falls back to transcript events when
    /// no PrRow exists (covers agent-created PRs that haven't been synced yet).
    /// </summary>
    internal static async Task<(string? PrId, string? PrCol)> LookupPrimaryPrAsync(
        DbContext db, string workItemId, CancellationToken ct = default)
    {
        var pr = await db.Set<PrRow>().AsNoTracking()
            .Where(p => p.WorkItemId == workItemId)
            .OrderByDescending(p => p.Sort)
            .Select(p => new { p.Id, p.Col })
            .FirstOrDefaultAsync(ct);
        if (pr is not null) return (pr.Id, pr.Col);

        // Fallback: check transcript for task.pr-opened events (agent created
        // a PR on the remote but it hasn't been synced into the local prs table).
        // NOTE: Order in-memory because SQLite cannot ORDER BY DateTimeOffset.
        var evtRows = await db.Set<TaskTranscriptEventRow>().AsNoTracking()
            .Where(e => e.WorkItemId == workItemId && e.Kind == "task.pr-opened")
            .ToListAsync(ct);
        var evt = evtRows
            .OrderByDescending(e => e.At)
            .Select(e => e.PayloadJson)
            .FirstOrDefault();
        if (evt is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(evt);
                var prId = doc.RootElement.TryGetProperty("prId", out var pid) ? pid.GetString() : null;
                // Work item is in-review = PR exists; default to "review" phase.
                return (prId, "review");
            }
            catch { /* malformed payload */ }
        }
        return (null, null);
    }

    /// <summary>
    /// Batch-lookup primary PRs for a set of work item IDs. Returns a dictionary
    /// keyed by work-item ID → (prId, prCol). Falls back to transcript events for
    /// items without a linked PrRow.
    /// </summary>
    internal static async Task<Dictionary<string, (string PrId, string PrCol)>> LookupPrimaryPrsAsync(
        DbContext db, IEnumerable<string> workItemIds, CancellationToken ct = default)
    {
        var ids = workItemIds.ToList();
        if (ids.Count == 0) return new Dictionary<string, (string, string)>();

        var prRows = await db.Set<PrRow>().AsNoTracking()
            .Where(p => p.WorkItemId != null && ids.Contains(p.WorkItemId))
            .Select(p => new { p.WorkItemId, p.Id, p.Col, p.Sort })
            .ToListAsync(ct);

        var result = prRows
            .GroupBy(p => p.WorkItemId!)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.OrderByDescending(p => p.Sort).First();
                    return (best.Id, best.Col);
                });

        // Fallback: for items not found in prs, check transcript events.
        var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var events = await db.Set<TaskTranscriptEventRow>().AsNoTracking()
                .Where(e => missing.Contains(e.WorkItemId) && e.Kind == "task.pr-opened")
                .ToListAsync(ct);

            foreach (var grp in events.GroupBy(e => e.WorkItemId))
            {
                var latest = grp.OrderByDescending(e => e.At).First();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(latest.PayloadJson);
                    var prId = doc.RootElement.TryGetProperty("prId", out var pid) ? pid.GetString() : null;
                    if (prId is not null)
                    {
                        result[grp.Key] = (prId, "review");
                    }
                }
                catch { /* skip malformed */ }
            }
        }

        return result;
    }

    private static WorkItem MapWorkItem(WorkItemRow r, string? primaryPrId = null, string? primaryPrStatus = null)
    {
        var assignee = r.AssignedToJson is null
            ? null
            : JsonSerializer.Deserialize<WorkItemAssignee>(r.AssignedToJson, JsonOpts);
        var tags = string.IsNullOrWhiteSpace(r.TagsJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(r.TagsJson, JsonOpts) ?? new List<string>());
        AssignedAgent? assignedAgent = null;
        if (!string.IsNullOrWhiteSpace(r.AssignedAgentJson))
        {
            try { assignedAgent = JsonSerializer.Deserialize<AssignedAgent>(r.AssignedAgentJson, JsonOpts); }
            catch { /* malformed JSON — return null gracefully */ }
        }
        return new WorkItem(
            r.Id, r.RepoId, r.Provider, r.ProviderRef, r.Track, r.Type, r.State,
            r.Title, r.DescriptionMd, r.ParentId, r.FeatureRef, r.IterationRef, r.AreaPath,
            assignee, r.Points, r.Priority, tags, r.CreatedAt, r.UpdatedAt, r.RemoteRevision,
            r.AutoDispatchedAt, r.ExpectedFileChanges, r.ExecutionStatus, r.TrialCount,
            assignedAgent, primaryPrId, primaryPrStatus);
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

    // ── Phase 22 — assignment transition hooks ──────────────────────────

    /// <summary>
    /// Records an <see cref="AgentAssignmentRow"/> when a state transition
    /// triggers an assignment event (Q22.3 steps 4-6). Called from both
    /// <see cref="Move"/> and the queue worker's PR-open path.
    /// </summary>
    internal static void RecordAssignmentTransition(
        DbContext db, WorkItemRow row, string fromState, string toState)
    {
        // → in-review (PR opened): record pr-opened, do NOT change AssignedAgentJson.
        if (toState is "in-review")
        {
            var agentIds = ParseAgentIds(row.AssignedAgentJson);
            db.Set<AgentAssignmentRow>().Add(new AgentAssignmentRow
            {
                WorkItemId = row.Id,
                Kind = AgentAssignmentKinds.PrOpened,
                AgentIdsJson = JsonSerializer.Serialize(agentIds, JsonOpts),
                FromExecutionStatus = fromState,
                ToExecutionStatus = toState,
                At = DateTimeOffset.UtcNow,
            });
            return;
        }

        // → resolved / closed: record completed AND null out AssignedAgentJson.
        if (toState is "resolved" or "closed")
        {
            var agentIds = ParseAgentIds(row.AssignedAgentJson);
            db.Set<AgentAssignmentRow>().Add(new AgentAssignmentRow
            {
                WorkItemId = row.Id,
                Kind = AgentAssignmentKinds.Completed,
                AgentIdsJson = JsonSerializer.Serialize(agentIds, JsonOpts),
                FromExecutionStatus = fromState,
                ToExecutionStatus = toState,
                At = DateTimeOffset.UtcNow,
            });
            row.AssignedAgentJson = null;
        }
    }

    /// <summary>Parse agent IDs from the <c>AssignedAgentJson</c> field (best-effort).</summary>
    internal static List<string> ParseAgentIds(string? assignedAgentJson)
    {
        if (string.IsNullOrWhiteSpace(assignedAgentJson)) return new List<string>();
        try
        {
            var agent = JsonSerializer.Deserialize<AssignedAgent>(assignedAgentJson, JsonOpts);
            return agent?.Agents?.ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }
}
