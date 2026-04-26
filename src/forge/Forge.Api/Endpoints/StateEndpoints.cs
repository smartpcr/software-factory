// -----------------------------------------------------------------------
// <copyright file="StateEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
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
            var columns = await db.Columns.AsNoTracking().OrderBy(c => c.Sort).ToListAsync();
            var workstreams = await wsQ.OrderBy(w => w.Sort).ToListAsync();
            var wsEvents = await db.WsEvents.AsNoTracking().OrderBy(w => w.Sort).ToListAsync();
            var prs = await prsQ.OrderBy(p => p.Sort).ToListAsync();
            var wsStats = await db.WsStats.AsNoTracking().OrderBy(s => s.Sort).ToListAsync();
            var activity = await db.OverviewActivity.AsNoTracking().OrderBy(a => a.Sort).ToListAsync();
            var agents = await db.OverviewAgents.AsNoTracking().OrderBy(a => a.Sort).ToListAsync();

            var payload = new ForgeState(
                Epics: epics.Select(MapEpic).ToList(),
                Cards: cards.Select(MapCard).ToList(),
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

        return app;
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
