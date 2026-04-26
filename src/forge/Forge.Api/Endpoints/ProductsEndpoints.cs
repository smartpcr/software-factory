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
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

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
        repos.MapDelete("/{id}", DeleteRepo).WithName("DeleteRepo");

        return app;
    }

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
        IHubContext<ForgeHub> hub)
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
        return Results.Created($"/api/repos/{row.Id}", repo);
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
        r.Id, r.Name, r.Slug, r.Description, r.OwnerUserId, r.CreatedAt);

    private static Repo MapRepo(RepoRow r) => new(
        r.Id, r.ProductId, r.Kind, r.RemoteUrl, r.DisplayName, r.DefaultBranch, r.CloneRoot,
        r.AdoOrg, r.AdoProject, r.GhOwner, r.GhRepo);

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
}
