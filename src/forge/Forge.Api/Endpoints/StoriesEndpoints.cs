// -----------------------------------------------------------------------
// <copyright file="StoriesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 28 — story-driven spawn endpoints. The
/// operator selects a story (an ADO Feature / Epic / PBI) and Forge
/// orchestrates the multi-team delivery lifecycle:
/// plan → decompose → implement → review → qa → done. Each phase advance
/// is gated on its preceding artifact PR being merged.
/// </summary>
public static class StoriesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Set to <c>false</c> via config <c>Forge:Stories:OrchestratorEnabled</c>
    /// to skip the auto-orchestrator and only run the gate logic. Tests
    /// covering the manual-API contract use this; production stays on.
    /// </summary>
    private static bool OrchestratorEnabled(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetService<IConfiguration>();
        var raw = cfg?["Forge:Stories:OrchestratorEnabled"];
        if (string.IsNullOrEmpty(raw)) return true;
        return bool.TryParse(raw, out var v) ? v : true;
    }

    /// <summary>
    /// When <c>true</c> (default), POST /spawn awaits the orchestrator drive
    /// before returning so callers can read <c>PlanPrId</c> in the response.
    /// Tests rely on this. When <c>false</c> (production via
    /// <c>Forge:Stories:SpawnDriveInline=false</c> in appsettings.json), the
    /// drive is fire-and-forget — the response returns 201 immediately and
    /// the architect crew runs in the background. Required because real-LLM
    /// architect runs take many minutes, which would otherwise freeze the
    /// spawn-button on the UI. <see cref="SpawnDependencyScheduler"/> ticks
    /// every 15 s and will re-drive the spawn if the background task falls
    /// over.
    /// </summary>
    private static bool SpawnDriveInline(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetService<IConfiguration>();
        var raw = cfg?["Forge:Stories:SpawnDriveInline"];
        if (string.IsNullOrEmpty(raw)) return true;
        return bool.TryParse(raw, out var v) ? v : true;
    }

    public static WebApplication MapStoriesEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/stories").WithTags("Stories");

        grp.MapPost("/{storyId}/spawn", Spawn).WithName("SpawnStory");
        grp.MapGet("/spawns", ListSpawns).WithName("ListStorySpawns");
        grp.MapGet("/{storyId}/spawn", GetSpawn).WithName("GetStorySpawn");
        grp.MapPatch("/{storyId}/spawn/phase", AdvancePhase).WithName("AdvanceStoryPhase");
        grp.MapPost("/{storyId}/spawn/decompose", SubmitDecomposition).WithName("SubmitStoryDecomposition");
        grp.MapPost("/{storyId}/spawn/decompose/approve", ApproveDecomposition).WithName("ApproveStoryDecomposition");
        grp.MapGet("/{storyId}/spawn/artifacts", GetArtifacts).WithName("GetStoryArtifacts");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}", GetArtifactContent).WithName("GetStoryArtifactContent");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}/iterations", GetArtifactIterations).WithName("GetStoryArtifactIterations");

        return app;
    }

    public sealed record SpawnRequest(string? RepoId);

    public sealed record DecomposeRequest(IReadOnlyList<DecomposeWorkstream> Workstreams);

    public sealed record DecomposeWorkstream(
        string Title,
        string? Description,
        IReadOnlyList<string>? AcceptanceCriteria,
        IReadOnlyList<string>? Deps,
        IReadOnlyList<string>? Deliverables,
        string? Timeline,
        string? EstimatedEffort);

    public sealed record AdvancePhaseRequest(string TargetPhase);

    private static async Task<IResult> Spawn(
        HttpContext ctx,
        string storyId,
        [FromBody] SpawnRequest? req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(storyId))
        {
            return Results.Problem(title: "storyId is required", statusCode: 400);
        }

        // 28.1 — duplicate spawn for the same story is rejected with 409.
        var existing = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (existing is not null)
        {
            return Results.Problem(
                title: $"Spawn already exists for story {storyId}",
                detail: $"Spawn id {existing.Id} is in phase '{existing.Phase}'.",
                statusCode: 409);
        }

        // 28.2 — story must exist. Three sources accepted:
        //   1) ProductStoryRow (locally-authored story; description carries the
        //      architect-agent user prompt and points size the work).
        //   2) WorkItemRow by id (e.g. "WI-12345" mirroring an ADO Feature/Epic).
        //   3) WorkItemRow by numeric ADO provider ref (bare "12345" / "WI-12345").
        string title;
        string description = string.Empty;
        int points = 0;
        string? defaultRepoId = null;

        var ps = await db.ProductStories.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storyId);
        if (ps is not null)
        {
            title = ps.Name;
            description = ps.Description ?? string.Empty;
            points = ps.Points;
            // ProductStoryRow is product-scoped, not repo-scoped; caller must
            // pick a repo via SpawnRequest.RepoId. defaultRepoId stays null.
        }
        else
        {
            var wi = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == storyId);
            if (wi is null)
            {
                if (int.TryParse(storyId.Replace("WI-", string.Empty, StringComparison.OrdinalIgnoreCase), out var providerRef))
                {
                    wi = await db.WorkItems.AsNoTracking()
                        .FirstOrDefaultAsync(w => w.ProviderRef == providerRef);
                }
                if (wi is null)
                {
                    return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);
                }
            }
            title = wi.Title;
            description = wi.DescriptionMd ?? string.Empty;
            points = wi.Points;
            defaultRepoId = wi.RepoId;
        }

        var repoId = req?.RepoId ?? defaultRepoId;
        var now = DateTimeOffset.UtcNow;
        var safeStoryId = StoryPaths.Safe(storyId);
        var spawn = new StorySpawnRow
        {
            Id = $"spawn-{safeStoryId}",
            StoryId = storyId,
            StoryTitle = title,
            StoryDescription = description,
            StoryPoints = points,
            RepoId = repoId,
            Phase = StorySpawnPhases.Plan,
            // Branch must use the sanitized id — `:` is illegal in Windows
            // worktree paths and crashes the orchestrator on commit.
            PlanBranch = $"story/{safeStoryId}/plan",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StorySpawns.Add(spawn);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "created", spawn = ToContract(spawn) });

        // forge_implementation.md Phase 28 — kick the orchestrator so the
        // architect crew can produce the plan artifacts + open the plan PR.
        // Production runs fire-and-forget (Forge:Stories:SpawnDriveInline=false
        // in appsettings.json) because real-LLM architect runs take many
        // minutes; tests keep the inline await so PlanPrId is populated by
        // the time the response is read.
        if (OrchestratorEnabled(ctx))
        {
            if (SpawnDriveInline(ctx))
            {
                await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
                var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
                return Results.Created($"/api/stories/{storyId}/spawn", ToContract(refreshed));
            }

            // Background drive — must NOT use ctx.RequestAborted (the HTTP
            // request ends as soon as we return). StorySpawnOrchestrator is
            // a singleton, so capturing it here is safe across the request
            // boundary. Failures are logged inside DriveAsync; the
            // SpawnDependencyScheduler will re-drive on the next 15 s tick.
            _ = Task.Run(async () =>
            {
                try { await orchestrator.DriveAsync(storyId, CancellationToken.None); }
                catch { /* swallowed — orchestrator already logs */ }
            });
        }
        return Results.Created($"/api/stories/{storyId}/spawn", ToContract(spawn));
    }

    private static async Task<IResult> ListSpawns(ForgeDbContext db, [FromQuery] string? phase)
    {
        var q = db.StorySpawns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(phase) && !string.Equals(phase, "all", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(s => s.Phase == phase);
        }
        // SQLite can't ORDER BY DateTimeOffset — materialize then sort client-side.
        var rows = await q.ToListAsync();
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return Results.Ok(rows.Select(ToContract).ToList());
    }

    private static async Task<IResult> GetSpawn(string storyId, ForgeDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null)
        {
            return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
        }

        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync();

        var wsIds = workstreams.Select(w => w.Id).ToList();

        var deps = await db.WorkstreamDeps.AsNoTracking()
            .Where(d => wsIds.Contains(d.WorkstreamId))
            .ToListAsync();

        // Per-workstream live runtime data: every deliverable in the
        // CrewStoryArtifactStrategy gen/eval loop is keyed on workstream.Id
        // (see CrewStoryArtifactStrategy.ProduceWorkstreamChangesAsync — the
        // workItemId argument is the workstream id). So the deliverables
        // table is the source of truth for "what's the engineer pair doing
        // right now" per workstream — no per-workstream work_items row
        // required.
        var deliverables = await db.Deliverables.AsNoTracking()
            .Where(d => wsIds.Contains(d.WorkItemId))
            .ToListAsync();
        var deliverablesByWs = deliverables
            .GroupBy(d => d.WorkItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedAt).ToList(), StringComparer.OrdinalIgnoreCase);

        // Cost ledger is keyed on TaskId — the loop writes the workstream id
        // there too. Sum once per ws to avoid per-card queries.
        var costByWs = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId != null && wsIds.Contains(u.TaskId))
            .GroupBy(u => u.TaskId!)
            .Select(g => new { TaskId = g.Key, Cost = g.Sum(x => x.CostUsd) })
            .ToListAsync();
        var costByWsMap = costByWs.ToDictionary(x => x.TaskId, x => x.Cost, StringComparer.OrdinalIgnoreCase);

        var wsByStatus = workstreams.Select(w =>
        {
            var wsAgents = StoriesJsonHelpers.Deserialize(w.AgentsJson);
            var wsDeliverables = deliverablesByWs.TryGetValue(w.Id, out var ds) ? ds : new List<DeliverableRow>();
            var latest = wsDeliverables.FirstOrDefault();
            var wsCost = costByWsMap.TryGetValue(w.Id, out var c) ? c : 0m;
            return new
            {
                id = w.Id,
                name = w.Name,
                branch = w.Branch,
                status = ResolveWorkstreamStatus(w.Id, w.Status, deps, workstreams, db),
                cards = StoriesJsonHelpers.Deserialize(w.CardsJson),
                prs = StoriesJsonHelpers.Deserialize(w.PrsJson),
                deps = deps.Where(d => d.WorkstreamId == w.Id).Select(d => d.DependsOnId).ToArray(),
                // ── live runtime fields ──────────────────────────────────
                agents = wsAgents,
                iter = wsDeliverables.Count,
                latestScore = (int?)(latest?.Score),
                latestVerdict = latest?.Verdict,
                latestGenAgentId = latest?.GeneratorAgentId,
                latestEvalAgentId = latest?.EvaluatorAgentId,
                lastActiveAt = latest?.CreatedAt.ToString("O"),
                costUsd = (double)wsCost,
                commits = w.Commits,
                ahead = w.Ahead,
                behind = w.Behind,
            };
        }).ToArray();

        return Results.Ok(new
        {
            spawn = ToContract(spawn),
            workstreams = wsByStatus,
        });
    }

    private static string ResolveWorkstreamStatus(
        string wsId,
        string baseStatus,
        IReadOnlyList<WorkstreamDepRow> deps,
        IReadOnlyList<WorkstreamRow> all,
        ForgeDbContext db)
    {
        // 28.5 / 28.11 — surface "blocked" when a dependency PR is unmerged.
        var depIds = deps.Where(d => d.WorkstreamId == wsId).Select(d => d.DependsOnId).ToArray();
        if (depIds.Length == 0) return baseStatus;
        foreach (var dep in depIds)
        {
            var depWs = all.FirstOrDefault(w => w.Id == dep);
            if (depWs is null) continue;
            // "merging" or "complete" upstream lets a downstream start.
            if (depWs.Status != "merging" && depWs.Status != "complete")
            {
                return baseStatus == "planning" ? "blocked" : baseStatus;
            }
        }
        return baseStatus;
    }

    private static async Task<IResult> AdvancePhase(
        HttpContext ctx,
        string storyId,
        [FromBody] AdvancePhaseRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (req is null || string.IsNullOrWhiteSpace(req.TargetPhase))
        {
            return Results.Problem(title: "targetPhase is required", statusCode: 400);
        }

        var currentIdx = StorySpawnPhases.IndexOf(spawn.Phase);
        var targetIdx = StorySpawnPhases.IndexOf(req.TargetPhase);
        if (targetIdx < 0)
        {
            return Results.Problem(title: $"Unknown phase '{req.TargetPhase}'", statusCode: 400);
        }

        // Cross-cutting — phase rollback is a non-goal (28 non-goals).
        if (targetIdx <= currentIdx)
        {
            return Results.Problem(
                title: "Phase rollback is not supported",
                detail: $"Cannot move from '{spawn.Phase}' to '{req.TargetPhase}'.",
                statusCode: 422);
        }

        // Only single-step advance unless target is "done" via completion.
        if (targetIdx != currentIdx + 1)
        {
            return Results.Problem(
                title: "Phase advance must be sequential",
                detail: $"Next phase from '{spawn.Phase}' is '{StorySpawnPhases.Order[currentIdx + 1]}', not '{req.TargetPhase}'.",
                statusCode: 422);
        }

        // Gate logic — the Phase 28 "artifact check-in rule".
        var gate = await CheckGateAsync(spawn, req.TargetPhase, db);
        if (gate is not null) return gate;

        spawn.Phase = req.TargetPhase;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "phase-advanced", spawn = ToContract(spawn) });
        // Drive the new phase forward — e.g. on entering 'decompose' the
        // architect crew immediately reads the plan and produces the
        // decomposition; on entering 'implement' the engineer crew claims
        // each ready workstream.
        if (OrchestratorEnabled(ctx)) await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
        var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
        return Results.Ok(ToContract(refreshed));
    }

    /// <summary>
    /// Returns null when the gate is satisfied, or an IResult with the
    /// 422 problem describing the gate violation.
    /// </summary>
    private static async Task<IResult?> CheckGateAsync(StorySpawnRow spawn, string targetPhase, ForgeDbContext db)
    {
        switch (targetPhase)
        {
            case StorySpawnPhases.Decompose:
                {
                    // 28.3 — plan PR must be merged.
                    if (string.IsNullOrEmpty(spawn.PlanPrId))
                    {
                        return Results.Problem(
                            title: "Plan PR must be merged before advancing",
                            detail: "No plan PR is recorded on this spawn.",
                            statusCode: 422);
                    }
                    var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId);
                    if (pr is null || !IsPrMerged(pr))
                    {
                        return Results.Problem(
                            title: "Plan PR must be merged before advancing",
                            detail: $"PR {spawn.PlanPrId} is not yet merged.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Implement:
                {
                    // 28.4 — operator must approve the decomposition.
                    if (!spawn.DecompositionApproved)
                    {
                        return Results.Problem(
                            title: "Decomposition not yet approved",
                            detail: "Submit and approve the decomposition before advancing to 'implement'.",
                            statusCode: 422);
                    }
                    if (string.IsNullOrEmpty(spawn.DecompositionJson))
                    {
                        return Results.Problem(
                            title: "Decomposition not yet submitted",
                            detail: "Decomposition must be persisted before advancing to 'implement'.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Review:
                {
                    // 28.5 → 28.6 — every workstream must have an open PR (status='review').
                    var workstreams = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == spawn.Id)
                        .ToListAsync();
                    if (workstreams.Count == 0)
                    {
                        return Results.Problem(
                            title: "No workstreams to review",
                            detail: "At least one workstream must exist before advancing to 'review'.",
                            statusCode: 422);
                    }
                    var notReady = workstreams.Where(w => w.Status != "review" && w.Status != "merging" && w.Status != "complete").ToList();
                    if (notReady.Count > 0)
                    {
                        return Results.Problem(
                            title: "All workstream PRs must be open (status='review') before advancing",
                            detail: $"{notReady.Count} workstream(s) not ready: {string.Join(", ", notReady.Select(w => w.Id))}.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Qa:
                {
                    // 28.6 → 28.7 — all workstream PRs approved (status in merging/complete).
                    var workstreams = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == spawn.Id)
                        .ToListAsync();
                    var notMerged = workstreams.Where(w => w.Status != "merging" && w.Status != "complete").ToList();
                    if (notMerged.Count > 0)
                    {
                        return Results.Problem(
                            title: "All workstream PRs must be merged before advancing to 'qa'",
                            detail: $"{notMerged.Count} workstream(s) not yet merged.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Done:
                {
                    return await CheckCompletionAsync(spawn, db);
                }
        }
        return null;
    }

    /// <summary>
    /// 28.8 — story is complete only when every workstream PR is merged,
    /// the QA feature PR is merged, and all e2e tests pass on main.
    /// </summary>
    private static async Task<IResult?> CheckCompletionAsync(StorySpawnRow spawn, ForgeDbContext db)
    {
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync();
        var unmerged = workstreams.Count(w => w.Status != "merging" && w.Status != "complete");
        if (unmerged > 0)
        {
            return Results.Problem(
                title: "Story not complete",
                detail: $"{unmerged}/{workstreams.Count} workstream PR(s) still unmerged.",
                statusCode: 422);
        }
        if (!string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qa = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.QaPrId);
            if (qa is null || !IsPrMerged(qa))
            {
                return Results.Problem(
                    title: "QA feature PR must be merged",
                    detail: $"QA PR {spawn.QaPrId} is not yet merged.",
                    statusCode: 422);
            }
        }
        // E2E tests pass on main — proxied by the absence of failing builds on
        // the main branch for this repo. If the spawn has no recorded builds,
        // assume tests have not yet run.
        if (spawn.RepoId is not null)
        {
            var failingOnMain = await db.Builds.AsNoTracking()
                .Where(b => b.RepoId == spawn.RepoId && b.Branch == "main"
                    && (b.Result == "failed" || b.Result == "failure" || b.Result == "partiallySucceeded"))
                .CountAsync();
            if (failingOnMain > 0)
            {
                return Results.Problem(
                    title: "E2E tests failing on main",
                    detail: $"{failingOnMain} failing build(s) on main for this repo.",
                    statusCode: 422);
            }
        }
        return null;
    }

    private static bool IsPrMerged(PrRow pr) =>
        string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> SubmitDecomposition(
        HttpContext ctx,
        string storyId,
        [FromBody] DecomposeRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (spawn.Phase != StorySpawnPhases.Decompose)
        {
            return Results.Problem(
                title: "Spawn must be in 'decompose' phase to submit a decomposition",
                detail: $"Current phase: '{spawn.Phase}'.",
                statusCode: 422);
        }

        if (req is null || req.Workstreams is null || req.Workstreams.Count == 0)
        {
            return Results.Problem(
                title: "At least one workstream is required",
                statusCode: 422);
        }

        // Validate metadata — 28.4 each entry must have title (other fields default).
        foreach (var w in req.Workstreams)
        {
            if (string.IsNullOrWhiteSpace(w.Title))
            {
                return Results.Problem(title: "Each workstream needs a title", statusCode: 422);
            }
        }

        // 28.4 — circular dependency detection. Build adjacency by title-slug.
        var titleToSlug = req.Workstreams
            .ToDictionary(w => w.Title, w => SlugFor(w.Title), StringComparer.OrdinalIgnoreCase);
        var slugSet = new HashSet<string>(titleToSlug.Values, StringComparer.OrdinalIgnoreCase);

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in req.Workstreams)
        {
            var fromSlug = titleToSlug[w.Title];
            adjacency[fromSlug] = new List<string>();
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                // Accept either the title or the slug.
                var depSlug = titleToSlug.TryGetValue(dep, out var s) ? s : dep;
                if (!slugSet.Contains(depSlug))
                {
                    return Results.Problem(
                        title: $"Unknown dependency '{depRaw}'",
                        statusCode: 422);
                }
                adjacency[fromSlug].Add(depSlug);
            }
        }
        if (HasCycle(adjacency))
        {
            return Results.Problem(
                title: "Circular dependency detected",
                statusCode: 422);
        }

        // Persist workstreams + dep edges.
        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync() ?? -1;
        var createdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in req.Workstreams)
        {
            var slug = titleToSlug[w.Title];
            var safeStory = StoryPaths.Safe(spawn.StoryId);
            var wsId = $"ws-{safeStory}-{slug}".ToLowerInvariant();
            createdMap[slug] = wsId;
            var row = new WorkstreamRow
            {
                Id = wsId,
                Sort = ++maxSort,
                RepoId = spawn.RepoId,
                Name = w.Title,
                Branch = $"ws/{safeStory}/{slug}",
                Base = "main",
                Epic = spawn.StoryId,
                Status = "planning",
                WorktreeJson = "[\"(not yet created)\"]",
                Created = 0,
                LastActive = 0,
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Commits = 0,
                Ahead = 0,
                Behind = 0,
                Conflicts = 0,
                Summary = w.Description ?? $"Workstream {w.Title} for story {spawn.StoryId}",
                BarStart = 0,
                BarEnd = 5,
                Planned = true,
                StorySpawnId = spawn.Id,
            };
            db.Workstreams.Add(row);
        }

        foreach (var w in req.Workstreams)
        {
            var fromId = createdMap[titleToSlug[w.Title]];
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                var depSlug = titleToSlug.TryGetValue(dep, out var s) ? s : dep;
                var dependsOnId = createdMap[depSlug];
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = fromId,
                    DependsOnId = dependsOnId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        spawn.DecompositionJson = JsonSerializer.Serialize(req.Workstreams, JsonOpts);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "decomposed", spawn = ToContract(spawn) });
        return Results.Ok(new
        {
            spawn = ToContract(spawn),
            workstreams = createdMap.Values.ToArray(),
        });
    }

    private static async Task<IResult> ApproveDecomposition(
        HttpContext ctx,
        string storyId,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.IsNullOrEmpty(spawn.DecompositionJson))
        {
            return Results.Problem(
                title: "No decomposition to approve",
                detail: "Submit a decomposition first.",
                statusCode: 422);
        }
        spawn.DecompositionApproved = true;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "decomposition-approved", spawn = ToContract(spawn) });
        if (OrchestratorEnabled(ctx)) await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
        var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
        return Results.Ok(ToContract(refreshed));
    }

    private static async Task<IResult> GetArtifacts(string storyId, ForgeDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync();
        var workstreamPrs = workstreams
            .SelectMany(w => StoriesJsonHelpers.Deserialize(w.PrsJson) ?? new List<string>())
            .ToArray();

        var safeStoryId = StoryPaths.Safe(storyId);
        var planArtifacts = new[]
        {
            $"docs/stories/{safeStoryId}/tech-spec.md",
            $"docs/stories/{safeStoryId}/architecture.md",
            $"docs/stories/{safeStoryId}/implementation-plan.md",
            $"docs/stories/{safeStoryId}/e2e-scenarios.md",
        };
        var qaArtifacts = string.IsNullOrEmpty(spawn.QaPrId)
            ? Array.Empty<string>()
            : new[] { $"tests/Features/{safeStoryId}/" };

        return Results.Ok(new
        {
            plan = new
            {
                branch = spawn.PlanBranch,
                pr = spawn.PlanPrId,
                files = planArtifacts,
            },
            decompose = new
            {
                approved = spawn.DecompositionApproved,
                json = spawn.DecompositionJson,
            },
            implement = new
            {
                prs = workstreamPrs,
                workstreams = workstreams.Select(w => w.Id).ToArray(),
            },
            review = new
            {
                prs = workstreamPrs,
            },
            qa = new
            {
                branch = spawn.QaBranch,
                pr = spawn.QaPrId,
                files = qaArtifacts,
            },
        });
    }

    /// <summary>
    /// Serve the actual markdown body for a single plan artifact. The plan
    /// PR has merged the four documents to <c>docs/stories/{safeStoryId}/</c>
    /// on main; this endpoint reads them straight off the operator's local
    /// working tree (same files git would show on `git checkout main`).
    ///
    /// <para>Valid keys: architecture | tech-spec | implementation-plan |
    /// e2e-scenarios | decomposition. The first four read MD files from disk;
    /// <c>decomposition</c> returns the spawn's stored decomposition JSON
    /// formatted as a markdown-friendly preview.</para>
    /// </summary>
    private static async Task<IResult> GetArtifactContent(string storyId, string key, ForgeDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "architecture", "tech-spec", "implementation-plan", "e2e-scenarios", "decomposition",
        };
        if (!allowed.Contains(key))
        {
            return Results.Problem(title: $"Unknown artifact key '{key}'", statusCode: 400);
        }

        // Decomposition has no on-disk MD — surface the stored JSON so the
        // UI can render the workstream breakdown the same way as a doc.
        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                key,
                path = (string?)null,
                content = spawn.DecompositionJson ?? string.Empty,
                lastModifiedAt = (string?)null,
                exists = !string.IsNullOrEmpty(spawn.DecompositionJson),
            });
        }

        var safeStoryId = StoryPaths.Safe(storyId);
        var relPath = $"docs/stories/{safeStoryId}/{key.ToLowerInvariant()}.md";
        var repoRoot = ForgeSeed.FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(fullPath))
        {
            var content = await File.ReadAllTextAsync(fullPath);
            var lastModified = File.GetLastWriteTimeUtc(fullPath).ToString("O");
            return Results.Ok(new
            {
                key,
                path = relPath,
                content,
                lastModifiedAt = lastModified,
                exists = true,
                source = "main",
            });
        }

        // File isn't on main yet — check the plan branch (architect crew has
        // committed but PR not merged). git show returns the file as it
        // exists on the branch tip without mutating the working tree.
        if (!string.IsNullOrEmpty(spawn.PlanBranch))
        {
            var fromBranch = await TryGitShowAsync(repoRoot, spawn.PlanBranch!, relPath);
            if (fromBranch is not null)
            {
                return Results.Ok(new
                {
                    key,
                    path = relPath,
                    content = fromBranch,
                    lastModifiedAt = (string?)null,
                    exists = true,
                    source = $"branch:{spawn.PlanBranch}",
                });
            }
        }

        // Not on main, not on plan branch — plan hasn't produced this file
        // yet. Return exists=false so the UI can render an empty state.
        return Results.Ok(new
        {
            key,
            path = relPath,
            content = string.Empty,
            lastModifiedAt = (string?)null,
            exists = false,
            source = (string?)null,
        });
    }

    /// <summary>
    /// `git show {branch}:{path}` against the operator's local clone. Tries
    /// the local branch first, then <c>origin/{branch}</c> — the orchestrator
    /// commits + pushes to origin without checking the branch out locally,
    /// so the local-only ref usually doesn't exist. Returns null when neither
    /// ref resolves the path (treated as "not produced yet" by callers).
    /// </summary>
    private static async Task<string?> TryGitShowAsync(string repoRoot, string branch, string relPath)
    {
        return await TryGitShowOneAsync(repoRoot, branch, relPath)
            ?? await TryGitShowOneAsync(repoRoot, $"origin/{branch}", relPath);
    }

    private static async Task<string?> TryGitShowOneAsync(string repoRoot, string refSpec, string relPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add($"{refSpec}:{relPath}");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Per-doc iteration history. Each plan file's gen↔eval loop writes one
    /// <see cref="DeliverableRow"/> per iteration keyed on the synthetic
    /// workItemId <c>plan-doc:{spawn.Id}:{filename}</c> (matches the convention
    /// in <see cref="Forge.Api.Services.Stories.CrewStoryArtifactStrategy.ProducePlanFileAsync"/>).
    /// Decomposition has no per-doc iterations — returns empty.
    /// </summary>
    private static async Task<IResult> GetArtifactIterations(string storyId, string key, ForgeDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                key,
                workItemId = (string?)null,
                iterations = Array.Empty<object>(),
                totalCostUsd = 0.0,
            });
        }

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";

        // SQLite can't ORDER BY DateTimeOffset directly. Order by Iter on the
        // server (cheap index hit) and then re-sort by CreatedAt on the client
        // for the rare same-iter tiebreak case.
        var rows = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .OrderBy(d => d.Iter)
            .ToListAsync())
            .OrderBy(d => d.Iter)
            .ThenBy(d => d.CreatedAt)
            .ToList();

        var totalCost = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == workItemId)
            .SumAsync(u => (double?)u.CostUsd) ?? 0.0;

        var iterations = rows.Select(d => new
        {
            id = d.Id,
            iter = d.Iter,
            score = d.Score,
            verdict = d.Verdict,
            generatorAgentId = d.GeneratorAgentId,
            evaluatorAgentId = d.EvaluatorAgentId,
            generatorSummary = d.GeneratorSummary,
            notes = d.Notes,
            genCostUsd = d.GenCostUsd,
            evalCostUsd = d.EvalCostUsd,
            judgeAgentId = d.JudgeAgentId,
            judgeScore = d.JudgeScore,
            judgeNotes = d.JudgeNotes,
            createdAt = d.CreatedAt.ToString("O"),
        });

        return Results.Ok(new
        {
            key,
            workItemId,
            iterations,
            totalCostUsd = totalCost,
        });
    }

    private static object ToContract(StorySpawnRow row) => new
    {
        id = row.Id,
        storyId = row.StoryId,
        storyTitle = row.StoryTitle,
        storyDescription = row.StoryDescription,
        storyPoints = row.StoryPoints,
        repoId = row.RepoId,
        phase = row.Phase,
        planBranch = row.PlanBranch,
        planPrId = row.PlanPrId,
        decompositionJson = row.DecompositionJson,
        decompositionApproved = row.DecompositionApproved,
        qaBranch = row.QaBranch,
        qaPrId = row.QaPrId,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
    };

    private static string SlugFor(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "ws" : s;
    }

    private static bool HasCycle(Dictionary<string, List<string>> adjacency)
    {
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // 0 = unvisited, 1 = on-stack, 2 = done.
        foreach (var node in adjacency.Keys)
        {
            if (DfsHasCycle(node, adjacency, visited)) return true;
        }
        return false;
    }

    private static bool DfsHasCycle(
        string node,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, int> visited)
    {
        if (!visited.TryGetValue(node, out var state)) state = 0;
        if (state == 1) return true;
        if (state == 2) return false;
        visited[node] = 1;
        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var n in neighbors)
            {
                if (DfsHasCycle(n, adjacency, visited)) return true;
            }
        }
        visited[node] = 2;
        return false;
    }
}

/// <summary>
/// forge_implementation.md Phase 28 — small helper for parsing the JSON
/// arrays stored on <see cref="WorkstreamRow.CardsJson"/> /
/// <see cref="WorkstreamRow.PrsJson"/>. Mirrors <c>CardMapper</c> but
/// scoped narrowly so this endpoint compiles independently.
/// </summary>
internal static class StoriesJsonHelpers
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static List<string> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, Opts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }
}
