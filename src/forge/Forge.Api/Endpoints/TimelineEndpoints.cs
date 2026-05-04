// -----------------------------------------------------------------------
// <copyright file="TimelineEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 2.10 — GET /api/timeline?from=…&amp;to=… returns normalized bars +
/// markers for the requested day-window. Days are RELATIVE: 0 == today,
/// negative == past, positive == future. Default window matches the
/// prototype: from=-10, to=5.
///
/// Bars are computed from real activity timestamps (agent_usage,
/// deliverables, story_spawns) rather than the static BarStart/BarEnd
/// columns. Markers are generated from deliverable events (pass, reject,
/// stalled) and PR activity.
/// </summary>
public static class TimelineEndpoints
{
    private const double DefaultFrom = -10;
    private const double DefaultTo = 5;

    private static readonly string[] EpicColors =
    {
        "epic024", "epic021", "epic026", "epic022", "epic018",
        "epic023", "epic025", "epic027", "epic020", "epic019",
    };

    public static WebApplication MapTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/timeline", async (
            HttpContext ctx,
            ForgeDbContext db,
            ILoggerFactory loggerFactory,
            double? from,
            double? to) =>
        {
            var logger = loggerFactory.CreateLogger("forge.timeline");
            var fromDay = from ?? DefaultFrom;
            var toDay = to ?? DefaultTo;
            if (toDay <= fromDay)
            {
                return Results.Problem(title: "to must be > from", statusCode: 400);
            }

            var nowUtc = DateTimeOffset.UtcNow;

            // Phase 8.9 — scope to active product.
            var scope = await ProductScope.ResolveAsync(ctx, db);
            var wsQ = db.Workstreams.AsNoTracking().AsQueryable();
            if (!scope.IsAll)
            {
                var ids = scope.RepoIds!;
                wsQ = wsQ.Where(w => w.RepoId != null && ids.Contains(w.RepoId));
            }
            var workstreams = await wsQ.OrderBy(w => w.Sort).ToListAsync();

            // Fall back to the static Epics table first; if empty, derive
            // epic names from story spawn titles so the timeline still groups.
            var epics = await db.Epics.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            var epicColor = epics.ToDictionary(e => e.Id, e => e.Color);

            // When no epic rows exist, derive from story spawns
            if (epicColor.Count == 0)
            {
                var spawns = await db.StorySpawns.AsNoTracking().ToListAsync();
                var colorIdx = 0;
                foreach (var s in spawns)
                {
                    epicColor[s.StoryId] = EpicColors[colorIdx % EpicColors.Length];
                    colorIdx++;
                }
            }

            // Load real activity timestamps for computing bar positions:
            // agent_usage grouped by TaskId gives us first/last activity per
            // workstream. Deliverables give us milestone events.
            var usageByTask = (await db.AgentUsage.AsNoTracking()
                    .Select(u => new { u.TaskId, u.StartedAt, u.EndedAt })
                    .ToListAsync())
                .Where(u => !string.IsNullOrEmpty(u.TaskId))
                .GroupBy(u => u.TaskId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (
                        First: g.Min(u => u.StartedAt),
                        Last: g.Max(u => u.EndedAt ?? u.StartedAt)),
                    StringComparer.OrdinalIgnoreCase);

            var deliverables = (await db.Deliverables.AsNoTracking()
                    .Select(d => new { d.WorkItemId, d.Verdict, d.Score, d.CreatedAt, d.GeneratorAgentId })
                    .ToListAsync())
                .OrderBy(d => d.CreatedAt)
                .ToList();

            var deliverablesByWi = deliverables
                .GroupBy(d => d.WorkItemId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Story spawn creation dates for fallback bar start
            var spawnDates = (await db.StorySpawns.AsNoTracking()
                    .Select(s => new { s.Id, s.CreatedAt })
                    .ToListAsync())
                .ToDictionary(s => s.Id, s => s.CreatedAt, StringComparer.OrdinalIgnoreCase);

            var bars = new List<TimelineBar>(workstreams.Count);
            var markers = new List<TimelineMarker>();

            foreach (var w in workstreams)
            {
                // Compute bar start/end from real timestamps.
                // Priority: agent_usage timestamps for this workstream's ID,
                // then deliverables, then story spawn creation date, then today.
                DateTimeOffset? earliest = null;
                DateTimeOffset? latest = null;

                // Check agent_usage for tasks matching this workstream ID
                if (usageByTask.TryGetValue(w.Id, out var usage))
                {
                    earliest = usage.First;
                    latest = usage.Last;
                }

                // Check deliverables linked to this workstream
                if (deliverablesByWi.TryGetValue(w.Id, out var wsDels))
                {
                    var delFirst = wsDels.First().CreatedAt;
                    var delLast = wsDels.Last().CreatedAt;
                    earliest = earliest is null ? delFirst : (delFirst < earliest ? delFirst : earliest);
                    latest = latest is null ? delLast : (delLast > latest ? delLast : latest);
                }

                // Fallback to story spawn date
                if (earliest is null && !string.IsNullOrEmpty(w.StorySpawnId)
                    && spawnDates.TryGetValue(w.StorySpawnId, out var spawnDate))
                {
                    earliest = spawnDate;
                }

                // Final fallback: use static BarStart/BarEnd if no activity
                double barStart, barEnd;
                if (earliest.HasValue)
                {
                    barStart = (earliest.Value - nowUtc).TotalDays;
                    barEnd = latest.HasValue ? (latest.Value - nowUtc).TotalDays : 0;
                    // Active workstreams extend to at least today
                    if (w.Status is "active" or "review" or "merging" && barEnd < 0)
                        barEnd = 0;
                    // Planning workstreams extend into the future
                    if (w.Status is "planning" && barEnd < 2)
                        barEnd = 2;
                }
                else
                {
                    barStart = w.BarStart;
                    barEnd = w.BarEnd;
                }

                barStart = Math.Round(barStart, 1);
                barEnd = Math.Round(barEnd, 1);

                // Skip workstreams that fall entirely outside the window.
                if (barEnd < fromDay || barStart > toDay) continue;

                // Compute lastActive as relative days
                var lastActiveDay = latest.HasValue
                    ? Math.Round((latest.Value - nowUtc).TotalDays, 1)
                    : w.LastActive;

                bars.Add(new TimelineBar(
                    WorkstreamId: w.Id,
                    Name: w.Name,
                    Branch: w.Branch,
                    Epic: w.Epic,
                    EpicColor: epicColor.GetValueOrDefault(w.Epic, "epic024"),
                    Status: w.Status,
                    BarStart: barStart,
                    BarEnd: barEnd,
                    Planned: w.Planned,
                    Summary: w.Summary,
                    Agents: CardMapper.Deserialize<List<string>>(w.AgentsJson) ?? new(),
                    Ahead: w.Ahead,
                    Behind: w.Behind,
                    Conflicts: w.Conflicts,
                    LastActive: lastActiveDay));

                // Generate markers from deliverables linked to this workstream
                if (deliverablesByWi.TryGetValue(w.Id, out var dels))
                {
                    foreach (var d in dels)
                    {
                        var day = Math.Round((d.CreatedAt - nowUtc).TotalDays, 1);
                        if (day < fromDay || day > toDay) continue;

                        var kind = d.Verdict?.ToLowerInvariant() switch
                        {
                            "pass" => "merge",
                            "reject" => "block",
                            "stalled" or "stalled-budget" => "block",
                            _ => "review",
                        };
                        var label = $"{d.Verdict} (score {d.Score}) by {d.GeneratorAgentId}";
                        markers.Add(new TimelineMarker(w.Id, day, kind, label));
                    }
                }
            }

            // Also generate markers from existing ws_events table (if any)
            var allEvents = await db.WsEvents.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            foreach (var ev in allEvents)
            {
                if (ev.Day < fromDay || ev.Day > toDay) continue;
                markers.Add(new TimelineMarker(ev.Ws, ev.Day, ev.Kind, ev.Label));
            }

            logger.LogInformation(
                "forge.timeline.requested from={From} to={To} bars={Bars} markers={Markers}",
                fromDay, toDay, bars.Count, markers.Count);

            return Results.Ok(new TimelineResponse(fromDay, toDay, bars, markers));
        })
        .WithTags("Timeline")
        .WithName("GetTimeline");

        return app;
    }
}
