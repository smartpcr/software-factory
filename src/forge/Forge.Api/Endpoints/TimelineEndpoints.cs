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
/// </summary>
public static class TimelineEndpoints
{
    private const double DefaultFrom = -10;
    private const double DefaultTo = 5;

    public static WebApplication MapTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/timeline", async (
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

            var workstreams = await db.Workstreams.AsNoTracking().OrderBy(w => w.Sort).ToListAsync();
            var epics = await db.Epics.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();
            var allEvents = await db.WsEvents.AsNoTracking().OrderBy(e => e.Sort).ToListAsync();

            var epicColor = epics.ToDictionary(e => e.Id, e => e.Color);

            var bars = new List<TimelineBar>(workstreams.Count);
            foreach (var w in workstreams)
            {
                // Skip workstreams that fall entirely outside the window.
                if (w.BarEnd < fromDay || w.BarStart > toDay) continue;

                bars.Add(new TimelineBar(
                    WorkstreamId: w.Id,
                    Name: w.Name,
                    Branch: w.Branch,
                    Epic: w.Epic,
                    EpicColor: epicColor.GetValueOrDefault(w.Epic, w.Epic),
                    Status: w.Status,
                    BarStart: w.BarStart,
                    BarEnd: w.BarEnd,
                    Planned: w.Planned,
                    Summary: w.Summary,
                    Agents: CardMapper.Deserialize<List<string>>(w.AgentsJson) ?? new(),
                    Ahead: w.Ahead,
                    Behind: w.Behind,
                    Conflicts: w.Conflicts,
                    LastActive: w.LastActive));
            }

            var markers = new List<TimelineMarker>();
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
