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
        grp.MapGet("/{id}/budget", GetBudget).WithName("GetCrewBudget");
        grp.MapGet("/{id}/salary", GetSalary).WithName("GetCrewSalary");
        grp.MapGet("/salary/today", GetSalaryTodayTotal).WithName("GetSalaryTodayTotal");
        return app;
    }

    private static async Task<IResult> GetSalary(string id, ForgeDbContext db, CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        var rows = await db.AgentUsage.AsNoTracking()
            .Where(r => r.CrewId == id)
            .Select(r => new { r.StartedAt, r.CostUsd })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var startOfDay = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var startOfMonth = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1), TimeSpan.Zero);

        // ISO sprint = the current Monday (0-indexed week start).
        var dayOfWeek = (int)now.UtcDateTime.DayOfWeek;
        var daysSinceMonday = (dayOfWeek + 6) % 7; // Sunday=0 → 6, Monday=1 → 0
        var startOfSprint = startOfDay.AddDays(-daysSinceMonday);

        decimal Sum(DateTimeOffset bound) => rows.Where(r => r.StartedAt >= bound).Sum(r => r.CostUsd);

        // 14-day sparkline, oldest → today.
        var sparkline = new decimal[14];
        for (var i = 0; i < 14; i++)
        {
            var dayStart = startOfDay.AddDays(-(13 - i));
            var dayEnd = dayStart.AddDays(1);
            sparkline[i] = rows.Where(r => r.StartedAt >= dayStart && r.StartedAt < dayEnd).Sum(r => r.CostUsd);
        }

        return Results.Ok(new Forge.Api.Contracts.CrewSalary(
            CrewId: id,
            Today: Round(Sum(startOfDay)),
            Sprint: Round(Sum(startOfSprint)),
            Month: Round(Sum(startOfMonth)),
            Sparkline: sparkline.Select(Round).ToList()));
    }

    private static async Task<IResult> GetSalaryTodayTotal(ForgeDbContext db, CancellationToken ct)
    {
        var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var rows = await db.AgentUsage.AsNoTracking()
            .Select(r => new { r.StartedAt, r.CostUsd, r.Status })
            .ToListAsync(ct);
        var todays = rows.Where(r => r.StartedAt >= startOfDay).ToList();
        var total = todays.Sum(r => r.CostUsd);
        var active = todays.Count(r => r.Status == "running");
        return Results.Ok(new Forge.Api.Contracts.SalaryTodaySummary(Round(total), active));
    }

    private static decimal Round(decimal v) => Math.Round(v, 6);

    private static async Task<IResult> GetBudget(string id, ForgeDbContext db, CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var spent = await TodaysSpendAsync(db, id, ct);
        var remaining = crew.DailyBudgetUsd is decimal cap ? cap - spent : (decimal?)null;
        return Results.Ok(new Forge.Api.Contracts.CrewBudget(
            CrewId: id,
            DailyBudgetUsd: crew.DailyBudgetUsd,
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            SpentTodayUsd: Math.Round(spent, 6),
            RemainingUsd: remaining is decimal r ? Math.Round(r, 6) : null));
    }

    /// <summary>
    /// Sum today's <see cref="AgentUsageRow.CostUsd"/> for a crew. The date
    /// filter and the sum both run in memory because (a) SQLite EF cannot
    /// translate <c>DateTimeOffset</c> comparisons, and (b) <c>CostUsd</c>
    /// uses a decimal→double value converter that doesn't push through SUM.
    /// Volume is bounded (one crew × one day), so this is cheap.
    /// </summary>
    public static async Task<decimal> TodaysSpendAsync(ForgeDbContext db, string crewId, CancellationToken ct)
    {
        var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var rows = await db.AgentUsage.AsNoTracking()
            .Where(r => r.CrewId == crewId)
            .Select(r => new { r.StartedAt, r.CostUsd })
            .ToListAsync(ct);
        return rows.Where(r => r.StartedAt >= startOfDay).Sum(r => r.CostUsd);
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
        decimal? ModelOutputPricePerMTok,
        decimal? DailyBudgetUsd = null,
        decimal? MaxTaskBudgetUsd = null);

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
            DailyBudgetUsd = req.DailyBudgetUsd,
            MaxTaskBudgetUsd = req.MaxTaskBudgetUsd,
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
        decimal? ModelOutputPricePerMTok = null,
        decimal? DailyBudgetUsd = null,
        decimal? MaxTaskBudgetUsd = null);

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
        if (req.DailyBudgetUsd is decimal db1) row.DailyBudgetUsd = db1;
        if (req.MaxTaskBudgetUsd is decimal mt) row.MaxTaskBudgetUsd = mt;

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
        r.ModelInputPricePerMTok, r.ModelOutputPricePerMTok,
        r.DailyBudgetUsd, r.MaxTaskBudgetUsd);
}

