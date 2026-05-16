// -----------------------------------------------------------------------
// <copyright file="StateEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// GET /api/state — aggregated payload that lights up every view of the prototype
/// from a single round-trip. Phase 1+ replaces this with per-resource endpoints.
/// </summary>
public static class StateEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WebApplication MapStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/state", async (HttpContext ctx, ProductDbContext db) =>
        {
            // Phase 8.9 — scope cards / workstreams / prs to the active product.
            // Epics, columns, ws-events, ws-stats, overview-* are NOT repo-scoped
            // today (they describe the workspace as a whole) so they ship as-is.
            var scope = await ProductScope.ResolveAsync(ctx, db);
            var epics = await db.Epics.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            var cardsQ = db.Cards.AsNoTracking().AsQueryable();
            var wsQ = db.Workstreams.AsNoTracking().AsQueryable();
            var prsQ = db.Prs.AsNoTracking().AsQueryable();
            if (!scope.IsAll)
            {
                var ids = scope.RepoIds!;
                cardsQ = cardsQ.Where(c => c.RepoId != null && ids.Contains(c.RepoId));
                wsQ = wsQ.Where(w => w.RepoId != null && ids.Contains(w.RepoId));
                prsQ = prsQ.Where(p => p.RepoId != null && ids.Contains(p.RepoId));
            }
            var cards = await cardsQ.OrderBy(c => c.Sort).ToListAsync();

            // Phase 17 follow-up — when the active product's repos have no
            // cards (the legacy table is sparse) but DO have work items
            // (Phase 12 first-class WIT), project the work items into the
            // legacy Card shape so the Plan & Design board isn't empty.
            // This is the bridge that Phase 12.2's "cards become a
            // projection over work_items" deferral describes; here it lights
            // up read-only as a fallback only — nothing is written back.
            var projectedCards = new List<Card>();
            if (cards.Count == 0)
            {
                var wiQ = db.WorkItems.AsNoTracking().AsQueryable();
                if (!scope.IsAll)
                {
                    var ids = scope.RepoIds!;
                    wiQ = wiQ.Where(w => w.RepoId != null && ids.Contains(w.RepoId));
                }
                var workItems = await wiQ.ToListAsync();
                projectedCards = workItems.Select(WorkItemToCardProjection).ToList();
            }
            var columns = await db.Columns.AsNoTracking().OrderBy(c => c.Sort).ToListAsync();
            var workstreams = await wsQ.OrderBy(w => w.Sort).ToListAsync();
            var wsEvents = await db.WsEvents.AsNoTracking().OrderBy(w => w.Sort).ToListAsync();
            var prs = await prsQ.OrderBy(p => p.Sort).ToListAsync();
            var wsStats = await db.WsStats.AsNoTracking().OrderBy(s => s.Sort).ToListAsync();
            var activity = await db.OverviewActivity.AsNoTracking().OrderBy(a => a.Sort).ToListAsync();
            var agents = await db.OverviewAgents.AsNoTracking().OrderBy(a => a.Sort).ToListAsync();

            var payload = new ForgeState(
                Epics: epics.Select(MapEpic).ToList(),
                Cards: cards.Count > 0 ? cards.Select(MapCard).ToList() : projectedCards,
                Columns: columns.Select(MapColumn).ToList(),
                Workstreams: workstreams.Select(MapWorkstream).ToList(),
                WsEvents: wsEvents.Select(MapWsEvent).ToList(),
                Prs: prs.Select(MapPr).ToList(),
                WsStats: wsStats.Select(MapWsStat).ToList(),
                OverviewActivity: activity.Select(MapActivity).ToList(),
                OverviewAgents: agents.Select(MapAgent).ToList());

            return Results.Ok(payload);
        })
        .WithTags("State")
        .WithName("GetState");

        var epicsGrp = app.MapGroup("/api/epics").WithTags("Epics");
        epicsGrp.MapGet("/", async (ProductDbContext db) =>
        {
            var rows = await db.Epics.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            return Results.Ok(rows.Select(MapEpic).ToList());
        }).WithName("ListEpics");

        // ── Product-scoped stories ──────────────────────────────────
        var stories = app.MapGroup("/api/products/{productId}/stories").WithTags("Stories");
        stories.MapGet("/", async (string productId, ProductDbContext db) =>
        {
            var rows = await db.ProductStories.AsNoTracking()
                .Where(s => s.ProductId == productId)
                .OrderBy(s => s.Sort)
                .ToListAsync();
            return Results.Ok(rows.Select(MapStory).ToList());
        }).WithName("ListProductStories");

        stories.MapPost("/", async (
            HttpContext ctx,
            string productId,
            [FromBody] CreateStoryRequest req,
            ProductDbContext db,
            IHubContext<ForgeHub> hub,
            IForgeDbPaths paths,
            Forge.Api.Services.Stories.IStoryGitOps git,
            ILoggerFactory loggerFactory) =>
        {
            var auth = ForgeUser.RequireWriter(ctx);
            if (auth is not null) return auth;
            if (string.IsNullOrWhiteSpace(req?.Name))
                return Results.Problem(title: "Name is required", statusCode: 400);
            if (!await db.Products.AnyAsync(p => p.Id == productId))
                return Results.Problem(title: $"Product {productId} not found", statusCode: 404);

            var id = req.Id?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                id = req.Name.Trim().ToUpperInvariant()
                    .Replace(' ', '-')
                    .Replace("--", "-");
                if (id.Length > 20) id = id[..20];
                id = $"{productId}:{id}";
            }

            if (await db.ProductStories.AnyAsync(s => s.Id == id))
                return Results.Problem(title: $"Story {id} already exists", statusCode: 409);

            var maxSort = await db.ProductStories
                .Where(s => s.ProductId == productId)
                .Select(s => (int?)s.Sort).MaxAsync() ?? -1;
            var points = req.Points.GetValueOrDefault(0);
            if (points < 0) points = 0;

            // Phase 45 — operator-chosen default base branch. Validated via
            // the same helper used by spawn endpoints so a typo can't reach
            // a `git fetch` command line. Empty / missing → "main".
            var baseBranchInput = req.BaseBranch?.Trim();
            var baseBranch = string.IsNullOrEmpty(baseBranchInput) ? "main" : baseBranchInput!;
            if (!StoriesEndpoints.IsValidBranchName(baseBranch))
            {
                return Results.Problem(
                    title: "Invalid base branch name",
                    detail: $"'{baseBranch}' is not a valid git branch name.",
                    statusCode: 400);
            }

            var row = new ProductStoryRow
            {
                Id = id,
                ProductId = productId,
                Name = req.Name.Trim(),
                Status = req.Status ?? "planning",
                Sort = maxSort + 1,
                CreatedAt = DateTimeOffset.UtcNow,
                Description = (req.Description ?? string.Empty).Trim(),
                Points = points,
                BaseBranch = baseBranch,
                // Phase 46 — auto-merge is honoured for any base, including "main".
                // The operator is responsible for arming the flag deliberately.
                AutoMerge = req.AutoMerge ?? false,
            };

            // Phase 43 — resolve pre-uploaded attachments.
            if (req.UploadIds is { Count: > 0 })
            {
                var attachments = StoriesEndpoints.ResolveUploadedFiles(req.UploadIds, paths);
                if (attachments.Count > 0)
                {
                    row.AttachmentsJson = System.Text.Json.JsonSerializer.Serialize(attachments);
                }
            }

            db.ProductStories.Add(row);
            await db.SaveChangesAsync();

            // Phase 45 — when the operator picks a non-default base at
            // creation time, materialise the branch in every repo of the
            // product so a subsequent spawn doesn't have to wait for first
            // drive to create it. Best-effort — a missing-clone repo gets
            // logged as a warning and reported in the response.
            var branchWarnings = new List<string>();
            if (!string.Equals(row.BaseBranch, "main", StringComparison.Ordinal))
            {
                branchWarnings.AddRange(
                    await EnsureBranchInProductReposAsync(
                        db, productId, row.BaseBranch, git, loggerFactory.CreateLogger("forge.story.basebranch"), ctx.RequestAborted));
            }

            var story = MapStory(row);
            await hub.Clients.All.SendAsync("story.changed", new { kind = "created", story });
            return Results.Created($"/api/products/{productId}/stories/{row.Id}",
                branchWarnings.Count == 0
                    ? (object)story
                    : new { story, branchWarnings });
        }).WithName("CreateProductStory");

        stories.MapPatch("/{storyId}", async (
            HttpContext ctx,
            string productId,
            string storyId,
            [FromBody] PatchStoryRequest req,
            ProductDbContext db,
            IHubContext<ForgeHub> hub,
            IForgeDbPaths paths,
            Forge.Api.Services.Stories.IStoryGitOps git,
            Forge.Api.Services.Stories.IStoryProviderOps storyProvider,
            ILoggerFactory loggerFactory) =>
        {
            var auth = ForgeUser.RequireWriter(ctx);
            if (auth is not null) return auth;

            var row = await db.ProductStories.FirstOrDefaultAsync(s => s.Id == storyId);
            if (row is null || row.ProductId != productId)
                return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);

            if (req.Name is { } name)
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0)
                    return Results.Problem(title: "Name cannot be empty", statusCode: 400);
                row.Name = trimmed;
            }
            if (req.Status is { } status)
            {
                var trimmed = status.Trim();
                if (trimmed.Length == 0)
                    return Results.Problem(title: "Status cannot be empty", statusCode: 400);
                row.Status = trimmed;
            }
            if (req.Description is { } description)
            {
                var trimmedDesc = description.Trim();
                row.Description = trimmedDesc;

                // Propagate to the spawn snapshot so workstream prompts
                // use the updated value (mirrors Points propagation below).
                var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                if (spawn is not null && spawn.StoryDescription != trimmedDesc)
                {
                    spawn.StoryDescription = trimmedDesc;
                    spawn.UpdatedAt = DateTimeOffset.UtcNow;
                }

                // Also update the parent work item so the orchestrator
                // sees the current description when building prompts.
                var parentWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == storyId);
                if (parentWi is not null)
                {
                    parentWi.DescriptionMd = trimmedDesc;
                    parentWi.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            if (req.Points is { } points)
            {
                if (points < 0)
                    return Results.Problem(title: "Points must be >= 0", statusCode: 400);
                row.Points = points;

                // Propagate to the spawn snapshot so BudgetForPhase and
                // scoring thresholds use the updated value.
                var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                if (spawn is not null && spawn.StoryPoints != points)
                {
                    spawn.StoryPoints = points;
                    spawn.UpdatedAt = DateTimeOffset.UtcNow;
                }

                // Cascade to child work items so BudgetForPhase / max-turns
                // pick up the new value on the next dispatch. Without this
                // the spawn snapshot updates but the stage/plan-doc rows
                // stay frozen at their original Points (Phase 38 snapshot)
                // and the agent keeps hitting the old 16m / 90-turn caps.
                // Stage WIs ride the story value 1:1. Plan-doc WIs preserve
                // the Phase 60 floor of max(3, points/4) so the 4-doc fan-out
                // still gets a sensible per-doc allocation.
                var newWsPoints = Math.Max(1, points);
                var newDocPoints = Math.Max(3, points / 4);
                var now = DateTimeOffset.UtcNow;
                await db.WorkItems
                    .Where(w => w.ParentId == storyId
                             && !w.Id.StartsWith("plan-doc:"))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Points, newWsPoints)
                        .SetProperty(w => w.UpdatedAt, now));
                await db.WorkItems
                    .Where(w => w.ParentId == storyId
                             && w.Id.StartsWith("plan-doc:"))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Points, newDocPoints)
                        .SetProperty(w => w.UpdatedAt, now));
            }

            // Phase 45 — operator-chosen default base branch. When the
            // value moves off "main" (or to a different non-default branch),
            // we (a) validate, (b) persist, (c) immediately create the
            // branch in every repo of the product so a future spawn doesn't
            // have to wait for first drive, (d) propagate the change to a
            // pre-existing spawn that was created from this story so its
            // future PRs target the new base.
            string? newBaseBranch = null;
            var branchWarnings = new List<string>();
            if (req.BaseBranch is { } baseBranchInput)
            {
                var trimmed = baseBranchInput.Trim();
                if (string.IsNullOrEmpty(trimmed)) trimmed = "main";
                if (!StoriesEndpoints.IsValidBranchName(trimmed))
                {
                    return Results.Problem(
                        title: "Invalid base branch name",
                        detail: $"'{trimmed}' is not a valid git branch name.",
                        statusCode: 400);
                }

                if (!string.Equals(row.BaseBranch, trimmed, StringComparison.Ordinal))
                {
                    row.BaseBranch = trimmed;
                    newBaseBranch = trimmed;
                }
            }

            // Phase 46 — auto-merge flag. Honoured for any base (including
            // "main"). When the flag changes, propagate to the spawn and
            // all still-open PRs so AutoMergeWatcher picks them up (or stops
            // picking them up).
            if (req.AutoMerge is { } reqAutoMerge)
            {
                var effective = reqAutoMerge;
                if (row.AutoMerge != effective)
                {
                    row.AutoMerge = effective;

                    var spawnAm = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                    if (spawnAm is not null && spawnAm.AutoMerge != effective)
                    {
                        spawnAm.AutoMerge = effective;
                        spawnAm.UpdatedAt = DateTimeOffset.UtcNow;

                        // Update all still-open PRs belonging to this spawn.
                        var spawnPrIds = new List<string>();
                        if (!string.IsNullOrEmpty(spawnAm.PlanPrId)) spawnPrIds.Add(spawnAm.PlanPrId);
                        if (!string.IsNullOrEmpty(spawnAm.QaPrId)) spawnPrIds.Add(spawnAm.QaPrId);
                        var wsRows = await db.Workstreams.Where(w => w.StorySpawnId == spawnAm.Id).ToListAsync();
                        foreach (var ws in wsRows)
                        {
                            var wsPrs = string.IsNullOrEmpty(ws.PrsJson)
                                ? new List<string>()
                                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(ws.PrsJson) ?? new();
                            spawnPrIds.AddRange(wsPrs);
                        }
                        if (spawnPrIds.Count > 0)
                        {
                            var openPrs = await db.Prs.Where(p => spawnPrIds.Contains(p.Id) && p.Col != "merged" && p.Col != "abandoned").ToListAsync();
                            foreach (var pr in openPrs) pr.AutoMerge = effective;
                        }
                    }
                }
            }

            // Phase 43 — append newly uploaded attachments.
            if (req.UploadIds is { Count: > 0 })
            {
                var newFiles = StoriesEndpoints.ResolveUploadedFiles(req.UploadIds, paths);
                if (newFiles.Count > 0)
                {
                    var existing = string.IsNullOrEmpty(row.AttachmentsJson)
                        ? new List<System.Text.Json.JsonElement>()
                        : System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(row.AttachmentsJson) ?? new();
                    var merged = new List<object>();
                    foreach (var e in existing) merged.Add(e);
                    merged.AddRange(newFiles);
                    row.AttachmentsJson = System.Text.Json.JsonSerializer.Serialize(merged);
                }
            }

            // Phase 43 — remove attachments by file name.
            if (req.RemoveFileNames is { Count: > 0 } && !string.IsNullOrEmpty(row.AttachmentsJson))
            {
                var removeSet = new HashSet<string>(req.RemoveFileNames, StringComparer.OrdinalIgnoreCase);
                var current = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(row.AttachmentsJson) ?? new();
                var filtered = current.Where(e =>
                {
                    if (e.TryGetProperty("fileName", out var fn) || e.TryGetProperty("FileName", out fn))
                    {
                        return !removeSet.Contains(fn.GetString() ?? string.Empty);
                    }
                    return true;
                }).ToList();
                row.AttachmentsJson = filtered.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(filtered)
                    : null;
            }

            // Propagate attachments to the spawn snapshot so architect
            // prompts include the latest reference documents.
            // Always sync — covers both fresh uploads and pre-existing
            // attachments that were never propagated to the spawn.
            {
                var spawnForAttach = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                if (spawnForAttach is not null && spawnForAttach.AttachmentsJson != row.AttachmentsJson)
                {
                    spawnForAttach.AttachmentsJson = row.AttachmentsJson;
                    spawnForAttach.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await db.SaveChangesAsync();

            // Phase 45 — branch materialisation + spawn propagation. Done
            // after the SaveChanges so the persisted row is the source of
            // truth even if branch creation fails (the warning is reported
            // back to the caller).
            string[]? replayedBranches = null;
            string[]? retargetedPrs = null;
            string[]? replayFailures = null;
            if (newBaseBranch is not null && !string.Equals(newBaseBranch, "main", StringComparison.Ordinal))
            {
                branchWarnings.AddRange(
                    await EnsureBranchInProductReposAsync(
                        db, productId, newBaseBranch, git, loggerFactory.CreateLogger("forge.story.basebranch"), ctx.RequestAborted));
            }
            if (newBaseBranch is not null)
            {
                // Phase 45 (revision after operator feedback) — when a spawn
                // already exists, run the FULL retarget flow: rebase the new
                // base on top of main, replay merged spawn PRs onto it, and
                // retarget every still-open spawn PR. This matches user
                // requirements #5 + #6 ("base branch change, make sure all
                // existing spawn tasks now create PR against new base").
                var spawnRow = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                if (spawnRow is not null
                    && !string.Equals(spawnRow.BaseBranch, newBaseBranch, StringComparison.Ordinal))
                {
                    var spawnResult = await StoriesEndpoints.ApplySpawnBaseBranchChangeAsync(
                        db, hub, git, storyProvider, spawnRow, newBaseBranch, ctx.RequestAborted);
                    if (spawnResult.PrepareError is not null)
                    {
                        // Prepare failure is non-fatal at the story level —
                        // surface it as a warning so the operator sees what
                        // went wrong but the story PATCH still completes.
                        branchWarnings.Add($"spawn base-branch change failed: {spawnResult.PrepareError}");
                    }
                    else
                    {
                        replayedBranches = spawnResult.ReplayedBranches.ToArray();
                        retargetedPrs = spawnResult.RetargetedPrs.ToArray();
                        replayFailures = spawnResult.ReplayFailures.ToArray();
                    }
                }
            }

            var story = MapStory(row);
            await hub.Clients.All.SendAsync("story.changed", new { kind = "updated", story });
            var hasExtras = branchWarnings.Count > 0
                || (replayedBranches is { Length: > 0 })
                || (retargetedPrs is { Length: > 0 })
                || (replayFailures is { Length: > 0 });
            return Results.Ok(hasExtras
                ? (object)new
                {
                    story,
                    branchWarnings = branchWarnings.Count > 0 ? branchWarnings : null,
                    replayedBranches,
                    retargetedPrs,
                    replayFailures,
                }
                : story);
        }).WithName("UpdateProductStory");

        stories.MapDelete("/{storyId}", async (
            HttpContext ctx,
            string productId,
            string storyId,
            ProductDbContext db,
            IHubContext<ForgeHub> hub) =>
        {
            var auth = ForgeUser.RequireWriter(ctx);
            if (auth is not null) return auth;

            var row = await db.ProductStories.FirstOrDefaultAsync(s => s.Id == storyId);
            if (row is null || row.ProductId != productId)
                return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);

            // Remove related story spawn (if any).
            var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
            if (spawn is not null)
                db.StorySpawns.Remove(spawn);

            db.ProductStories.Remove(row);
            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync("story.changed", new { kind = "deleted", id = storyId });
            return Results.NoContent();
        }).WithName("DeleteProductStory");

        return app;
    }

    public sealed record CreateStoryRequest(
        string Name,
        string? Id = null,
        string? Status = null,
        string? Description = null,
        int? Points = null,
        IReadOnlyList<string>? UploadIds = null,
        /// <summary>Phase 45 — default base branch for spawns produced from this story. Empty / missing = "main".</summary>
        string? BaseBranch = null,
        /// <summary>Phase 46 — auto-merge PRs against the base branch. Only effective when BaseBranch != "main".</summary>
        bool? AutoMerge = null);

    public sealed record PatchStoryRequest(
        string? Name = null,
        string? Status = null,
        string? Description = null,
        int? Points = null,
        IReadOnlyList<string>? UploadIds = null,
        /// <summary>Phase 43 — file names to remove from AttachmentsJson.</summary>
        IReadOnlyList<string>? RemoveFileNames = null,
        /// <summary>Phase 45 — change the story's default base branch. When the new value is non-"main" the server creates the branch in every repo of the product immediately.</summary>
        string? BaseBranch = null,
        /// <summary>Phase 46 — toggle auto-merge. Only effective when BaseBranch != "main".</summary>
        bool? AutoMerge = null);

    private static ProductStory MapStory(ProductStoryRow r) => new(
        r.Id, r.ProductId, r.Name, r.Status, r.Sort, r.CreatedAt.ToString("o"),
        r.Description ?? string.Empty, r.Points,
        string.IsNullOrEmpty(r.AttachmentsJson)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<object[]>(r.AttachmentsJson),
        string.IsNullOrEmpty(r.BaseBranch) ? "main" : r.BaseBranch,
        r.AutoMerge);

    /// <summary>
    /// Phase 45 — create <paramref name="baseBranch"/> branched from
    /// <c>main</c> in every repo of the given product. Idempotent — repos
    /// where the branch already exists are no-op'd. Returns a list of
    /// per-repo warning strings for repos where creation failed (e.g.
    /// missing clone, no auth) so the caller can surface them without
    /// failing the whole request.
    /// </summary>
    private static async Task<List<string>> EnsureBranchInProductReposAsync(
        ProductDbContext db,
        string productId,
        string baseBranch,
        Forge.Api.Services.Stories.IStoryGitOps git,
        ILogger logger,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var repos = await db.Repos.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .ToListAsync(ct);
        if (repos.Count == 0)
        {
            warnings.Add($"No repos attached to product '{productId}'; branch '{baseBranch}' was recorded but not created in any working tree.");
            return warnings;
        }
        foreach (var repo in repos)
        {
            try
            {
                await git.EnsureBranchAsync(repo, baseBranch, "main", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Branch '{Branch}' could not be created in repo {RepoId} ({DisplayName}).",
                    baseBranch, repo.Id, repo.DisplayName);
                warnings.Add($"{repo.DisplayName ?? repo.Id}: {ex.Message}");
            }
        }
        return warnings;
    }

    private static Epic MapEpic(EpicRow r) => new(
        r.Id, r.Name, r.Status, r.Progress,
        Deserialize<List<string>>(r.CrewJson) ?? new(),
        r.Cost, r.Color);

    private static Card MapCard(CardRow r) => new(
        r.Id, r.Epic, r.Col, r.Type, r.Title,
        Deserialize<List<string>>(r.TagsJson) ?? new(),
        r.Pts, r.Priority,
        r.AgentJson is null ? null : Deserialize<Agent>(r.AgentJson),
        r.Cost, r.AutoNext, r.Live, r.Progress, r.Branch,
        r.HumanGate, r.GateReason, r.Blocked, r.BlockedReason, r.DoneAt);

    /// <summary>
    /// Phase 17 follow-up — project a <see cref="Forge.Api.Persistence.WorkItemRow"/>
    /// into the legacy <see cref="Card"/> shape so the Plan &amp; Design board
    /// can render Phase 12 work items when the legacy <c>cards</c> table is
    /// empty for the active product. State maps to column:
    ///   * proposed → backlog
    ///   * active   → build
    ///   * resolved → review
    ///   * closed   → done
    /// </summary>
    private static Card WorkItemToCardProjection(Forge.Api.Persistence.WorkItemRow w)
    {
        var col = w.State switch
        {
            "active" => "build",
            "resolved" => "review",
            "closed" => "done",
            _ => "backlog",
        };
        var type = w.Type switch
        {
            "bug" => "bug",
            "task" or "step" => "task",
            _ => "story",
        };
        Agent? agent = null;
        if (!string.IsNullOrEmpty(w.AssignedToJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(w.AssignedToJson);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = root.TryGetProperty("displayName", out var n) ? n.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                {
                    // Map kind → role for the legacy Agent shape. team / crew
                    // surface as "human" so the prototype's role-color palette
                    // doesn't render them as code-writing agents.
                    var role = kind switch
                    {
                        "agent" => "eng",
                        "human" => "human",
                        _ => "human",
                    };
                    agent = new Agent(id!, role, name ?? id!);
                }
            }
            catch { /* malformed JSON — leave agent null */ }
        }
        var tags = Deserialize<List<string>>(w.TagsJson) ?? new List<string>();
        return new Card(
            w.Id, w.FeatureRef ?? "", col, type, w.Title,
            tags, w.Points, w.Priority, agent,
            Cost: 0,
            AutoNext: null, Live: false, Progress: null, Branch: null,
            HumanGate: false, GateReason: null, Blocked: false, BlockedReason: null, DoneAt: null);
    }

    private static Column MapColumn(ColumnRow r) => new(r.Id, r.Name, r.WipLimit);

    private static Workstream MapWorkstream(WorkstreamRow r) => new(
        r.Id, r.Name, r.Branch, r.Base, r.Epic, r.Status,
        Deserialize<List<string>>(r.WorktreeJson) ?? new(),
        r.Created, r.LastActive,
        Deserialize<List<string>>(r.AgentsJson) ?? new(),
        Deserialize<List<string>>(r.PrsJson) ?? new(),
        Deserialize<List<string>>(r.CardsJson) ?? new(),
        r.Commits, r.Ahead, r.Behind, r.Conflicts, r.Summary,
        r.BarStart, r.BarEnd, r.Planned,
        r.RepoId);

    private static WsEvent MapWsEvent(WsEventRow r) => new(r.Ws, r.Day, r.Kind, r.Label);

    private static Pr MapPr(PrRow r) => new(
        r.Id, r.Col, r.Epic, r.EpicColor, r.Workstream, r.Base, r.Branch, r.Title,
        Deserialize<Agent>(r.AuthorJson) ?? new("", "", ""),
        r.Additions, r.Deletions, r.Files, r.Commits, r.Age,
        r.BuildJson is null ? null : Deserialize<BuildStatus>(r.BuildJson),
        r.ReviewJson is null ? null : Deserialize<ReviewVerdict>(r.ReviewJson),
        r.Conflicts, r.HumanGate, r.GateReason, r.ConflictPaths, r.AutoMerge, r.Blocked,
        r.ImplJson is null ? null : Deserialize<ImplStatus>(r.ImplJson),
        RepoId: r.RepoId);

    private static WsStat MapWsStat(WsStatRow r) => new(r.Label, r.Val, r.Unit, r.Trend, r.TrendVal);

    private static OverviewActivity MapActivity(OverviewActivityRow r) => new(r.Time, r.Who, r.Msg);

    private static OverviewAgent MapAgent(OverviewAgentRow r) => new(r.Id, r.Name, r.Role, r.Status, r.Task);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOpts);
}
