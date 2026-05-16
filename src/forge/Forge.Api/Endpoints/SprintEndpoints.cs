// -----------------------------------------------------------------------
// <copyright file="SprintEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 15 — product sprint &amp; area-path management endpoints.
/// Iterations and area paths are bound to a product (Q15.1, Q15.2);
/// repo bindings (Q15.5) wire a repo's "current" / "next" iteration
/// pointer to a product iteration; saved prefixes (Q15.10) let the
/// operator one-click refresh a previously-discovered subtree.
/// </summary>
public static class SprintEndpoints
{
    public static WebApplication MapSprintEndpoints(this WebApplication app)
    {
        var products = app.MapGroup("/api/products").WithTags("Sprints");
        products.MapPost("/{id}/sprint-discovery",                  DiscoverNodes).WithName("DiscoverSprintNodes");
        products.MapGet ("/{id}/iterations",                         ListIterations).WithName("ListProductIterations");
        products.MapPost("/{id}/iterations/import",                  ImportIterations).WithName("ImportIterations");
        products.MapGet ("/{id}/area-paths",                         ListAreaPaths).WithName("ListProductAreaPaths");
        products.MapPost("/{id}/area-paths/import",                  ImportAreaPaths).WithName("ImportAreaPaths");
        products.MapPatch("/{id}/area-paths/{areaPathId}/default",   SetDefaultAreaPath).WithName("SetDefaultAreaPath");
        products.MapGet ("/{id}/saved-prefixes",                     ListSavedPrefixes).WithName("ListSavedPrefixes");
        products.MapPost("/{id}/saved-prefixes",                     SavePrefix).WithName("SavePrefix");
        products.MapDelete("/{id}/saved-prefixes/{prefixId:long}",   DeleteSavedPrefix).WithName("DeleteSavedPrefix");
        products.MapPost("/{id}/saved-prefixes/{prefixId:long}/refresh", RefreshSavedPrefix).WithName("RefreshSavedPrefix");

        var repos = app.MapGroup("/api/repos").WithTags("Sprints");
        repos.MapPost("/{id}/iteration-binding",                                      AddIterationBinding).WithName("AddIterationBinding");
        repos.MapDelete("/{id}/iteration-binding/{bindingId:long}",                  RemoveIterationBinding).WithName("RemoveIterationBinding");

        return app;
    }

    // ── Discovery (15.2) ─────────────────────────────────────────────────

    private static async Task<IResult> DiscoverNodes(
        HttpContext ctx,
        string id,
        [FromBody] SprintDiscoveryRequest req,
        ProductDbContext db,
        IAdoClassificationService ado,
        IGitHubMilestoneService github)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, id, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.RepoId))
            return Results.Problem(title: "repoId is required", statusCode: 400);

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RepoId);
        if (repo is null) return Results.Problem(title: $"Repo {req.RepoId} not found", statusCode: 404);
        if (repo.ProductId != id) return Results.Problem(title: $"Repo {req.RepoId} is not in product {id}", statusCode: 400);

        IReadOnlyList<DiscoveryNode> nodes;
        if (string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase))
        {
            nodes = await github.DiscoverAsync(repo);
        }
        else
        {
            nodes = await ado.DiscoverAsync(repo, req.Kind ?? "iterations", req.Prefix);
        }

        // Mark which nodes are already in the product so the UI can render
        // an "imported" pill without a second round-trip.
        var existing = req.Kind?.Equals("areas", StringComparison.OrdinalIgnoreCase) == true
            ? new HashSet<string>(await db.ProductAreaPaths.AsNoTracking()
                .Where(a => a.ProductId == id).Select(a => a.ProviderNodeId).ToListAsync())
            : new HashSet<string>(await db.ProductIterations.AsNoTracking()
                .Where(i => i.ProductId == id).Select(i => i.ProviderNodeId).ToListAsync());

        var marked = nodes
            .Select(n => n with { AlreadyImported = existing.Contains(n.ProviderNodeId) })
            .ToList();
        return Results.Ok(marked);
    }

    // ── List (15.7) ──────────────────────────────────────────────────────

    private static async Task<IResult> ListIterations(string id, ProductDbContext db)
    {
        if (!await db.Products.AnyAsync(p => p.Id == id))
            return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var rows = await db.ProductIterations.AsNoTracking()
            .Where(i => i.ProductId == id)
            .OrderBy(i => i.Path)
            .ToListAsync();

        // Bindings — one query, then bucket by iteration id.
        var iterIds = rows.Select(r => r.Id).ToList();
        var bindingRows = await db.RepoIterationBindings.AsNoTracking()
            .Where(b => iterIds.Contains(b.ProductIterationId))
            .ToListAsync();
        var repoNames = await db.Repos.AsNoTracking()
            .Where(r => r.ProductId == id)
            .ToDictionaryAsync(r => r.Id, r => r.DisplayName);
        var byIter = bindingRows.GroupBy(b => b.ProductIterationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IterationBinding>)g.Select(b => new IterationBinding(
                    b.RepoId,
                    repoNames.TryGetValue(b.RepoId, out var n) ? n : b.RepoId,
                    b.Role)).ToList());

        // Points are denormalised on work items; sum per iteration ref.
        var points = await db.WorkItems.AsNoTracking()
            .Where(w => iterIds.Contains(w.IterationRef))
            .GroupBy(w => w.IterationRef)
            .Select(g => new
            {
                IterationRef = g.Key,
                Total = g.Sum(w => w.Points),
                Done  = g.Where(w => w.State == "resolved" || w.State == "closed").Sum(w => w.Points),
            })
            .ToDictionaryAsync(x => x.IterationRef);

        var list = rows.Select(r => SprintMappers.MapIteration(
            r,
            byIter.TryGetValue(r.Id, out var bs) ? bs : Array.Empty<IterationBinding>(),
            points.TryGetValue(r.Id, out var p) ? p.Total : 0,
            points.TryGetValue(r.Id, out var p2) ? p2.Done : 0)).ToList();
        return Results.Ok(list);
    }

    private static async Task<IResult> ListAreaPaths(string id, ProductDbContext db)
    {
        if (!await db.Products.AnyAsync(p => p.Id == id))
            return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var rows = await db.ProductAreaPaths.AsNoTracking()
            .Where(a => a.ProductId == id)
            .OrderBy(a => a.Path)
            .ToListAsync();

        // Work-item count per area path — count rows whose AreaPath equals
        // OR is a descendant of this row's path. Pulled into memory once
        // because SQLite can't translate startswith on every key.
        var areaCounts = (await db.WorkItems.AsNoTracking()
            .Where(w => w.AreaPath != string.Empty)
            .Select(w => w.AreaPath)
            .ToListAsync())
            .GroupBy(s => s)
            .ToDictionary(g => g.Key, g => g.Count());

        var list = rows.Select(r =>
        {
            // Count items whose stored AreaPath equals this path or starts with `${path}\`.
            var prefix = r.Path + "\\";
            var n = 0;
            foreach (var (k, c) in areaCounts)
            {
                if (string.Equals(k, r.Path, StringComparison.OrdinalIgnoreCase)) n += c;
                else if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n += c;
            }
            return SprintMappers.MapArea(r, n);
        }).ToList();
        return Results.Ok(list);
    }

    // ── Import (15.3, 15.4) ──────────────────────────────────────────────

    private static async Task<IResult> ImportIterations(
        HttpContext ctx,
        string id,
        [FromBody] ImportNodesRequest req,
        ProductDbContext db,
        IAdoClassificationService ado,
        IGitHubMilestoneService github,
        IHubContext<ForgeHub> hub)
        => await ImportNodes(ctx, id, req, kind: "iterations", db, ado, github, hub);

    private static async Task<IResult> ImportAreaPaths(
        HttpContext ctx,
        string id,
        [FromBody] ImportNodesRequest req,
        ProductDbContext db,
        IAdoClassificationService ado,
        IGitHubMilestoneService github,
        IHubContext<ForgeHub> hub)
        => await ImportNodes(ctx, id, req, kind: "areas", db, ado, github, hub);

    private static async Task<IResult> ImportNodes(
        HttpContext ctx,
        string productId,
        ImportNodesRequest req,
        string kind,
        ProductDbContext db,
        IAdoClassificationService ado,
        IGitHubMilestoneService github,
        IHubContext<ForgeHub> hub)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, productId, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.RepoId) || req.ProviderNodeIds is null)
            return Results.Problem(title: "repoId and providerNodeIds are required", statusCode: 400);

        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == req.RepoId);
        if (repo is null) return Results.Problem(title: $"Repo {req.RepoId} not found", statusCode: 404);
        if (repo.ProductId != productId)
            return Results.Problem(title: $"Repo {req.RepoId} is not in product {productId}", statusCode: 400);

        // Pull the live discovery so we can extract attributes for each
        // imported id (the request body carries only the ids).
        var live = string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase)
            ? await github.DiscoverAsync(repo)
            : await ado.DiscoverAsync(repo, kind, prefix: null);
        var liveById = live.ToDictionary(n => n.ProviderNodeId);

        var requested = req.ProviderNodeIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        var now = DateTimeOffset.UtcNow;

        if (kind == "iterations")
        {
            var existing = await db.ProductIterations
                .Where(i => i.ProductId == productId && requested.Contains(i.ProviderNodeId))
                .ToDictionaryAsync(i => i.ProviderNodeId);
            foreach (var nodeId in requested)
            {
                liveById.TryGetValue(nodeId, out var node);
                var attrs = node?.Attributes ?? new DiscoveryNodeAttributes();
                var state = (attrs.TimeFrame ?? string.Empty) switch
                {
                    "past" => "past",
                    "current" => "current",
                    "future" => "future",
                    _ => "future",
                };
                if (existing.TryGetValue(nodeId, out var row))
                {
                    if (node is not null) { row.Path = node.Path; row.Name = node.Name; }
                    row.StartDate = attrs.StartDate;
                    row.FinishDate = attrs.FinishDate;
                    // Live re-import always restores state from the provider —
                    // a previously-orphaned row that reappears upstream is
                    // no longer orphaned.
                    row.State = state;
                    row.RefreshedAt = now;
                }
                else
                {
                    db.ProductIterations.Add(new ProductIterationRow
                    {
                        Id = $"pi-{Guid.NewGuid():N}",
                        ProductId = productId,
                        ProviderKind = repo.Kind,
                        ProviderOrg = repo.AdoOrg ?? repo.GhOwner ?? string.Empty,
                        ProviderProject = repo.AdoProject ?? repo.GhRepo ?? string.Empty,
                        ProviderNodeId = nodeId,
                        Path = node?.Path ?? nodeId,
                        Name = node?.Name ?? nodeId,
                        StartDate = attrs.StartDate,
                        FinishDate = attrs.FinishDate,
                        State = state,
                        ImportedAt = now,
                        RefreshedAt = now,
                    });
                }
            }
        }
        else
        {
            var existing = await db.ProductAreaPaths
                .Where(a => a.ProductId == productId && requested.Contains(a.ProviderNodeId))
                .ToDictionaryAsync(a => a.ProviderNodeId);
            foreach (var nodeId in requested)
            {
                liveById.TryGetValue(nodeId, out var node);
                var path = node?.Path ?? nodeId;
                var parentPath = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : null;
                if (existing.TryGetValue(nodeId, out var row))
                {
                    row.Path = path;
                    row.ParentPath = parentPath;
                }
                else
                {
                    db.ProductAreaPaths.Add(new ProductAreaPathRow
                    {
                        Id = $"ap-{Guid.NewGuid():N}",
                        ProductId = productId,
                        ProviderKind = repo.Kind,
                        ProviderOrg = repo.AdoOrg ?? string.Empty,
                        ProviderProject = repo.AdoProject ?? string.Empty,
                        ProviderNodeId = nodeId,
                        Path = path,
                        ParentPath = parentPath,
                        IsDefaultFilter = false,
                        ImportedAt = now,
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        // AuditMiddleware records the request automatically; the SignalR
        // event drives the SprintManager UI to refresh.
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = kind == "iterations" ? "iterations-imported" : "areas-imported", productId, count = requested.Count });

        if (kind == "iterations") return await ListIterations(productId, db);
        return await ListAreaPaths(productId, db);
    }

    // ── Default area path (15.2) ─────────────────────────────────────────

    private static async Task<IResult> SetDefaultAreaPath(
        HttpContext ctx,
        string id,
        string areaPathId,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, id, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        var row = await db.ProductAreaPaths.FirstOrDefaultAsync(a => a.Id == areaPathId && a.ProductId == id);
        if (row is null) return Results.Problem(title: $"Area path {areaPathId} not found in product {id}", statusCode: 404);

        // Q15.2 — at most one default per product. Flip everything else off
        // first so we never have two trues simultaneously.
        var others = await db.ProductAreaPaths
            .Where(a => a.ProductId == id && a.IsDefaultFilter && a.Id != areaPathId)
            .ToListAsync();
        foreach (var o in others) o.IsDefaultFilter = false;

        // Toggle the target.
        row.IsDefaultFilter = !row.IsDefaultFilter;
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = "default-area-changed", productId = id, areaPathId, isDefault = row.IsDefaultFilter });
        return await ListAreaPaths(id, db);
    }

    // ── Saved prefixes (15.10) ───────────────────────────────────────────

    private static async Task<IResult> ListSavedPrefixes(string id, ProductDbContext db)
    {
        if (!await db.Products.AnyAsync(p => p.Id == id))
            return Results.Problem(title: $"Product {id} not found", statusCode: 404);
        var rows = await db.ProductSavedPrefixes.AsNoTracking()
            .Where(p => p.ProductId == id)
            .OrderBy(p => p.Prefix)
            .ToListAsync();
        var repoNames = await db.Repos.AsNoTracking()
            .Where(r => r.ProductId == id)
            .ToDictionaryAsync(r => r.Id, r => r.DisplayName);
        var list = rows.Select(p => SprintMappers.MapSavedPrefix(p,
            repoNames.TryGetValue(p.RepoId, out var n) ? n : p.RepoId)).ToList();
        return Results.Ok(list);
    }

    private static async Task<IResult> SavePrefix(
        HttpContext ctx,
        string id,
        [FromBody] SavePrefixRequest req,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, id, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;
        if (req is null || string.IsNullOrWhiteSpace(req.Prefix) || string.IsNullOrWhiteSpace(req.RepoId))
            return Results.Problem(title: "prefix and repoId are required", statusCode: 400);

        var kind = (req.Kind ?? "iterations").ToLowerInvariant();
        if (kind != "iterations" && kind != "areas")
            return Results.Problem(title: "kind must be 'iterations' or 'areas'", statusCode: 400);

        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == req.RepoId);
        if (repo is null || repo.ProductId != id)
            return Results.Problem(title: "Repo does not belong to product", statusCode: 400);

        var existing = await db.ProductSavedPrefixes.FirstOrDefaultAsync(p =>
            p.ProductId == id && p.Prefix == req.Prefix && p.Kind == kind);
        if (existing is not null) return Results.Conflict(new { id = existing.Id });

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "unknown";
        var row = new ProductSavedPrefixRow
        {
            ProductId = id,
            Prefix = req.Prefix.Trim(),
            Kind = kind,
            RepoId = req.RepoId,
            AddedBy = caller,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.ProductSavedPrefixes.Add(row);
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = "prefix-saved", productId = id, prefixId = row.Id });
        return Results.Created($"/api/products/{id}/saved-prefixes/{row.Id}",
            SprintMappers.MapSavedPrefix(row, repo.DisplayName));
    }

    private static async Task<IResult> DeleteSavedPrefix(
        HttpContext ctx,
        string id,
        long prefixId,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, id, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;
        var row = await db.ProductSavedPrefixes.FirstOrDefaultAsync(p => p.Id == prefixId && p.ProductId == id);
        if (row is null) return Results.NotFound();
        db.ProductSavedPrefixes.Remove(row);
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = "prefix-removed", productId = id, prefixId });
        return Results.NoContent();
    }

    private static async Task<IResult> RefreshSavedPrefix(
        HttpContext ctx,
        string id,
        long prefixId,
        ProductDbContext db,
        IAdoClassificationService ado,
        IGitHubMilestoneService github,
        IHubContext<ForgeHub> hub)
    {
        var auth = RequireRole.CheckForProduct(ctx, db, id, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        var row = await db.ProductSavedPrefixes.FirstOrDefaultAsync(p => p.Id == prefixId && p.ProductId == id);
        if (row is null) return Results.NotFound();
        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == row.RepoId);
        if (repo is null) return Results.Problem(title: "Repo no longer exists", statusCode: 410);

        // Discovery → import every node that matches the prefix. Emits the
        // diff vs the previous import so the chip can render a +N badge.
        var live = string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase)
            ? await github.DiscoverAsync(repo)
            : await ado.DiscoverAsync(repo, row.Kind, row.Prefix);

        var liveIds = new HashSet<string>(live.Select(n => n.ProviderNodeId));

        // Q15.4 — orphan detection. Find existing rows in the prefix scope
        // whose upstream node has disappeared, and flip them to
        // provider-orphan without dropping any bindings.
        int removedCount = 0;
        int addedCount = 0;

        if (row.Kind == "areas")
        {
            var scoped = await db.ProductAreaPaths
                .Where(a => a.ProductId == id && a.Path.StartsWith(row.Prefix))
                .ToListAsync();
            var existingIds = new HashSet<string>(scoped.Select(s => s.ProviderNodeId));
            addedCount = liveIds.Count(n => !existingIds.Contains(n));
            // Orphan flag lives in AttributesJson for areas (no State column).
            foreach (var a in scoped.Where(s => !liveIds.Contains(s.ProviderNodeId)))
            {
                a.AttributesJson = "{\"orphan\":true}";
                removedCount++;
            }
        }
        else
        {
            var scoped = await db.ProductIterations
                .Where(i => i.ProductId == id && i.Path.StartsWith(row.Prefix))
                .ToListAsync();
            var existingIds = new HashSet<string>(scoped.Select(s => s.ProviderNodeId));
            addedCount = liveIds.Count(n => !existingIds.Contains(n));
            foreach (var i in scoped.Where(s => !liveIds.Contains(s.ProviderNodeId)))
            {
                i.State = "provider-orphan";
                i.RefreshedAt = DateTimeOffset.UtcNow;
                removedCount++;
            }
        }
        await db.SaveChangesAsync();

        // Re-import every live node — this restores state for ones that
        // reappeared upstream and creates new ones.
        var toImport = live.Select(n => n.ProviderNodeId).ToList();
        if (toImport.Count > 0)
        {
            await ImportNodes(
                ctx,
                productId: id,
                req: new ImportNodesRequest(repo.Id, toImport, SavedFromPrefix: row.Prefix),
                kind: row.Kind,
                db, ado, github, hub);
        }

        row.LastRefreshedAt = DateTimeOffset.UtcNow;
        row.LastDeltaJson = SprintMappers.SerializeDelta(new SavedPrefixDelta(addedCount, removedCount, 0));
        await db.SaveChangesAsync();

        var repoName = repo.DisplayName;
        return Results.Ok(SprintMappers.MapSavedPrefix(row, repoName));
    }

    // ── Repo iteration bindings (15.5) ───────────────────────────────────

    private static async Task<IResult> AddIterationBinding(
        HttpContext ctx,
        string id,
        [FromBody] IterationBindingRequest req,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.ProductIterationId) || string.IsNullOrWhiteSpace(req.Role))
            return Results.Problem(title: "productIterationId and role are required", statusCode: 400);

        var role = req.Role.ToLowerInvariant();
        if (role != "current" && role != "next" && role != "reference")
            return Results.Problem(title: "role must be 'current' | 'next' | 'reference'", statusCode: 400);

        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var auth = RequireRole.CheckForProduct(ctx, db, repo.ProductId, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        var iter = await db.ProductIterations.FirstOrDefaultAsync(i => i.Id == req.ProductIterationId);
        if (iter is null) return Results.Problem(title: $"Iteration {req.ProductIterationId} not found", statusCode: 404);
        if (iter.ProductId != repo.ProductId)
            return Results.Problem(title: "Iteration is not in this repo's product", statusCode: 400);

        // Q15.5 — only one binding per (repo, role) for current/next.
        if (role is "current" or "next")
        {
            var dup = await db.RepoIterationBindings
                .FirstOrDefaultAsync(b => b.RepoId == id && b.Role == role);
            if (dup is not null && dup.ProductIterationId != req.ProductIterationId)
            {
                return Results.Conflict(new
                {
                    error = "binding_role_taken",
                    existingBindingId = dup.Id,
                    existingIterationId = dup.ProductIterationId,
                });
            }
            if (dup is not null) return Results.Ok(MapBinding(dup));
        }

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var row = new RepoIterationBindingRow
        {
            RepoId = id,
            ProductIterationId = req.ProductIterationId,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = caller,
        };
        db.RepoIterationBindings.Add(row);
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = "binding-added", productId = repo.ProductId, repoId = id, bindingId = row.Id });
        return Results.Created($"/api/repos/{id}/iteration-binding/{row.Id}", MapBinding(row));
    }

    private static async Task<IResult> RemoveIterationBinding(
        HttpContext ctx,
        string id,
        long bindingId,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var auth = RequireRole.CheckForProduct(ctx, db, repo.ProductId, RequireRole.ProductOwner, "tech-lead");
        if (auth is not null) return auth;

        var row = await db.RepoIterationBindings.FirstOrDefaultAsync(b => b.Id == bindingId && b.RepoId == id);
        if (row is null) return Results.NotFound();
        db.RepoIterationBindings.Remove(row);
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("sprint.changed", new { kind = "binding-removed", productId = repo.ProductId, repoId = id, bindingId });
        return Results.NoContent();
    }

    private static object MapBinding(RepoIterationBindingRow b)
        => new { id = b.Id, repoId = b.RepoId, productIterationId = b.ProductIterationId, role = b.Role, createdAt = b.CreatedAt };
}
