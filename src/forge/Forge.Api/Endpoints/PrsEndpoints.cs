// -----------------------------------------------------------------------
// <copyright file="PrsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 4 — Pull Request endpoints. The API merely surfaces the cached PR
/// state today; Phase 5/6 land the ADO + GitHub providers and the actual
/// merge / re-run / cancel actions. The Phase-4 stubs return 202 so the
/// UI can wire the buttons end-to-end.
/// </summary>
public static class PrsEndpoints
{
    public static WebApplication MapPrsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/prs").WithTags("Prs");

        grp.MapGet("/", GetAll).WithName("GetPrs");
        grp.MapGet("/{id}", Get).WithName("GetPr");
        grp.MapGet("/{id}/details", GetDetails).WithName("GetPrDetails");
        grp.MapPost("/{id}/auto-merge", SetAutoMerge).WithName("SetPrAutoMerge");
        grp.MapPost("/{id}/build/rerun", RerunBuild).WithName("RerunPrBuild");
        grp.MapPost("/{id}/build/cancel", CancelBuild).WithName("CancelPrBuild");

        return app;
    }

    // GET /api/prs — list with cached state.
    private static async Task<IResult> GetAll(ForgeDbContext db)
    {
        var rows = await db.Prs.AsNoTracking().OrderBy(p => p.Sort).ToListAsync();
        return Results.Ok(rows.Select(PrMapper.ToContract).ToList());
    }

    // GET /api/prs/{id} — single PR.
    private static async Task<IResult> Get(string id, ForgeDbContext db)
    {
        var row = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        return row is null ? NotFound(id) : Results.Ok(PrMapper.ToContract(row));
    }

    public sealed record PrDetailsResponse(
        Pr Pr,
        string Description,
        IReadOnlyList<string> BuildLogTail,
        IReadOnlyList<string> Reviewers);

    // GET /api/prs/{id}/details — extended payload for the detail panel.
    private static async Task<IResult> GetDetails(string id, ForgeDbContext db)
    {
        var row = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);
        var pr = PrMapper.ToContract(row);

        var description = string.IsNullOrEmpty(pr.Branch)
            ? "No description provided."
            : $"Branch {pr.Branch} → {pr.Base}. {pr.Title}.";

        // Phase-4 placeholder — Phase 6 streams the real CI log via SignalR.
        var log = new List<string>
        {
            $"$ git fetch origin {pr.Base} && git checkout {pr.Branch}",
            "$ npm ci",
            "$ npm run build",
            $"… {pr.Files} file{(pr.Files == 1 ? "" : "s")}, {pr.Commits} commit{(pr.Commits == 1 ? "" : "s")} compared",
        };
        if (pr.Build is not null)
        {
            log.Add($"$ npm test  → {pr.Build.Step}");
            if (pr.Build.Status == "failing" && pr.Build.FailingTest is not null)
            {
                log.Add($"FAIL  {pr.Build.FailingTest}");
            }
            log.Add($"build status: {pr.Build.Status} ({pr.Build.Percent}%)");
        }
        else
        {
            log.Add("no build run yet");
        }

        var reviewers = pr.Review is null
            ? Array.Empty<string>()
            : new[] { $"{pr.Review.Agent} {pr.Review.Name} — {pr.Review.Verdict}" };

        return Results.Ok(new PrDetailsResponse(pr, description, log, reviewers));
    }

    public sealed record SetAutoMergeRequest(bool AutoMerge);

    // POST /api/prs/{id}/auto-merge — toggle the per-PR auto-merge flag.
    private static async Task<IResult> SetAutoMerge(
        HttpContext ctx,
        string id,
        [FromBody] SetAutoMergeRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        row.AutoMerge = req.AutoMerge;
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "autoMerge", pr });
        return Results.Ok(pr);
    }

    // POST /api/prs/{id}/build/rerun — Phase 6 stub. 202 + the cached PR.
    private static async Task<IResult> RerunBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        // Optimistic UI hint — flip the in-memory build to "running 0%" so the
        // SignalR fan-out tells every client. The real CI rerun lands in Phase 6.
        row.BuildJson = CardMapper.Serialize(new { status = "running", step = "rerun queued", percent = 0 });
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.rerun", pr });
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    // POST /api/prs/{id}/build/cancel — Phase 6 stub. 202 + the cached PR.
    private static async Task<IResult> CancelBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        row.BuildJson = CardMapper.Serialize(new { status = "queued", step = "cancelled by user", percent = 0 });
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.cancel", pr });
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: $"PR {id} not found", statusCode: 404);
}
