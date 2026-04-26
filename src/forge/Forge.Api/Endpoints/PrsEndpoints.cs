// -----------------------------------------------------------------------
// <copyright file="PrsEndpoints.cs" company="Microsoft Corp.">
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
        grp.MapPost("/{id}/approve", Approve).WithName("ApprovePr");
        grp.MapPost("/{id}/changes", RequestChanges).WithName("RequestChangesOnPr");
        grp.MapPost("/{id}/comment", Comment).WithName("CommentOnPr");
        grp.MapPost("/{id}/merge", Merge).WithName("MergePr");

        return app;
    }

    private static IForgeProvider? PickProvider(string preferred, IEnumerable<IForgeProvider> providers) =>
        providers.FirstOrDefault(p => p.Kind == preferred && p.IsConfigured)
        ?? providers.FirstOrDefault(p => p.IsConfigured);

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

    // POST /api/prs/{id}/build/rerun — Phase 6.6: route through provider when one is configured.
    private static async Task<IResult> RerunBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        // Best-effort provider trigger; cached state always flips so the UI animates.
        var provider = PickProvider("github", providers);
        BuildStatus? trigger = null;
        if (provider is not null)
        {
            try { trigger = await provider.TriggerBuildAsync(row.Branch, user.ForProvider(provider.Kind)); }
            catch (Exception ex) { return Results.Problem(title: "Provider rerun failed", detail: ex.Message, statusCode: 502); }
        }
        var newBuild = trigger ?? new BuildStatus("running", "rerun queued", 0, null);
        row.BuildJson = CardMapper.Serialize(newBuild);
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.rerun", pr });
        // Phase 6.7 — push a tiny synthetic log tail so the UI can demonstrate streaming.
        await StreamFakeBuildLogAsync(hub, pr.Id);
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    // POST /api/prs/{id}/build/cancel — Phase 6.7: route through provider when one is configured.
    private static async Task<IResult> CancelBuild(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        BuildStatus? cancel = null;
        if (provider is not null)
        {
            try { cancel = await provider.CancelBuildAsync(row.Branch, user.ForProvider(provider.Kind)); }
            catch (Exception ex) { return Results.Problem(title: "Provider cancel failed", detail: ex.Message, statusCode: 502); }
        }
        var newBuild = cancel ?? new BuildStatus("queued", "cancelled by user", 0, null);
        row.BuildJson = CardMapper.Serialize(newBuild);
        await db.SaveChangesAsync();

        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "build.cancel", pr });
        return Results.Accepted($"/api/prs/{id}", pr);
    }

    // POST /api/prs/{id}/approve — Phase 6.5.
    private static async Task<IResult> Approve(
        HttpContext ctx, string id,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.ApprovePullRequestAsync(id, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider approve failed", detail: ex.Message, statusCode: 502); }

        row.ReviewJson = CardMapper.Serialize(new ReviewVerdict(
            Agent: user.UserId ?? "human",
            Name: user.UserId ?? "human",
            Verdict: "approved",
            Summary: "Approved via Forge."));
        await db.SaveChangesAsync();
        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "approved", pr });
        return Results.Ok(pr);
    }

    public sealed record ChangesRequest(string Body);

    // POST /api/prs/{id}/changes — Phase 6.5.
    private static async Task<IResult> RequestChanges(
        HttpContext ctx, string id, [FromBody] ChangesRequest req,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.Body))
        {
            return Results.Problem(title: "Body is required", statusCode: 400);
        }
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.RequestChangesOnPullRequestAsync(id, req.Body, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider request-changes failed", detail: ex.Message, statusCode: 502); }

        row.ReviewJson = CardMapper.Serialize(new ReviewVerdict(
            Agent: user.UserId ?? "human",
            Name: user.UserId ?? "human",
            Verdict: "changes",
            Summary: req.Body));
        await db.SaveChangesAsync();
        var pr = PrMapper.ToContract(row);
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "changes", pr });
        return Results.Ok(pr);
    }

    public sealed record PrCommentRequest(string Body);

    // POST /api/prs/{id}/comment — Phase 6.5.
    private static async Task<IResult> Comment(
        HttpContext ctx, string id, [FromBody] PrCommentRequest req,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.Body))
        {
            return Results.Problem(title: "Body is required", statusCode: 400);
        }
        if (!await db.Prs.AnyAsync(p => p.Id == id)) return NotFound(id);

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        try { await provider.CommentOnPullRequestAsync(id, req.Body, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider comment failed", detail: ex.Message, statusCode: 502); }

        await hub.Clients.All.SendAsync("pr.commented", new { id, by = user.UserId });
        return Results.Ok(new { ok = true });
    }

    // POST /api/prs/{id}/merge — Phase 6.5/6.8 explicit-merge path (auto-merge has its own watcher).
    private static async Task<IResult> Merge(
        HttpContext ctx, string id,
        ForgeDbContext db,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        IHumanGateEvaluator gate,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return NotFound(id);

        var pr = PrMapper.ToContract(row);
        var decision = gate.Evaluate(pr);
        if (decision.Required)
        {
            return Results.Json(new { requireConfirm = true, title = "Human gate", detail = decision.Reason }, statusCode: 422);
        }

        var provider = PickProvider("github", providers);
        if (provider is null) return Results.Problem(title: "No provider configured", statusCode: 400);
        Pr merged;
        try { merged = await provider.MergePullRequestAsync(id, user.ForProvider(provider.Kind)); }
        catch (Exception ex) { return Results.Problem(title: "Provider merge failed", detail: ex.Message, statusCode: 502); }

        row.Col = "merged";
        row.AutoMerge = false;
        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("pr.changed", new { kind = "merged", pr = merged });
        return Results.Ok(merged);
    }

    // Phase 6.7 — synthetic log tail. Phase R replaces this with provider log streaming.
    private static async Task StreamFakeBuildLogAsync(IHubContext<ForgeHub> hub, string prId)
    {
        var lines = new[] { "$ rerun", "$ npm ci", "$ npm test", "PASS", "PASS", "passed in 18s" };
        for (var i = 0; i < lines.Length; i++)
        {
            await hub.Clients.All.SendAsync("pr.log", new { id = prId, seq = i, line = lines[i] });
            await Task.Delay(150);
        }
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: $"PR {id} not found", statusCode: 404);
}
