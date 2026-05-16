// -----------------------------------------------------------------------
// <copyright file="WorkstreamsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Agents;
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
        grp.MapPost("/{id}/retry", Retry).WithName("RetryWorkstream");
        grp.MapPost("/{id}/cancel", Cancel).WithName("CancelWorkstream");
        // Phase 70 — operator-override ACCEPT mirrors plan-doc Phase 43:
        // flip the latest deliverable verdict to `pass` so the orchestrator
        // proceeds to commit + open the workstream PR on its next tick.
        // Used to bypass Phase 50 / 56 / 66 false-positive demotions when
        // the operator has independently verified the work is good.
        grp.MapPost("/{id}/accept", Accept).WithName("AcceptWorkstream");
        grp.MapGet("/{id}/memory", GetMemory).WithName("GetWorkstreamMemory");
        grp.MapGet("/{id}/iter-notes", GetIterNotes).WithName("GetWorkstreamIterNotes");
        grp.MapGet("/{id}/iter-kpis", GetIterKpis).WithName("GetWorkstreamIterKpis");

        return app;
    }

    // GET /api/workstreams/{id}/iter-kpis — Insight I.3
    // (docs/forge/workstream-insights.md). Delegates to
    // ConversationInsightQueries so the per-doc plan-doc routes on
    // StoriesEndpoints share the same query body.
    private static async Task<IResult> GetIterKpis(
        string id,
        ProductDbContext db,
        CancellationToken ct)
    {
        var payload = await Forge.Api.Services.ConversationInsightQueries.GetIterKpisAsync(db, id, ct);
        return Results.Ok(payload);
    }

    // GET /api/workstreams/{id}/iter-notes — Insight I.2
    // (docs/forge/workstream-insights.md). Resolves the worktree via
    // WorktreePathResolver, then delegates to ConversationInsightQueries
    // for the directory walk.
    private static async Task<IResult> GetIterNotes(
        string id,
        ProductDbContext db,
        CancellationToken ct)
    {
        var resolved = await WorktreePathResolver.ResolveForWorkstreamAsync(db, id, ct);
        var payload = await Forge.Api.Services.ConversationInsightQueries
            .GetIterNotesAsync(id, resolved.WorktreePath, ct);
        return Results.Ok(payload);
    }

    // GET /api/workstreams/{id}/memory — Phase 55 / Phase G. Delegates to
    // ConversationInsightQueries so the per-doc plan-doc memory route
    // shares the same body. Crew resolution walks deliverables so the
    // same code works for engineer workstream ids + plan-doc work items.
    private static async Task<IResult> GetMemory(
        string id,
        ProductDbContext db,
        Forge.Api.Agents.CopilotSessionPool copilotPool,
        Forge.Api.Services.MemoryCompactor compactor,
        CancellationToken ct)
    {
        var payload = await Forge.Api.Services.ConversationInsightQueries
            .GetMemoryAsync(db, copilotPool, compactor, id, ct);
        return Results.Ok(payload);
    }

    // POST /api/workstreams/{id}/pr — Phase 6.4. Opens a PR via the named
    // provider with a templated body (workstream summary + linked card ids).
    public sealed record OpenPrRequest(string Title, string Body, string Provider = "github", bool Draft = false);

    private static async Task<IResult> OpenPr(
        HttpContext ctx,
        string id,
        [FromBody] OpenPrRequest req,
        ProductDbContext db,
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
    private static async Task<IResult> GetAll(HttpContext ctx, ProductDbContext db, IGitWorktreeService git)
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
        ProductDbContext db,
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
        ProductDbContext db,
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
        ProductDbContext db,
        IGitWorktreeService git,
        IForgeDbPaths paths,
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

        var report = await CascadeDeleteWorkstreamAsync(db, git, paths, row, ct: ctx.RequestAborted);

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
    ///
    /// <para>Phase 50 — when <paramref name="deleteRemoteBranch"/> is
    /// <c>true</c>, also runs <c>git push origin --delete {ws.Branch}</c>
    /// after the local branch has been removed. Used by the spawn-delete
    /// endpoint where a "restart from scratch" must wipe the remote
    /// counterpart so the operator can re-spawn the same story without
    /// resurrecting the previous attempt's PR branch.</para>
    /// </summary>
    internal static async Task<object> CascadeDeleteWorkstreamAsync(
        ProductDbContext db,
        IGitWorktreeService git,
        IForgeDbPaths paths,
        WorkstreamRow row,
        CancellationToken ct,
        bool deleteRemoteBranch = false)
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

        // Phase 50 follow-up (2026-05-12) — cost ledger rows. TaskId on
        // agent_usage equals the work-item id (DeliverableLoop emits
        // each gen/eval invocation with TaskId = workItemId). Without
        // this purge, deleting a workstream and re-decomposing into
        // another workstream with the same phase/stage slug (very
        // common — operator iterates on a plan) inherits the old
        // cost ledger and the doc / workstream cards show stale dollar
        // totals (operator-reported: re-spawned story showed
        // ~$1300-$2000 per plan-doc card before any LLM had run).
        var agentUsage = await db.AgentUsage.Where(u => u.TaskId == id).ToListAsync(ct);
        db.AgentUsage.RemoveRange(agentUsage);
        counts["agentUsage"] = agentUsage.Count;

        // Phase 52 follow-up (2026-05-13) — pair-attempt audit rows
        // (one per (workItem, pair) attempt; Phase 52 schema). Without
        // this purge the rows are orphaned to a dead WorkItemId and the
        // Phase 14.16 SloppyRate KPI keeps counting them for the agents
        // involved, inflating each agent's sloppy rate after every
        // operator workstream-restart cycle.
        var pairAssignments = await db.AgentPairAssignments
            .Where(p => p.WorkItemId == id).ToListAsync(ct);
        db.AgentPairAssignments.RemoveRange(pairAssignments);
        counts["pairAssignments"] = pairAssignments.Count;

        // Phase 52 follow-up — agent_claims. DeleteSpawn already releases
        // open claims (ReleasedAt = now) but never deletes the rows; the
        // capacity-tracking sweep keeps reading them and the audit grows
        // unboundedly. Wipe historical claims for this work item — the
        // ledger of "which agent picked this up" is meaningless once
        // the work item itself is gone.
        var agentClaims = await db.AgentClaims.Where(c => c.WorkItemId == id).ToListAsync(ct);
        db.AgentClaims.RemoveRange(agentClaims);
        counts["agentClaims"] = agentClaims.Count;

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
        var stateDir = paths.Folder;
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
                // Try BOTH worktree-path conventions this codebase ships
                // with (canonical engineer-workstream slug, ad-hoc ws-{id}),
                // plus whatever path git has registered against this branch
                // (handles operator-customised directory layouts).
                var candidatePaths = new List<string>();
                var sanitizedId = row.Id.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
                candidatePaths.Add(Path.Combine(cloneRoot, ".worktree", $"ws-{sanitizedId}"));
                if (!string.IsNullOrEmpty(row.StorySpawnId))
                {
                    try
                    {
                        var siblings = await db.Workstreams.AsNoTracking()
                            .Where(w => w.StorySpawnId == row.StorySpawnId)
                            .ToListAsync(ct);
                        if (siblings.All(w => w.Id != row.Id)) siblings.Add(row);
                        var newSlug = Forge.Api.Services.Stories.WorkstreamWorktreeSlug.Compute(row, siblings);
                        candidatePaths.Add(Path.Combine(cloneRoot, ".worktree", newSlug));
                    }
                    catch { /* slug computer is best-effort */ }
                }
                var registeredPath = await TryFindWorktreeForBranchAsync(cloneRoot, row.Branch);
                if (!string.IsNullOrEmpty(registeredPath)) candidatePaths.Add(registeredPath);

                // Run `git worktree prune` first so stale metadata doesn't
                // block a fresh `worktree add` after a re-spawn.
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");

                var wtRemoved = 0;
                foreach (var wtPath in candidatePaths.Distinct())
                {
                    // Always try `git worktree remove --force` first — even
                    // when the dir doesn't exist on disk, git may still
                    // hold a stale worktree registration that blocks new
                    // `worktree add` calls with "already exists" errors.
                    await RunGitQuietAsync(cloneRoot, "worktree", "remove", "--force", wtPath);
                    if (Directory.Exists(wtPath))
                    {
                        try { await git.RemoveAsync(wtPath, force: true); wtRemoved++; }
                        catch
                        {
                            try { Directory.Delete(wtPath, recursive: true); wtRemoved++; }
                            catch { /* locked files — will be pruned on next claim */ }
                        }
                    }
                }
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");
                if (wtRemoved > 0) counts["worktreeRemoved"] = wtRemoved;

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
                    Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is not null && p.WaitForExit(10_000) && p.ExitCode == 0)
                    {
                        counts["branchRemoved"] = 1;
                    }
                }
                catch { /* best-effort */ }

                // Phase 50 — spawn-delete path also wipes the remote so a
                // re-spawn of the same story doesn't pull stale history
                // from origin. Best-effort; failures (no remote, branch
                // never pushed, no auth) are silent.
                if (deleteRemoteBranch)
                {
                    var rc = await RunGitQuietAsync(cloneRoot, "push", "origin", "--delete", row.Branch);
                    if (rc == 0) counts["remoteBranchRemoved"] = 1;
                    else counts["remoteBranchRemoveFailed"] = 1;
                }
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
    ///
    /// <para>The git cleanup is critical: delete the remote branch, remove the
    /// local worktree, and delete the local branch ref so
    /// <see cref="RealStoryGitOps.EnsureBranchAsync"/> creates a clean branch
    /// from <c>origin/main</c> instead of reusing the stale one. Without this,
    /// the new run inherits files from the previous attempt (observed:
    /// 2026-05-04, NodeJS artifacts from a prior run leaked into a C#
    /// workstream creating a 2k-file PR with node_modules).</para>
    /// </summary>
    private static async Task<IResult> Restart(
        HttpContext ctx,
        string id,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        IForgeDbPaths paths,
        IEnumerable<IForgeProvider> providers,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // ── 0. Cancel in-flight work (mirrors Cancel endpoint logic) ────
        try { await runner.CancelByTaskAsync(id, ctx.RequestAborted); }
        catch { /* best-effort — don't block restart if runner is down */ }

        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        // ── 1. Delete all deliverables for this workstream ──────────────
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync();
        db.Deliverables.RemoveRange(deliverables);

        // ── 2. Delete transcript events ─────────────────────────────────
        var events = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync();
        db.TaskTranscriptEvents.RemoveRange(events);

        // ── 3. Delete open questions ────────────────────────────────────
        var questions = await db.DeliverableQuestions.Where(q => q.WorkItemId == id).ToListAsync();
        db.DeliverableQuestions.RemoveRange(questions);

        // ── 3b. Wipe Phase 52 pair-attempt history + cost ledger ────────
        // Operator-observed 2026-05-13: Restart used to leave
        // agent_pair_assignment, agent_usage, WorkItemRow.PairAttemptCount,
        // and WorkItemRow.BudgetWindowStartUtc untouched. That left a
        // freshly-restarted workstream born at PairAttemptCount=3 with the
        // prior pairs locked into PriorPairsJson and ~$3,600 of historical
        // generator spend already metered into the dollar/token caps. The
        // SloppyReassignmentReconciler then refused to pick any pair and
        // the orchestrator went straight to terminal-stall on the very
        // first sloppy hit.
        //
        // Wiping these on Restart matches the spec's intent: Restart is
        // the explicit operator unblock that gives the work item a clean
        // slate. agent_usage rows are deleted (not just cutoff via
        // BudgetWindowStartUtc) so the workstream's cost card shows $0
        // for the new attempt — operators were also seeing stale $-totals
        // on stalled rows post-Restart.
        var pairRows = await db.AgentPairAssignments.Where(p => p.WorkItemId == id).ToListAsync();
        db.AgentPairAssignments.RemoveRange(pairRows);

        var usageRows = await db.AgentUsage.Where(u => u.TaskId == id).ToListAsync();
        db.AgentUsage.RemoveRange(usageRows);

        // ── 4. Close upstream PRs and deactivate local rows ─────────────
        var prIds = ParseStringList(ws.PrsJson);
        foreach (var prId in prIds)
        {
            var prRow = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId);
            if (prRow is null) continue;

            // Close the PR upstream (GitHub / ADO) so it doesn't block the
            // new PR on the same branch name.
            if (!string.IsNullOrEmpty(prRow.Provider) && !string.IsNullOrEmpty(prRow.WebUrl))
            {
                try
                {
                    var provider = providers.FirstOrDefault(p =>
                        string.Equals(p.Kind, prRow.Provider, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
                    if (provider is not null)
                    {
                        // Use the existing PATCH endpoint to close the PR.
                        // GitHub: PATCH /repos/{owner}/{repo}/pulls/{number} { "state": "closed" }
                        await provider.ClosePullRequestAsync(prId, ProviderCallContext.Anonymous, ctx.RequestAborted);
                    }
                }
                catch { /* best-effort — PR may already be closed or deleted */ }
            }

            prRow.IsActive = false;
            prRow.Col = "abandoned";
            prRow.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }

        // ── 5. Reset workstream status ──────────────────────────────────
        ws.Status = "active";
        ws.PrsJson = "[]";
        ws.Commits = 0;
        ws.Ahead = 0;
        ws.Behind = 0;
        ws.Conflicts = 0;
        ws.AgentsJson = "[]";
        // Phase 49 — clear the operator-triage reason when leaving the
        // stalled state. Restart is the explicit unblock path; any prior
        // stall reason (build failure, no-progress, non-pass verdict) is
        // no longer applicable once the worktree + deliverables are wiped.
        ws.StallReason = null;

        // Reset the work-item execution status so the orchestrator can
        // re-assign agents and re-claim the workstream.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null)
        {
            // 2026-05-07 — was `null`, but ExecutionStatus is a non-nullable
            // string (Entities.cs:1528) → SQLite NOT NULL violation at
            // SaveChangesAsync. Match the working precedent in
            // TaskExecutionEndpoints.ClearTranscript: queue it ready to
            // re-claim and zero the trial counter.
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.TrialCount = 0;
            wi.AssignedAgentJson = null;

            // Phase 52 reset — pair-attempt counter, prior-pair exclusion
            // list, terminal failure reason, and the dollar/token budget
            // window all return to their post-claim defaults so the new
            // run is genuinely a clean slate. Without these, a Restart on
            // a row that hit MaxPairAttempts (3) stays terminal-failed and
            // the reconciler/orchestrator refuse to drive it forward.
            // PairAttemptCount = 1 matches the Phase 52 migration default
            // and the "first pair currently working" semantic — only
            // SloppyReassignmentReconciler increments it (newAttempt = +1
            // per rotation), so resetting to 1 gives the new pair a full
            // 3-attempt budget before the cap re-applies.
            wi.PairAttemptCount = 1;
            wi.PriorPairsJson = null;
            wi.FailureReason = null;
            wi.LastPairAgentIdsJson = null;
            wi.BudgetWindowStartUtc = DateTimeOffset.UtcNow;

            wi.UpdatedAt = DateTimeOffset.UtcNow;

            // Refresh DescriptionMd from the current story description
            // so restarted tasks pick up any updates the user made.
            if (!string.IsNullOrEmpty(ws.StorySpawnId))
            {
                var spawnForDesc = await db.StorySpawns.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId);
                if (spawnForDesc is not null)
                {
                    var story = await db.ProductStories.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == spawnForDesc.StoryId);
                    if (story is not null && !string.IsNullOrEmpty(story.Description))
                    {
                        wi.DescriptionMd = story.Description;
                    }
                }
            }
        }

        await db.SaveChangesAsync();

        // ── 6. Clear the cached workspace ───────────────────────────────
        var stateDir = paths.Folder;
        var spawnSlug = ws.StorySpawnId ?? "";
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            var wsDir = System.IO.Path.Combine(stateDir, "storyworkspaces", slug, $"impl-{id}");
            if (System.IO.Directory.Exists(wsDir))
            {
                try { System.IO.Directory.Delete(wsDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        // ── 7. Nuke git branch — worktree, local ref, AND remote ────────
        if (!string.IsNullOrEmpty(ws.Branch))
        {
            var repoRow = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ws.RepoId);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.CloneRoot)
                && System.IO.Directory.Exists(repoRow.CloneRoot))
            {
                var cloneRoot = repoRow.CloneRoot;

                // Compute the canonical worktree path for this workstream
                // (engineer rows take the stage-{n.n}-{title} shape; ad-hoc
                // rows take ws-{sanitizedId}). Clean it AND ask git for any
                // additional path it has registered against this branch so
                // operator-customised layouts get reaped too.
                string? canonicalWtPath = null;
                if (!string.IsNullOrEmpty(ws.StorySpawnId))
                {
                    var siblings = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == ws.StorySpawnId)
                        .ToListAsync();
                    if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
                    var slug = Forge.Api.Services.Stories.WorkstreamWorktreeSlug.Compute(ws, siblings);
                    canonicalWtPath = System.IO.Path.Combine(cloneRoot, ".worktree", slug);
                }

                var registeredPath = await TryFindWorktreeForBranchAsync(cloneRoot, ws.Branch);
                foreach (var wtPath in new[] { canonicalWtPath, registeredPath }
                    .Where(p => !string.IsNullOrEmpty(p)).Distinct())
                {
                    // (a) Remove worktree (if registered)
                    await RunGitQuietAsync(cloneRoot, "worktree", "remove", "--force", wtPath!);

                    // (b) Delete directory if it survived the remove
                    if (System.IO.Directory.Exists(wtPath!))
                    {
                        try { System.IO.Directory.Delete(wtPath!, recursive: true); }
                        catch { /* locked files — will be pruned on next tick */ }
                    }
                }

                // (c) Prune stale worktree refs
                await RunGitQuietAsync(cloneRoot, "worktree", "prune");

                // (d) Delete local branch ref so EnsureWorktreeAsync
                //     doesn't resurrect old history.
                await RunGitQuietAsync(cloneRoot, "branch", "-D", ws.Branch);

                // (e) Delete remote branch so EnsureBranchAsync creates
                //     a fresh one from origin/main.
                await RunGitQuietAsync(cloneRoot, "push", "origin", "--delete", ws.Branch);
            }
        }

        await hub.Clients.All.SendAsync("workstream.changed", new { kind = "restarted", id });

        // ── 8. Re-drive the story orchestrator ──────────────────────────
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
    /// Ask <c>git worktree list --porcelain</c> for the worktree path
    /// currently registered against <paramref name="branch"/>. Returns
    /// null when no match. Best-effort; failures return null.
    /// </summary>
    internal static async Task<string?> TryFindWorktreeForBranchAsync(string cloneRoot, string branch)
    {
        var (code, stdout, _) = await RunGitCaptureAsync(cloneRoot, "worktree", "list", "--porcelain");
        if (code != 0 || string.IsNullOrEmpty(stdout)) return null;
        var lines = stdout.Split('\n');
        string? currentWt = null;
        var wantedRef = $"refs/heads/{branch}";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentWt = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line["branch ".Length..];
                if (string.Equals(refName, wantedRef, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(currentWt))
                {
                    return currentWt;
                }
            }
        }
        return null;
    }

    /// <summary>Best-effort git command — returns exit code, never throws.</summary>
    internal static async Task<int> RunGitQuietAsync(string cwd, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return -1;

            // Bounded wait — covers the original 2026-05-12 hang path
            // (push --delete against msazure.visualstudio.com) for both
            // FailFast (DNS/network stalls) and Interactive (unanswered
            // GCM prompt) policies. Default 180s; tunable via env.
            var timeoutSeconds = 180;
            var timeoutRaw = Environment.GetEnvironmentVariable("FORGE_GIT_NETWORK_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0) timeoutSeconds = parsed;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
                return p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return -1;
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Phase 50 — capture variant of <see cref="RunGitQuietAsync"/>. Returns
    /// (exit-code, stdout, stderr) so callers can parse output (e.g.
    /// <c>git branch --list</c>, <c>git ls-remote --heads</c>). Never throws.
    /// </summary>
    internal static async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureAsync(string cwd, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (-1, string.Empty, string.Empty);

            // See RunGitQuietAsync — same timeout rationale.
            var timeoutSeconds = 180;
            var timeoutRaw = Environment.GetEnvironmentVariable("FORGE_GIT_NETWORK_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutRaw, out var parsed) && parsed > 0) timeoutSeconds = parsed;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            // No ct on the read tasks — they drain to EOF naturally
            // when the process exits / is killed, preserving stderr.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
                return (p.ExitCode, await stdoutTask, await stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                string drainedStderr = string.Empty;
                try { drainedStderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort */ }
                return (-1, string.Empty, $"git {string.Join(' ', args)} timed out after {timeoutSeconds}s. Partial stderr: {drainedStderr}");
            }
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    internal static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Phase 56 -- soft retry. Demote the last deliverable's verdict to
    /// <c>iterate</c> so the orchestrator's
    /// <c>DriveOneWorkstreamImplAsync</c> re-enters the gen/eval loop
    /// instead of treating the row as terminal. Preserves the
    /// worktree, deliverables, transcript, agent_pair_assignment,
    /// agent_usage, and any PRs -- the operator's "I want to keep
    /// going, just take another swing" button vs the heavy
    /// <see cref="Restart"/> that wipes everything for a clean slate.
    ///
    /// <para>Use case: workstream stalled because the post-pass gate
    /// suite (Phase 56) exhausted recovery attempts, but the agent's
    /// prior iterations scored close to pass and most of the diff is
    /// good. Retry demotes verdict pass/stalled → iterate so the next
    /// orchestrator tick picks up at iter+1 with the gate-failure
    /// notes already on the row's Notes (flows into the next iter's
    /// evaluator prompt via <c>FormatPriorChain</c>).</para>
    ///
    /// <para>Refused when:
    /// <list type="bullet">
    ///   <item>Workstream not found → 404.</item>
    ///   <item>No prior deliverable → 400 (nothing to demote; use Restart instead).</item>
    ///   <item>Workstream already in a non-retryable status (<c>cancelled</c>, <c>complete</c>, <c>merging</c>) → 409.</item>
    /// </list></para>
    /// </summary>
    private static async Task<IResult> Retry(
        HttpContext ctx,
        string id,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        if (ws.Status is "cancelled" or "complete" or "merging")
        {
            return Results.Problem(
                type: "retry.terminal",
                title: $"Workstream is in terminal status '{ws.Status}'; cannot retry.",
                detail: "Use the Cancel/Restart actions instead, or open a new workstream.",
                statusCode: 409);
        }

        var deliverables = await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync();
        var lastDel = deliverables
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        if (lastDel is null)
        {
            return Results.Problem(
                type: "retry.no-deliverables",
                title: "Nothing to retry -- workstream has no prior deliverables.",
                detail: "Use Restart to dispatch fresh, or wait for the first iteration to land.",
                statusCode: 400);
        }

        // (1) Cancel any in-flight runner work and open queue rows. Mirrors
        // the first two steps of Restart, but preserves everything else.
        try { await runner.CancelByTaskAsync(id, ctx.RequestAborted); }
        catch { /* best-effort */ }

        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (2) Phase 67 — operator-recovery reset. The previous demotion
        // list was incomplete (missing `stalled-questions` / `sloppy` /
        // `stalled-no-convergence`) AND the pair-attempt accounting was
        // never reset, so a Retry on a sloppy-capped row was a no-op:
        // ws.Status flipped to active but `wi.PairAttemptCount = 3`
        // immediately re-tripped the orchestrator's claim-loop cap at
        // StorySpawnOrchestrator.cs:2431 and the row stayed terminal-
        // failed. WorkstreamRecovery centralizes the correct shape:
        // demote any terminal verdict, and when stalled-or-at-cap, also
        // wipe the pair-attempt accounting + agent_pair_assignment +
        // agent_usage rows so the orchestrator gets a clean slate.
        var actor = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";
        var report = await Forge.Api.Services.Stories.WorkstreamRecovery.SoftResetForResumeAsync(
            db, ws, await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id), lastDel,
            operatorActor: actor,
            noteSuffix: "operator clicked Retry",
            ct: ctx.RequestAborted);
        var priorVerdict = report.PriorVerdict;
        var demoted = report.VerdictDemoted;

        // (3) Load work-item row early — needed by both the merged-PR
        // short-circuit and the normal active-reset path below.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);

        // If the PR is already merged, promote straight to "complete" —
        // the gen/eval loop has nothing left to do.
        var pr = await db.Prs.FirstOrDefaultAsync(p => p.Workstream == id);
        if (pr is not null && string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
        {
            ws.Status = "complete";
            ws.StallReason = null;

            if (wi is not null)
            {
                wi.ExecutionStatus = ExecutionStatuses.Completed;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync("workstream.changed", new
            {
                workstreamId = id,
                status = ws.Status,
                kind = "retry-promoted-complete",
                priorVerdict,
                prMerged = true,
            });

            return Results.Ok(new
            {
                workstreamId = id,
                priorVerdict,
                newVerdict = lastDel.Verdict,
                demoted,
                priorIter = lastDel.Iter,
                priorScore = lastDel.Score,
                status = ws.Status,
            });
        }

        ws.Status = "active";
        ws.StallReason = null;

        // (4) Reset work-item ExecutionStatus so the UI un-pulses any
        // stuck "live" / "failed" badge and the orchestrator's claim
        // sweep sees a clean row. Mirrors Restart's reset, minus the
        // PairAttemptCount/BudgetWindowStartUtc/PriorPairsJson wipe
        // (Retry is a continuation, not a clean slate).
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();

        // (5) Emit a workstream-changed event so the live UI picks up the
        // new status without a manual refresh.
        await hub.Clients.All.SendAsync("workstream.changed", new
        {
            workstreamId = id,
            status = ws.Status,
            kind = "retry",
            priorVerdict,
            newVerdict = lastDel.Verdict,
            iter = lastDel.Iter,
        });

        return Results.Ok(new
        {
            workstreamId = id,
            priorVerdict,
            newVerdict = lastDel.Verdict,
            demoted,
            priorIter = lastDel.Iter,
            priorScore = lastDel.Score,
            status = ws.Status,
        });
    }

    /// <summary>
    /// Phase 70 — POST /api/workstreams/{id}/accept. Operator override that
    /// flips the latest deliverable's verdict to <c>pass</c> so the
    /// orchestrator commits the worktree files + opens the PR on its next
    /// tick. Used to bypass false-positive demotions from the Phase 50
    /// checkbox-attention gate, Phase 56 post-pass gate suite, or Phase 66
    /// stalled-questions gate when the operator has independently verified
    /// the diff is good.
    ///
    /// <para>Mirrors plan-doc <c>StoriesEndpoints.AcceptArtifact</c> but
    /// scoped to a workstream — preserves the worktree, deliverables,
    /// transcript, agent_usage, branch + commits. Does NOT push the
    /// branch or open the PR directly; that's the orchestrator's job
    /// (<c>DriveOneWorkstreamImplAsync</c> sees <c>lastDel.Verdict=pass</c>
    /// and routes through the cached-files commit+PR-open path).</para>
    ///
    /// <para>Refuses with 409 when the workstream is in a terminal status
    /// (<c>cancelled</c> / <c>complete</c> / <c>merging</c>) since
    /// flipping the verdict at that point can't resurrect the workstream.
    /// Idempotent on already-passed deliverables (logs a no-op note + emits
    /// <c>workstream.changed</c> so the UI nudges the orchestrator).</para>
    ///
    /// <para>Observed pain 2026-05-15 on
    /// <c>failover-cluster:XRAFT:stage-4.1-grpc-transport-layer</c> iter 6
    /// score 98 — Phase 50 false positive demoted pass→iterate
    /// indefinitely; operator had no equivalent ACCEPT path the way
    /// plan-docs do. Phase 70 closes the gap.</para>
    /// </summary>
    private static async Task<IResult> Accept(
        HttpContext ctx,
        string id,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        if (ws.Status is "cancelled" or "complete" or "merging")
        {
            return Results.Problem(
                type: "accept.terminal",
                title: $"Workstream is in terminal status '{ws.Status}'; cannot accept.",
                detail: "Use Restart to dispatch fresh, or open a new workstream.",
                statusCode: 409);
        }

        var deliverables = await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync();
        var lastDel = deliverables
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        if (lastDel is null)
        {
            return Results.Problem(
                type: "accept.no-deliverables",
                title: "Nothing to accept — workstream has no prior deliverables.",
                detail: "Wait for at least one iteration to land before ACCEPT.",
                statusCode: 400);
        }

        var priorVerdict = lastDel.Verdict;
        var priorScore = lastDel.Score;
        var alreadyPassed = string.Equals(lastDel.Verdict, "pass", StringComparison.OrdinalIgnoreCase);

        // (1) Cancel any in-flight runner work and open queue rows so the
        // loop doesn't keep iterating while the orchestrator transitions
        // to the commit+PR-open path. Mirrors Retry's first two steps.
        try { await runner.CancelByTaskAsync(id, ctx.RequestAborted); }
        catch { /* best-effort */ }

        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (2) Flip the latest deliverable verdict to `pass` and append an
        // audit note. The orchestrator's DriveOneWorkstreamImplAsync reads
        // `lastDel.Verdict == "pass"` (line ~2238) to route into the
        // ReadCachedWorkstreamFilesAsync commit-and-PR-open branch.
        if (!alreadyPassed)
        {
            lastDel.Verdict = "pass";
            var actor = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";
            var note = $"OPERATOR ACCEPT ({actor}) — verdict overridden from '{priorVerdict}' to 'pass' at score {priorScore}. Worktree, deliverables, transcript, agent_usage, branch, and any prior PR commits are preserved. Orchestrator's next tick will commit the worktree files and open the PR via the cached-files branch.";
            lastDel.Notes = string.IsNullOrEmpty(lastDel.Notes) ? note : lastDel.Notes + "\n\n" + note;
        }

        // (3) Reset workstream + work-item state so the orchestrator's
        // claim sweep / ExecBadge unwedges. Status="active" so the
        // for-each-workstream loop in DriveImplementAsync picks it up
        // on the next tick; ExecutionStatus="queued" so the UI badge
        // un-pulses any stuck live state.
        ws.Status = "active";
        ws.StallReason = null;

        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();

        // (4) Emit workstream-changed so the live UI refreshes without a
        // manual reload. The orchestrator's next tick (~15s) does the
        // commit + PR-open work.
        await hub.Clients.All.SendAsync("workstream.changed", new
        {
            workstreamId = id,
            status = ws.Status,
            kind = "accepted",
            priorVerdict,
            newVerdict = lastDel.Verdict,
            iter = lastDel.Iter,
            score = lastDel.Score,
            alreadyPassed,
        });

        return Results.Ok(new
        {
            workstreamId = id,
            priorVerdict,
            newVerdict = lastDel.Verdict,
            alreadyPassed,
            iter = lastDel.Iter,
            score = lastDel.Score,
            status = ws.Status,
            queueRowsCancelled = queueRows.Count,
        });
    }

    /// <summary>
    /// POST /api/workstreams/{id}/cancel — stop the currently running gen/eval
    /// loop for this workstream. Kills the running agent invocation(s),
    /// marks queue rows as cancelled, and flips ExecutionStatus to idle.
    ///
    /// <para>Before 2026-05-04 this endpoint only flipped DB state — the
    /// in-flight agent stream was unaware of the cancellation and kept
    /// emitting events for minutes after the operator clicked Cancel.
    /// Now we route through <see cref="IAgentRunner.CancelByTaskAsync"/>
    /// which cancels the per-invocation CTS — for Claude/Copilot CLI the
    /// dispatcher's <c>ctReg.Register</c> calls <c>process.Kill(entireProcessTree: true)</c>
    /// on cancel, so the subprocess dies. For the SDK paths the streaming
    /// HTTP request errors out and the agent loop exits.</para>
    /// </summary>
    private static async Task<IResult> Cancel(
        HttpContext ctx,
        string id,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == id);
        if (ws is null) return Results.Problem(title: $"Workstream {id} not found", statusCode: 404);

        // (1) Kill in-flight agent invocations FIRST so no stray events land
        // after we flip the DB state below. Best-effort — the runner is
        // tolerant of unknown taskIds (returns 0).
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await runner.CancelByTaskAsync(id, ctx.RequestAborted);
        }
        catch
        {
            // Don't let runner failures prevent DB cleanup — the queue row
            // is the source of truth for whether the workstream is "running".
        }

        // (2) Cancel any queued/active task_queue rows for this workstream
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (3) Mark the work item execution as cancelled — TERMINAL state.
        // Earlier this code wrote "idle" which the StorySpawnOrchestrator
        // tick treats as re-claimable, so 15 seconds after a cancel a fresh
        // iter would start (observed 2026-05-04). "cancelled" is a real
        // terminal status and the orchestrator skips it.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Mark the workstream itself as cancelled so DriveImplementAsync
        // skips it on subsequent ticks. The Restart endpoint flips it back
        // to "active" and clears deliverables/transcripts to revive the loop.
        ws.Status = "cancelled";

        // (5) Mark the last deliverable as stalled if it's in iterate state
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

        return Results.Ok(new
        {
            id,
            status = "cancelled",
            queueRowsCancelled = queueRows.Count,
            invocationsCancelled,
        });
    }
}
