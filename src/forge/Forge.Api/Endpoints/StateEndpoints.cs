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
        app.MapGet("/api/state", async (HttpContext ctx, ForgeDbContext db) =>
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
        epicsGrp.MapGet("/", async (ForgeDbContext db) =>
        {
            var rows = await db.Epics.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            return Results.Ok(rows.Select(MapEpic).ToList());
        }).WithName("ListEpics");

        // ── Product-scoped stories ──────────────────────────────────
        var stories = app.MapGroup("/api/products/{productId}/stories").WithTags("Stories");
        stories.MapGet("/", async (string productId, ForgeDbContext db) =>
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
            ForgeDbContext db,
            IHubContext<ForgeHub> hub) =>
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
            var row = new ProductStoryRow
            {
                Id = id,
                ProductId = productId,
                Name = req.Name.Trim(),
                Status = req.Status ?? "planning",
                Sort = maxSort + 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ProductStories.Add(row);
            await db.SaveChangesAsync();

            var story = MapStory(row);
            await hub.Clients.All.SendAsync("story.changed", new { kind = "created", story });
            return Results.Created($"/api/products/{productId}/stories/{row.Id}", story);
        }).WithName("CreateProductStory");

        return app;
    }

    public sealed record CreateStoryRequest(string Name, string? Id = null, string? Status = null);

    private static ProductStory MapStory(ProductStoryRow r) => new(
        r.Id, r.ProductId, r.Name, r.Status, r.Sort, r.CreatedAt.ToString("o"));

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
        r.ImplJson is null ? null : Deserialize<ImplStatus>(r.ImplJson));

    private static WsStat MapWsStat(WsStatRow r) => new(r.Label, r.Val, r.Unit, r.Trend, r.TrendVal);

    private static OverviewActivity MapActivity(OverviewActivityRow r) => new(r.Time, r.Who, r.Msg);

    private static OverviewAgent MapAgent(OverviewAgentRow r) => new(r.Id, r.Name, r.Role, r.Status, r.Task);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOpts);
}
