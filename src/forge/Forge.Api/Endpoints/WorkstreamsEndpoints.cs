// -----------------------------------------------------------------------
// <copyright file="WorkstreamsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 3.6 — workstream CRUD + sync endpoints.
/// Writes go through <see cref="ForgeUser.RequireWriter"/>; the git-worktree
/// service is best-effort (no-ops in environments without a checkout).
/// </summary>
public static class WorkstreamsEndpoints
{
    public static WebApplication MapWorkstreamsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/workstreams").WithTags("Workstreams");

        grp.MapGet("/", GetAll).WithName("GetWorkstreams");
        grp.MapPost("/", Create).WithName("CreateWorkstream");
        grp.MapPost("/{id}/sync", Sync).WithName("SyncWorkstream");
        grp.MapDelete("/{id}", Delete).WithName("DeleteWorkstream");
        grp.MapPost("/{id}/pr", OpenPr).WithName("OpenWorkstreamPr");

        return app;
    }

    // POST /api/workstreams/{id}/pr — Phase 6.4. Opens a PR via the named
    // provider with a templated body (workstream summary + linked card ids).
    public sealed record OpenPrRequest(string Title, string Body, string Provider = "github", bool Draft = false);

    private static async Task<IResult> OpenPr(
        HttpContext ctx,
        string id,
        [FromBody] OpenPrRequest req,
        ForgeDbContext db,
        IEnumerable<IForgeProvider> providers,
        Forge.Api.Auth.IUserContext user,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        var provider = providers.FirstOrDefault(p => p.Kind == req.Provider);
        if (provider is null) return Results.Problem(title: $"Unknown provider {req.Provider}", statusCode: 400);
        if (!provider.IsConfigured) return Results.Problem(title: $"Provider {req.Provider} not configured", statusCode: 400);

        var linkedCards = CardMapper.Deserialize<List<string>>(row.CardsJson) ?? new();
        var body = string.IsNullOrWhiteSpace(req.Body)
            ? $"{row.Summary}\n\nLinked: {string.Join(", ", linkedCards)}"
            : req.Body;

        try
        {
            var pr = await provider.CreatePullRequestAsync(
                new NewPullRequestRequest(row.Branch, row.Base, req.Title, body, req.Draft),
                user.ForProvider(provider.Kind));

            // Stash the new PR id on the workstream row.
            var prs = CardMapper.Deserialize<List<string>>(row.PrsJson) ?? new();
            if (!prs.Contains(pr.Id)) { prs.Add(pr.Id); row.PrsJson = CardMapper.Serialize(prs); }
            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync("pr.changed", new { kind = "opened", source = req.Provider, pr });
            return Results.Created($"/api/prs/{pr.Id}", pr);
        }
        catch (Exception ex)
        {
            return Results.Problem(title: $"Provider {req.Provider} create-PR failed", detail: ex.Message, statusCode: 502);
        }
    }

    // GET /api/workstreams
    private static async Task<IResult> GetAll(HttpContext ctx, ForgeDbContext db, IGitWorktreeService git)
    {
        var scope = await ProductScope.ResolveAsync(ctx, db);
        var q = db.Workstreams.AsNoTracking().AsQueryable();
        if (!scope.IsAll)
        {
            var ids = scope.RepoIds!;
            q = q.Where(w => w.RepoId != null && ids.Contains(w.RepoId));
        }
        var rows = await q.OrderBy(w => w.Sort).ToListAsync();
        var list = rows.Select(WorkstreamMapper.ToContract).ToList();
        // Best-effort live ahead/behind/conflicts when a git checkout is configured.
        // We do not block the response on git errors.
        try
        {
            foreach (var ws in list)
            {
                if (string.IsNullOrEmpty(ws.Branch) || string.IsNullOrEmpty(ws.Base)) continue;
                var ab = await git.AheadBehindAsync(ws.Base, ws.Branch);
                var conflicts = await git.ConflictPathsAsync(ws.Base, ws.Branch);
                if (ab.Ahead != 0 || ab.Behind != 0 || conflicts.Count > 0)
                {
                    var idx = list.IndexOf(ws);
                    list[idx] = ws with
                    {
                        Ahead = ab.Ahead,
                        Behind = ab.Behind,
                        Conflicts = conflicts.Count,
                    };
                }
            }
        }
        catch
        {
            // Ignore — cached values stay.
        }
        return Results.Ok(list);
    }

    public sealed record CreateWorkstreamRequest(
        string Id,
        string Name,
        string Branch,
        string Base,
        string Epic,
        string? WorktreePath = null,
        /// <summary>Phase 8.10 — repo this workstream belongs to. Auto-clone fires when the repo's cloneRoot is empty.</summary>
        string? RepoId = null);

    // POST /api/workstreams
    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateWorkstreamRequest req,
        ForgeDbContext db,
        IGitWorktreeService git,
        IRepoCloneService cloneSvc,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Branch))
        {
            return Results.Problem(title: "id and branch are required", statusCode: 400);
        }
        if (await db.Workstreams.AnyAsync(w => w.Id == req.Id))
        {
            return Results.Problem(title: $"Workstream {req.Id} already exists", statusCode: 409);
        }

        // Phase 8.10 — if the workstream targets a repo whose cloneRoot is
        // empty, run git clone first (silent / Q6). On hard failure we abort
        // the workstream creation rather than leaving a dangling row.
        var repoId = string.IsNullOrWhiteSpace(req.RepoId) ? ForgeSeed.DefaultRepoId : req.RepoId;
        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId);
        if (repo is not null)
        {
            var clone = await cloneSvc.EnsureClonedAsync(repo);
            if (clone.Outcome == RepoCloneOutcome.Mismatched)
            {
                return Results.Problem(
                    title: "Repo cloneRoot mismatch",
                    detail: clone.Message,
                    statusCode: 409);
            }
            if (clone.Outcome == RepoCloneOutcome.Failed)
            {
                return Results.Problem(
                    title: "Auto-clone failed",
                    detail: clone.Message,
                    statusCode: 502);
            }
        }

        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync() ?? -1;
        var worktree = req.WorktreePath is null
            ? new[] { "(not yet created)" }
            : new[] { Path.GetDirectoryName(req.WorktreePath) ?? string.Empty, Path.GetFileName(req.WorktreePath) };

        var row = new WorkstreamRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            RepoId = repo?.Id,
            Name = req.Name,
            Branch = req.Branch,
            Base = req.Base,
            Epic = req.Epic,
            Status = "planning",
            WorktreeJson = CardMapper.Serialize(worktree),
            Created = 0,
            LastActive = 0,
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Commits = 0,
            Ahead = 0,
            Behind = 0,
            Conflicts = 0,
            Summary = $"Spawned workstream {req.Name}.",
            BarStart = 0,
            BarEnd = 5,
            Planned = true,
        };
        db.Workstreams.Add(row);
        await db.SaveChangesAsync();

        if (req.WorktreePath is not null)
        {
            try
            {
                await git.CreateAsync(req.WorktreePath, req.Branch, req.Base);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Worktree path rejected", detail: ex.Message, statusCode: 403);
            }
            catch (Exception ex)
            {
                // Persistence already succeeded — surface as warning, not failure.
                Console.Error.WriteLine($"forge.git.worktree.create failed: {ex.Message}");
            }
        }

        var ws = WorkstreamMapper.ToContract(row);
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "created", workstream = ws });
        return Results.Created($"/api/workstreams/{row.Id}", ws);
    }

    // POST /api/workstreams/{id}/sync — refresh ahead/behind/conflicts.
    private static async Task<IResult> Sync(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IGitWorktreeService git,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        try
        {
            var ab = await git.AheadBehindAsync(row.Base, row.Branch);
            var conflicts = await git.ConflictPathsAsync(row.Base, row.Branch);
            row.Ahead = ab.Ahead;
            row.Behind = ab.Behind;
            row.Conflicts = conflicts.Count;
            await db.SaveChangesAsync();
        }
        catch
        {
            // Leave cached values; the UI surfaces 200 with whatever we have.
        }

        var ws = WorkstreamMapper.ToContract(row);
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "synced", workstream = ws });
        return Results.Ok(ws);
    }

    // DELETE /api/workstreams/{id}?force=true
    private static async Task<IResult> Delete(
        HttpContext ctx,
        string id,
        bool? force,
        ForgeDbContext db,
        IGitWorktreeService git,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // Safety: if there are open PRs and force != true, ask the caller to confirm.
        var prs = CardMapper.Deserialize<List<string>>(row.PrsJson) ?? new();
        if (prs.Count > 0 && force != true)
        {
            return Results.Json(new
            {
                requireConfirm = true,
                title = "Delete workstream with open PRs?",
                detail = $"Workstream {id} still has {prs.Count} PR(s). Re-call with ?force=true to delete anyway.",
            }, statusCode: 422);
        }

        var worktree = CardMapper.Deserialize<List<string>>(row.WorktreeJson) ?? new();
        if (worktree.Count >= 2 && worktree[0] != "(not yet created)")
        {
            try
            {
                var path = Path.Combine(worktree[0], worktree[1]);
                await git.RemoveAsync(path, force ?? false);
            }
            catch
            {
                // Best-effort.
            }
        }

        db.Workstreams.Remove(row);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "deleted", id });
        return Results.NoContent();
    }
}
