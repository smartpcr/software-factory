// -----------------------------------------------------------------------
// <copyright file="CrewsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 10.6 / 10.7 / 10.9 — crew CRUD with role gating.
/// Reads are open to any signed-in role (so the UI can render crew chips
/// across the board); writes require <c>product-owner</c> per Phase 10.9.
/// </summary>
public static class CrewsEndpoints
{
    public static WebApplication MapCrewsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/crews").WithTags("Crews");
        grp.MapGet("/", List).WithName("ListCrews");
        grp.MapGet("/{id}", Get).WithName("GetCrew");
        grp.MapPost("/", Create).WithName("CreateCrew");
        grp.MapPatch("/{id}", Patch).WithName("PatchCrew");
        grp.MapDelete("/{id}", Delete).WithName("DeleteCrew");
        return app;
    }

    private static async Task<IResult> List(ForgeDbContext db, string? productId, CancellationToken ct)
    {
        var q = db.Crews.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productId)) q = q.Where(c => c.ProductId == productId);
        var rows = await q.OrderBy(c => c.Sort).ThenBy(c => c.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(MapCrew).ToList());
    }

    private static async Task<IResult> Get(string id, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null
            ? Results.Problem(title: $"Crew {id} not found", statusCode: 404)
            : Results.Ok(MapCrew(row));
    }

    public sealed record CreateCrewRequest(
        string Id,
        string Name,
        string? ProductId,
        string? Summary,
        string? Lead,
        string? Model,
        decimal? ModelInputPricePerMTok,
        decimal? ModelOutputPricePerMTok);

    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateCrewRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.Problem(title: "id and name are required", statusCode: 400);
        }
        if (await db.Crews.AnyAsync(c => c.Id == req.Id, ct))
        {
            return Results.Problem(title: $"Crew {req.Id} already exists", statusCode: 409);
        }

        var maxSort = await db.Crews.Select(c => (int?)c.Sort).MaxAsync(ct) ?? -1;
        var row = new CrewRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            Name = req.Name,
            ProductId = req.ProductId,
            Summary = req.Summary ?? string.Empty,
            Lead = req.Lead,
            Model = string.IsNullOrWhiteSpace(req.Model) ? "claude-opus-4-7" : req.Model,
            ModelInputPricePerMTok = req.ModelInputPricePerMTok ?? 15m,
            ModelOutputPricePerMTok = req.ModelOutputPricePerMTok ?? 75m,
        };
        db.Crews.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/crews/{row.Id}", MapCrew(row));
    }

    public sealed record PatchCrewRequest(
        string? Name = null,
        string? Summary = null,
        string? Lead = null,
        string? Model = null,
        decimal? ModelInputPricePerMTok = null,
        decimal? ModelOutputPricePerMTok = null);

    private static async Task<IResult> Patch(
        HttpContext ctx,
        string id,
        [FromBody] PatchCrewRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var row = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        if (req.Name is not null) row.Name = req.Name;
        if (req.Summary is not null) row.Summary = req.Summary;
        if (req.Lead is not null) row.Lead = string.IsNullOrWhiteSpace(req.Lead) ? null : req.Lead;
        if (req.Model is not null) row.Model = req.Model;
        if (req.ModelInputPricePerMTok is decimal i) row.ModelInputPricePerMTok = i;
        if (req.ModelOutputPricePerMTok is decimal o) row.ModelOutputPricePerMTok = o;

        await db.SaveChangesAsync(ct);
        return Results.Ok(MapCrew(row));
    }

    private static async Task<IResult> Delete(HttpContext ctx, string id, ForgeDbContext db, CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var row = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        db.Crews.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Crew MapCrew(CrewRow r) => new(
        r.Id, r.Name, r.ProductId, r.Summary, r.Lead, r.Model,
        r.ModelInputPricePerMTok, r.ModelOutputPricePerMTok);
}
