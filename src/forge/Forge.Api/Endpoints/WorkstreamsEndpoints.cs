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
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
        grp.MapPost("/{id}/restart", Restart).WithName("RestartWorkstream");
        grp.MapPost("/{id}/cancel", Cancel).WithName("CancelWorkstream");

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
        IConfiguration config,
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

        var report = await CascadeDeleteWorkstreamAsync(db, git, config, row, ct: ctx.RequestAborted);

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "deleted", id });
        if (!string.IsNullOrEmpty(row.StorySpawnId))
        {
            await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "workstream-deleted", spawnId = row.StorySpawnId, workstreamId = id });
        }
        return Results.Ok(report);
    }

    /// <summary>
    /// Cascading delete for a workstream and every artifact it produced.
    /// Removes the rows referenced by <c>workstream.Id</c> across the
    /// task-execution surface (deliverables, transcripts, queue, links,
    /// dep edges, agent assignments, board card), the matching
    /// <see cref="WorkItemRow"/>, the on-disk gen↔eval workspaces, the git
    /// worktree mount, and the local git branch. Returns a structured
    /// report so the caller can surface "X rows / Y files removed" in the
    /// UI without having to count again.
    ///
    /// <para>What is intentionally KEPT: <see cref="AgentUsageRow"/> (cost
    /// audit), closed/merged <see cref="PrRow"/>s (history), and the
    /// spawn's <c>DecompositionJson</c> (historical record of what the
    /// architect produced — UI sources current state from the Workstreams
    /// table, so a stale entry there isn't load-bearing).</para>
    /// </summary>
    private static async Task<object> CascadeDeleteWorkstreamAsync(
        ForgeDbContext db,
        IGitWorktreeService git,
        IConfiguration config,
        WorkstreamRow row,
        CancellationToken ct)
    {
        var id = row.Id;
        var counts = new Dictionary<string, int>();

        // ── 1. Task-execution surface (deliverables, transcripts, queue) ──
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync(ct);
        db.Deliverables.RemoveRange(deliverables);
        counts["deliverables"] = deliverables.Count;

        var deliverableQuestions = await db.DeliverableQuestions.Where(q => q.WorkItemId == id).ToListAsync(ct);
        db.DeliverableQuestions.RemoveRange(deliverableQuestions);
        counts["deliverableQuestions"] = deliverableQuestions.Count;

        var transcript = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync(ct);
        db.TaskTranscriptEvents.RemoveRange(transcript);
        counts["transcriptEvents"] = transcript.Count;

        var queueRows = await db.TaskQueue.Where(q => q.WorkItemId == id).ToListAsync(ct);
        var queueIds = queueRows.Select(q => q.Id).ToList();
        var queueHistory = await db.TaskQueueHistory.Where(h => queueIds.Contains(h.QueueId)).ToListAsync(ct);
        db.TaskQueueHistory.RemoveRange(queueHistory);
        db.TaskQueue.RemoveRange(queueRows);
        counts["queueRows"] = queueRows.Count;
        counts["queueHistory"] = queueHistory.Count;

        // ── 2. Cross-table edges that reference this workstream ──────────
        var depEdges = await db.WorkstreamDeps
            .Where(d => d.WorkstreamId == id || d.DependsOnId == id)
            .ToListAsync(ct);
        db.WorkstreamDeps.RemoveRange(depEdges);
        counts["depEdges"] = depEdges.Count;

        var workItemLinks = await db.WorkItemLinks
            .Where(l => l.FromId == id || l.ToId == id)
            .ToListAsync(ct);
        db.WorkItemLinks.RemoveRange(workItemLinks);
        counts["workItemLinks"] = workItemLinks.Count;

        var agentAssignments = await db.AgentAssignments.Where(a => a.WorkItemId == id).ToListAsync(ct);
        db.AgentAssignments.RemoveRange(agentAssignments);
        counts["agentAssignments"] = agentAssignments.Count;

        var wsEvents = await db.WsEvents.Where(e => e.Ws == id).ToListAsync(ct);
        db.WsEvents.RemoveRange(wsEvents);
        counts["wsEvents"] = wsEvents.Count;

        // ── 3. The work-item synthesized for this workstream ─────────────
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            db.WorkItems.Remove(wi);
            counts["workItem"] = 1;
        }

        // ── 4. Board card (if one was created via "Dispatch") ────────────
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is not null)
        {
            db.Cards.Remove(card);
            counts["card"] = 1;
        }

        // ── 5. The workstream itself ─────────────────────────────────────
        db.Workstreams.Remove(row);
        counts["workstream"] = 1;

        await db.SaveChangesAsync(ct);

        // ── 6. On-disk artifacts (best-effort; never block the delete) ───
        var stateDir = Path.GetDirectoryName(config["Forge:Db:Path"] ?? @"E:\forge\forge.db") ?? @"E:\forge";
        var spawnSlug = row.StorySpawnId ?? string.Empty;
        var dirsRemoved = 0;
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            if (string.IsNullOrEmpty(slug)) continue;
            var spawnRoot = Path.Combine(stateDir, "storyworkspaces", slug);
            if (!Directory.Exists(spawnRoot)) continue;
            // impl-{id}, review-{id}-r*, fix-{id}-* — match on prefix so
            // every per-round / per-fix workspace gets reaped.
            foreach (var dir in Directory.EnumerateDirectories(spawnRoot))
            {
                var name = Path.GetFileName(dir);
                if (name == $"impl-{id}"
                    || name.StartsWith($"review-{id}-", StringComparison.Ordinal)
                    || name.StartsWith($"fix-{id}-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(dir, recursive: true); dirsRemoved++; }
                    catch { /* best-effort */ }
                }
            }
        }
        counts["workspaceDirs"] = dirsRemoved;

        // ── 7. Worktree mount + local branch ─────────────────────────────
        if (!string.IsNullOrEmpty(row.Branch))
        {
            var repoRow = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.RepoId, ct);
            var cloneRoot = repoRow?.CloneRoot;
            if (!string.IsNullOrEmpty(cloneRoot))
            {
                var sanitized = row.Branch.Replace('/', '-');
                var wtPath = Path.Combine(cloneRoot, ".worktree", $"spawn-{sanitized}");
                if (Directory.Exists(wtPath))
                {
                    try { await git.RemoveAsync(wtPath, force: true); counts["worktreeRemoved"] = 1; }
                    catch { counts["worktreeRemoved"] = 0; }
                }
                // Delete the local branch. Best-effort — `git branch -D`
                // refuses if the branch is checked out somewhere; in that
                // case the worktree-remove above is the meaningful work.
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", $"branch -D {row.Branch}")
                    {
                        WorkingDirectory = cloneRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is not null && p.WaitForExit(10_000) && p.ExitCode == 0)
                    {
                        counts["branchRemoved"] = 1;
                    }
                }
                catch { /* best-effort */ }
            }
        }

        // Also catch the legacy worktree shape (worktreeJson = [base, sub]).
        var legacyWorktree = CardMapper.Deserialize<List<string>>(row.WorktreeJson) ?? new();
        if (legacyWorktree.Count >= 2 && legacyWorktree[0] != "(not yet created)")
        {
            try
            {
                var path = Path.Combine(legacyWorktree[0], legacyWorktree[1]);
                if (Directory.Exists(path)) await git.RemoveAsync(path, force: true);
            }
            catch { /* best-effort */ }
        }

        return new
        {
            id,
            status = "deleted",
            counts,
        };
    }

    /// <summary>
    /// POST /api/workstreams/{id}/restart — nuke everything for this workstream
    /// and start fresh: delete deliverables, clear workspace, reset git branch,
    /// reset status to active, re-enter the gen/eval loop from iter 1.
    /// </summary>
    private static async Task<IResult> Restart(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IConfiguration config,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // 1. Delete all deliverables for this workstream
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync();
        db.Deliverables.RemoveRange(deliverables);

        // 2. Delete transcript events
        var events = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync();
        db.TaskTranscriptEvents.RemoveRange(events);

        // 3. Reset workstream status
        ws.Status = "active";
        ws.PrsJson = "[]";
        ws.Commits = 0;
        ws.Ahead = 0;
        ws.Behind = 0;
        ws.Conflicts = 0;
        await db.SaveChangesAsync();

        // 4. Clear the cached workspace
        var dbPath = config["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        var stateDir = System.IO.Path.GetDirectoryName(dbPath) ?? @"E:\forge";
        var spawnSlug = ws.StorySpawnId ?? "";
        // Check both slug conventions (dashes and underscores)
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            var wsDir = System.IO.Path.Combine(stateDir, "storyworkspaces", slug, $"impl-{id}");
            if (System.IO.Directory.Exists(wsDir))
            {
                try { System.IO.Directory.Delete(wsDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        // 5. Reset git branch to main (if worktree exists)
        if (!string.IsNullOrEmpty(ws.Branch))
        {
            var repoRow = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ws.RepoId);
            var cloneRoot = repoRow?.CloneRoot ?? @"C:\forge\workflow";
            var sanitized = ws.Branch.Replace('/', '-');
            var wtPath = System.IO.Path.Combine(cloneRoot, ".worktree", $"spawn-{sanitized}");
            if (System.IO.Directory.Exists(wtPath))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", $"reset --hard origin/main")
                    {
                        WorkingDirectory = wtPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(10_000);
                }
                catch { /* best effort */ }
            }
        }

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "restarted", id });

        // 6. Fire-and-forget: re-drive the story so the orchestrator picks up
        //    this workstream and re-enters the gen/eval loop.
        if (!string.IsNullOrEmpty(ws.StorySpawnId))
        {
            var spawn = await db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId);
            if (spawn is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await orchestrator.DriveAsync(spawn.StoryId, forceResume: false, CancellationToken.None); }
                    catch { /* failures persisted via PersistLastErrorAsync */ }
                });
            }
        }

        return Results.Ok(new { id, status = "restarted", deliverablesCleaned = deliverables.Count, eventsCleaned = events.Count });
    }

    /// <summary>
    /// POST /api/workstreams/{id}/cancel — stop the currently running gen/eval
    /// loop for this workstream. Kills the running agent process and marks the
    /// workstream as stalled.
    /// </summary>
    private static async Task<IResult> Cancel(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // Cancel any queued/active task_queue rows for this workstream
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // Mark the work item execution as cancelled
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null && wi.ExecutionStatus is "live" or "queued")
        {
            wi.ExecutionStatus = "idle";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Mark the last deliverable as stalled if it's in iterate state
        var lastDel = (await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync())
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is not null && lastDel.Verdict is "iterate")
        {
            lastDel.Verdict = "stalled";
        }

        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "cancelled", id });

        return Results.Ok(new { id, status = "cancelled", queueRowsCancelled = queueRows.Count });
    }
}
