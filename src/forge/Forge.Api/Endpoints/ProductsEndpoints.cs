// -----------------------------------------------------------------------
// <copyright file="ProductsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 8.4 — products + repos CRUD.
///
/// Q5 invariant (1:N) is enforced both at the DB layer (UNIQUE indexes on
/// repos.RemoteUrl / repos.CloneRoot) and here at the API layer with explicit
/// 409 responses that include the conflicting product / repo id so the UI can
/// offer "open existing product" as a recovery action.
/// </summary>
public static class ProductsEndpoints
{
    public static WebApplication MapProductsEndpoints(this WebApplication app)
    {
        var products = app.MapGroup("/api/products").WithTags("Products");
        products.MapGet("/", ListProducts).WithName("ListProducts");
        products.MapGet("/{id}", GetProduct).WithName("GetProduct");
        products.MapPost("/", CreateProduct).WithName("CreateProduct");
        products.MapPatch("/{id}", PatchProduct).WithName("PatchProduct");
        products.MapDelete("/{id}", DeleteProduct).WithName("DeleteProduct");
        products.MapGet("/{id}/repos", ListReposForProduct).WithName("ListProductRepos");
        products.MapPost("/{id}/repos", AttachRepo).WithName("AttachRepo");
        products.MapGet("/{id}/stats", GetProductStats).WithName("GetProductStats");

        var repos = app.MapGroup("/api/repos").WithTags("Repos");
        repos.MapGet("/", ListAllRepos).WithName("ListAllRepos");
        repos.MapGet("/{id}", GetRepo).WithName("GetRepo");
        repos.MapPatch("/{id}", PatchRepo).WithName("PatchRepo");
        repos.MapPost("/{id}/clone", CloneRepo).WithName("CloneRepo");
        repos.MapDelete("/{id}", DeleteRepo).WithName("DeleteRepo");

        // Phase 23 — scheduled sync endpoints.
        repos.MapPost("/{id}/sync-work-items", TriggerRepoSync).WithName("TriggerRepoSync");
        repos.MapGet("/{id}/sync-work-items/runs", GetRepoSyncRuns).WithName("GetRepoSyncRuns");
        repos.MapGet("/{id}/sync-work-items/runs/{runId}/items", GetSyncRunItems).WithName("GetSyncRunItems");
        repos.MapGet("/{id}/sync-work-items/status", GetRepoSyncStatus).WithName("GetRepoSyncStatus");

        return app;
    }

    // PATCH /api/repos/{id} — Phase 10 per-repo override editor.
    private static async Task<IResult> PatchRepo(
        HttpContext ctx,
        string id,
        [FromBody] UpdateRepoRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        if (req.DisplayName is not null) row.DisplayName = req.DisplayName.Trim();
        if (req.CloneRoot is not null)
        {
            var clean = req.CloneRoot.Trim();
            if (!Path.IsPathFullyQualified(clean))
            {
                return Results.Problem(title: "cloneRoot must be an absolute path", statusCode: 400);
            }
            // Q2 — same clone root cannot host two repos.
            if (await db.Repos.AnyAsync(r => r.CloneRoot == clean && r.Id != id))
            {
                return Results.Problem(title: "Clone root already in use", statusCode: 409);
            }
            row.CloneRoot = clean;
        }
        if (req.DefaultBranch is not null) row.DefaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim();
        if (req.AdoOrg is not null)     row.AdoOrg     = NullIfEmpty(req.AdoOrg);
        if (req.AdoProject is not null) row.AdoProject = NullIfEmpty(req.AdoProject);
        if (req.GhOwner is not null)    row.GhOwner    = NullIfEmpty(req.GhOwner);
        if (req.GhRepo is not null)     row.GhRepo     = NullIfEmpty(req.GhRepo);

        // Phase 23 — area-path scope + agent-ready default team.
        if (req.AreaPaths is not null)
        {
            row.AreaPathsJson = req.AreaPaths.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(req.AreaPaths);
        }
        if (req.AgentReadyDefaultTeam is not null)
        {
            row.AgentReadyDefaultTeam = NullIfEmpty(req.AgentReadyDefaultTeam);
        }

        await db.SaveChangesAsync();
        var repo = MapRepo(row);
        await hub.Clients.All.SendAsync("repo.changed", new { kind = "patched", repo });
        return Results.Ok(repo);
    }

    // POST /api/repos/{id}/clone — ensure the repo's cloneRoot is checked out.
    // Used by the Products view "Edit repo" modal so that updating the local
    // path triggers an automatic clone when the directory is missing.
    //
    // If the cloneRoot directory already exists we trust the operator's choice
    // and skip — no clone, no remote-URL match check. Workstream creation
    // (WorkstreamsEndpoints.Create) still uses the stricter EnsureClonedAsync
    // path that validates the existing checkout.
    private static async Task<IResult> CloneRepo(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IRepoCloneService cloneSvc,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        if (Directory.Exists(row.CloneRoot))
        {
            return Results.Ok(new { outcome = "skipped", message = "cloneRoot already exists" });
        }

        var result = await cloneSvc.EnsureClonedAsync(row, ct);
        return result.Outcome switch
        {
            RepoCloneOutcome.Cloned => Results.Ok(new { outcome = "cloned", message = result.Message }),
            RepoCloneOutcome.Skipped => Results.Ok(new { outcome = "skipped", message = result.Message }),
            RepoCloneOutcome.Mismatched => Results.Problem(title: "Repo cloneRoot mismatch", detail: result.Message, statusCode: 409),
            RepoCloneOutcome.Failed => Results.Problem(title: "Clone failed", detail: result.Message, statusCode: 502),
            _ => Results.Problem(title: "Unknown clone outcome", statusCode: 500),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ----- /api/products ---------------------------------------------------

    private static async Task<IResult> ListProducts(ForgeDbContext db) =>
        Results.Ok((await db.Products.AsNoTracking().OrderBy(p => p.Sort).ThenBy(p => p.Name).ToListAsync())
            .Select(MapProduct).ToList());

    private static async Task<IResult> GetProduct(string id, ForgeDbContext db)
    {
        var row = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        return row is null
            ? Results.Problem(title: $"Product {id} not found", statusCode: 404)
            : Results.Ok(MapProduct(row));
    }

    private static async Task<IResult> CreateProduct(
        HttpContext ctx,
        [FromBody] CreateProductRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.Problem(title: "id and name are required", statusCode: 400);
        }

        if (await db.Products.AnyAsync(p => p.Id == req.Id))
        {
            return Results.Problem(title: $"Product {req.Id} already exists", statusCode: 409);
        }

        var slug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Name) : req.Slug;
        if (await db.Products.AnyAsync(p => p.Slug == slug))
        {
            return Results.Problem(title: $"Slug '{slug}' is already in use", statusCode: 409);
        }

        var maxSort = await db.Products.Select(p => (int?)p.Sort).MaxAsync() ?? -1;
        var row = new ProductRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            Name = req.Name,
            Slug = slug,
            Description = req.Description ?? string.Empty,
            OwnerUserId = req.OwnerUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(row);
        await db.SaveChangesAsync();

        var product = MapProduct(row);
        await hub.Clients.All.SendAsync("product.changed", new { kind = "created", product });
        return Results.Created($"/api/products/{row.Id}", product);
    }

    private static async Task<IResult> PatchProduct(
        HttpContext ctx,
        string id,
        [FromBody] UpdateProductRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        if (req.Name is not null) row.Name = req.Name;
        if (req.Description is not null) row.Description = req.Description;
        if (req.OwnerUserId is not null) row.OwnerUserId = req.OwnerUserId;
        // Phase 15.6 — explicit override for the active iteration. An empty
        // string is treated as "clear the override"; null leaves it unchanged.
        if (req.ActiveIterationId is not null)
        {
            var v = req.ActiveIterationId.Length == 0 ? null : req.ActiveIterationId;
            if (v is not null && !await db.ProductIterations.AnyAsync(i => i.Id == v && i.ProductId == row.Id))
            {
                return Results.Problem(title: $"Iteration {v} is not in product {row.Id}", statusCode: 400);
            }
            row.ActiveIterationId = v;
        }

        await db.SaveChangesAsync();
        var product = MapProduct(row);
        await hub.Clients.All.SendAsync("product.changed", new { kind = "patched", product });
        return Results.Ok(product);
    }

    private static async Task<IResult> DeleteProduct(
        HttpContext ctx,
        string id,
        bool? force,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var repoCount = await db.Repos.CountAsync(r => r.ProductId == id);
        if (repoCount > 0 && force != true)
        {
            return Results.Json(
                new
                {
                    requireConfirm = true,
                    title = "Delete product with attached repos?",
                    detail = $"Product {id} has {repoCount} repo(s). Re-call with ?force=true to delete the product and detach the repos.",
                },
                statusCode: 422);
        }

        // Cascade delete (FK with OnDelete.Cascade) removes repos automatically.
        db.Products.Remove(row);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("product.changed", new { kind = "deleted", id });
        return Results.NoContent();
    }

    // ----- /api/products/{id}/repos ---------------------------------------

    private static async Task<IResult> ListReposForProduct(string id, ForgeDbContext db)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);
        var rows = await db.Repos.AsNoTracking().Where(r => r.ProductId == id).OrderBy(r => r.Sort).ToListAsync();
        return Results.Ok(rows.Select(MapRepo).ToList());
    }

    private static async Task<IResult> AttachRepo(
        HttpContext ctx,
        string id,
        [FromBody] CreateRepoRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IRepoCloneService cloneSvc,
        ILoggerFactory loggerFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var validation = RepoValidation.Validate(req);
        if (validation is not null) return validation;

        if (await db.Repos.AnyAsync(r => r.Id == req.Id))
        {
            return Results.Problem(title: $"Repo id {req.Id} already exists", statusCode: 409);
        }

        // Q5 — same remote URL cannot live under two products. The DB index
        // would reject this anyway, but a hand-shaped 409 with the existing
        // productId lets the UI offer "open existing product" as recovery.
        var existingByRemote = await db.Repos.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteUrl == req.RemoteUrl);
        if (existingByRemote is not null)
        {
            return Results.Json(
                new
                {
                    title = "Remote URL already attached",
                    detail = $"This repo is already attached to product '{existingByRemote.ProductId}' as '{existingByRemote.Id}'.",
                    productId = existingByRemote.ProductId,
                    repoId = existingByRemote.Id,
                },
                statusCode: 409);
        }

        // Q2 — same clone root cannot host two repos.
        if (await db.Repos.AnyAsync(r => r.CloneRoot == req.CloneRoot))
        {
            return Results.Problem(
                title: "Clone root already in use",
                detail: $"Another repo already targets {req.CloneRoot}. Pick a different folder.",
                statusCode: 409);
        }

        var maxSort = await db.Repos.Where(r => r.ProductId == id).Select(r => (int?)r.Sort).MaxAsync() ?? -1;
        var row = new RepoRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            ProductId = id,
            Kind = req.Kind,
            RemoteUrl = req.RemoteUrl,
            DisplayName = req.DisplayName,
            DefaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch,
            CloneRoot = req.CloneRoot,
            AdoOrg = req.AdoOrg,
            AdoProject = req.AdoProject,
            GhOwner = req.GhOwner,
            GhRepo = req.GhRepo,
        };
        db.Repos.Add(row);
        await db.SaveChangesAsync();

        var repo = MapRepo(row);
        await hub.Clients.All.SendAsync("repo.changed", new { kind = "attached", repo });

        // Auto-clone: if the local clone root doesn't exist, clone now.
        // Progress is streamed via SignalR clone events.
        string? cloneOutcome = null;
        string? cloneError = null;
        if (!Directory.Exists(row.CloneRoot))
        {
            try
            {
                var result = await cloneSvc.EnsureClonedAsync(row);
                cloneOutcome = result.Outcome.ToString().ToLowerInvariant();
                if (result.Outcome == Forge.Api.Services.Git.RepoCloneOutcome.Failed
                    || result.Outcome == Forge.Api.Services.Git.RepoCloneOutcome.Mismatched)
                {
                    cloneError = result.Message;
                }
            }
            catch (Exception ex)
            {
                cloneOutcome = "failed";
                cloneError = ex.Message;
                var log = loggerFactory.CreateLogger("Forge.Api.Endpoints.ProductsEndpoints");
                log.LogWarning(ex, "Auto-clone failed for repo {RepoId}", row.Id);
            }
        }

        return Results.Created($"/api/repos/{row.Id}", new { repo, cloneOutcome, cloneError });
    }

    // ----- /api/products/{id}/stats ---------------------------------------

    private static async Task<IResult> GetProductStats(string id, ForgeDbContext db)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var repoIds = await db.Repos.Where(r => r.ProductId == id).Select(r => r.Id).ToListAsync();
        var activeWs = repoIds.Count == 0
            ? 0
            : await db.Workstreams.CountAsync(w => repoIds.Contains(w.RepoId!) && w.Status != "merging" && !w.Planned);
        var openPrs = repoIds.Count == 0
            ? 0
            : await db.Prs.CountAsync(p => repoIds.Contains(p.RepoId!) && p.Col != "merged");
        var lastActiveTicks = repoIds.Count == 0
            ? null
            : await db.Workstreams.Where(w => repoIds.Contains(w.RepoId!)).Select(w => (double?)w.LastActive).MinAsync();

        return Results.Ok(new
        {
            productId = id,
            repoCount = repoIds.Count,
            activeWorkstreams = activeWs,
            openPrs,
            lastActivity = lastActiveTicks?.ToString("0.###"),
        });
    }

    // ----- /api/repos ------------------------------------------------------

    private static async Task<IResult> ListAllRepos(ForgeDbContext db, string? productId)
    {
        var q = db.Repos.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productId)) q = q.Where(r => r.ProductId == productId);
        var rows = await q.OrderBy(r => r.Sort).ToListAsync();
        return Results.Ok(rows.Select(MapRepo).ToList());
    }

    private static async Task<IResult> GetRepo(string id, ForgeDbContext db)
    {
        var row = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        return row is null
            ? Results.Problem(title: $"Repo {id} not found", statusCode: 404)
            : Results.Ok(MapRepo(row));
    }

    private static async Task<IResult> DeleteRepo(
        HttpContext ctx,
        string id,
        bool? force,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var liveWs = await db.Workstreams.CountAsync(w => w.RepoId == id);
        var livePrs = await db.Prs.CountAsync(p => p.RepoId == id);
        if ((liveWs > 0 || livePrs > 0) && force != true)
        {
            return Results.Json(
                new
                {
                    requireConfirm = true,
                    title = "Repo still has workstreams or PRs",
                    detail = $"Repo {id} has {liveWs} workstream(s) and {livePrs} PR(s). Re-call with ?force=true to detach them.",
                },
                statusCode: 422);
        }

        // Detach: orphan rows fall back to no repo (UI scopes them out of any
        // product view). Operator can move them later if they want.
        if (force == true)
        {
            await foreach (var w in db.Workstreams.Where(w => w.RepoId == id).AsAsyncEnumerable())
            {
                w.RepoId = null;
            }
            await foreach (var p in db.Prs.Where(p => p.RepoId == id).AsAsyncEnumerable())
            {
                p.RepoId = null;
            }
        }

        db.Repos.Remove(row);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("repo.changed", new { kind = "detached", id });
        return Results.NoContent();
    }

    // ----- helpers ---------------------------------------------------------

    private static Product MapProduct(ProductRow r) => new(
        r.Id, r.Name, r.Slug, r.Description, r.OwnerUserId, r.CreatedAt, r.ActiveIterationId);

    private static readonly System.Text.Json.JsonSerializerOptions RepoJsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static Repo MapRepo(RepoRow r)
    {
        IReadOnlyList<string>? areaPaths = null;
        if (!string.IsNullOrWhiteSpace(r.AreaPathsJson))
        {
            try { areaPaths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.AreaPathsJson, RepoJsonOpts); }
            catch { /* malformed — null */ }
        }
        return new Repo(
            r.Id, r.ProductId, r.Kind, r.RemoteUrl, r.DisplayName, r.DefaultBranch, r.CloneRoot,
            r.AdoOrg, r.AdoProject, r.GhOwner, r.GhRepo,
            areaPaths, r.LastSyncAt, r.LastSyncCursor, r.LastSyncStatus, r.LastSyncError,
            r.IsRunningSince, r.AgentReadyDefaultTeam, r.InitialSyncWindowDays);
    }

    private static string Slugify(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        var b = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) b.Append(ch);
            else if (ch is ' ' or '-' or '_') b.Append('-');
        }
        return b.ToString().Trim('-');
    }

    // ----- Phase 23 — scheduled sync endpoints ----------------------------

    public sealed record TriggerSyncRequest(
        bool IgnoreThrottle = false,
        string Kind = "auto");

    private static async Task<IResult> TriggerRepoSync(
        HttpContext ctx,
        string id,
        [FromBody] TriggerSyncRequest? req,
        ForgeDbContext db,
        WorkItemSyncService syncService,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var ignoreThrottle = req?.IgnoreThrottle ?? false;
        if (ignoreThrottle)
        {
            var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
            if (gate is not null) return gate;
        }

        // kind='full' clears the delta cursor so the next run uses the initial window.
        var kind = req?.Kind ?? "auto";
        if (string.Equals(kind, "full", StringComparison.OrdinalIgnoreCase))
        {
            repo.LastSyncAt = null;
            await db.SaveChangesAsync(ct);
        }

        // Build filters from the repo's sync state.
        DateTimeOffset? updatedFrom = null;
        if (!string.IsNullOrEmpty(repo.LastSyncAt)
            && DateTimeOffset.TryParse(repo.LastSyncAt, out var lastSync))
        {
            updatedFrom = lastSync;
        }

        var filters = new WorkItemSyncFilters(UpdatedFrom: updatedFrom);
        var result = await syncService.SyncAsync(repo, filters, ignoreThrottle, ct);

        if (result.Error is not null && result.Pulled == 0 && result.Inserted == 0)
        {
            var status = result.Error.StartsWith("Rate limit", StringComparison.OrdinalIgnoreCase) ? 429
                : result.Error.Contains("not configured", StringComparison.OrdinalIgnoreCase) ? 400
                : 502;
            return Results.Problem(title: result.Error, statusCode: status);
        }

        // Record the sync run.
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = $"sr-{Guid.NewGuid():N}",
            RepoId = id,
            Kind = "manual",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
            ItemsAdded = result.Inserted,
            ItemsUpdated = result.Updated,
            ErrorMessage = result.Error,
            CompletedSuccessfully = result.Error is null,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            repoId = id,
            pulled = result.Pulled,
            inserted = result.Inserted,
            updated = result.Updated,
            skipped = result.Skipped,
            error = result.Error,
        });
    }

    private static async Task<IResult> GetRepoSyncRuns(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var max = Math.Clamp(limit ?? 20, 1, 100);
        var runs = await db.WorkItemSyncRuns
            .Where(r => r.RepoId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(max)
            .Select(r => new
            {
                r.Id,
                r.Kind,
                r.StartedAt,
                r.FinishedAt,
                r.ItemsAdded,
                r.ItemsUpdated,
                r.ItemsConflicted,
                r.ErrorMessage,
                r.CompletedSuccessfully,
            })
            .ToListAsync(ct);

        return Results.Ok(runs);
    }

    private static async Task<IResult> GetSyncRunItems(
        HttpContext ctx,
        string id,
        string runId,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var run = await db.WorkItemSyncRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.RepoId == id, ct);
        if (run is null) return Results.Problem(title: $"Sync run {runId} not found", statusCode: 404);

        var items = await db.SyncRunItems
            .Where(i => i.RunId == runId)
            .OrderBy(i => i.Action)
            .ThenBy(i => i.EntityKind)
            .ThenBy(i => i.Title)
            .Select(i => new
            {
                i.EntityKind,
                i.EntityId,
                i.Title,
                i.Action,
                i.ProviderRef,
            })
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetRepoSyncStatus(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        return Results.Ok(new
        {
            lastSyncAt = repo.LastSyncAt,
            lastSyncStatus = repo.LastSyncStatus,
            lastSyncError = repo.LastSyncError,
            isRunningSince = repo.IsRunningSince,
        });
    }
}
