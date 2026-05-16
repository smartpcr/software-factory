// -----------------------------------------------------------------------
// <copyright file="PayrollEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.8 — HR payroll endpoint plus the <c>/spend</c> deprecation
/// alias for the existing token-spend rollup (Q14.7).
/// <list type="bullet">
///   <item><c>GET /api/crews/{id}/payroll</c> — new HR salary surface.</item>
///   <item><c>GET /api/crews/{id}/spend</c> — alias of the existing
///         <c>GET /api/crews/{id}/salary</c> handler. Logs
///         <c>forge.api.deprecated</c> on hits to the old path; the new
///         <c>/spend</c> path is the canonical one going forward.</item>
/// </list>
/// </summary>
public static class PayrollEndpoints
{
    public static WebApplication MapPayrollEndpoints(this WebApplication app)
    {
        app.MapGet("/api/crews/{id}/payroll", GetPayroll)
            .WithTags("Payroll")
            .WithName("GetCrewPayroll");

        // Phase 14.11 — TopBar payroll chip aggregator. Single SUM across
        // every crew so the chip's polling cost is O(1) requests, not O(N).
        // The route's static `payroll/today` segment wins over the wildcard
        // `{id}/payroll` so there's no conflict with GetPayroll.
        app.MapGet("/api/crews/payroll/today", GetPayrollTodayTotal)
            .WithTags("Payroll")
            .WithName("GetPayrollTodayTotal");

        // Q14.7 — `/spend` is the new canonical name for the token-spend
        // rollup. We don't reach into the existing salary handler here —
        // we replicate the payload from `agent_usage` so the alias stays
        // wholly within Phase 14 code. The legacy `/salary` route is kept
        // 200-OK in CrewsEndpoints; both eventually land in the same place.
        app.MapGet("/api/crews/{id}/spend", GetSpend)
            .WithTags("Payroll")
            .WithName("GetCrewSpend");

        return app;
    }

    private static async Task<IResult> GetPayrollTodayTotal(ControlDbContext db, CancellationToken ct)
    {
        var crews = await db.Crews.AsNoTracking().ToListAsync(ct);
        if (crews.Count == 0) return Results.Ok(new PayrollTodaySummary(0m, 0));

        // Pull every (crew, lead+roster) membership in two queries instead
        // of N. Build per-crew member id sets, then resolve every member
        // agent in a single round-trip and group by crew.
        var crewIds = crews.Select(c => c.Id).ToList();
        var memberRows = await db.CrewMembers.AsNoTracking()
            .Where(m => crewIds.Contains(m.CrewId))
            .Select(m => new { m.CrewId, m.AgentId })
            .ToListAsync(ct);

        var byCrew = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in crews)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(c.Lead)) set.Add(c.Lead);
            byCrew[c.Id] = set;
        }
        foreach (var r in memberRows)
        {
            if (byCrew.TryGetValue(r.CrewId, out var set)) set.Add(r.AgentId);
        }

        var allMemberIds = byCrew.Values.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var agents = allMemberIds.Count == 0
            ? new List<AgentRow>()
            : await db.Agents.AsNoTracking()
                .Where(a => allMemberIds.Contains(a.Id))
                .ToListAsync(ct);
        var agentById = agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var nowUtc = DateTimeOffset.UtcNow;
        decimal total = 0m;
        var counted = 0;
        foreach (var crew in crews)
        {
            // Today is local-day-aligned per crew TZ so the result matches
            // the per-crew /payroll endpoint exactly when summed.
            var tz = ScheduleGate.ResolveTimeZone(crew.Timezone);
            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
            var todayLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
            var todayUtcStart = todayLocal.ToUniversalTime();

            var members = byCrew[crew.Id]
                .Select(id => agentById.TryGetValue(id, out var a) ? a : null)
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList();

            total += PayrollCalculator.ComputeTotal(crew, members, todayUtcStart, nowUtc);
            counted++;
        }

        return Results.Ok(new PayrollTodaySummary(Math.Round(total, 6), counted));
    }

    private static async Task<IResult> GetPayroll(
        string id,
        ControlDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        // Resolve members: include the lead + all roster rows.
        var memberIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(crew.Lead)) memberIds.Add(crew.Lead);
        foreach (var rosterId in await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == id)
            .Select(m => m.AgentId)
            .ToListAsync(ct))
        {
            memberIds.Add(rosterId);
        }
        var members = memberIds.Count == 0
            ? new List<AgentRow>()
            : await db.Agents.AsNoTracking()
                .Where(a => memberIds.Contains(a.Id))
                .ToListAsync(ct);

        // Period boundaries. Today = local 00:00 → UtcNow; Sprint = trailing
        // 14d; Month = trailing 30d. Sparkline = 14 daily totals oldest first.
        var nowUtc = DateTimeOffset.UtcNow;
        var tz = ScheduleGate.ResolveTimeZone(crew.Timezone);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var todayLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var todayUtcStart = todayLocal.ToUniversalTime();

        var today = PayrollCalculator.ComputeTotal(crew, members, todayUtcStart, nowUtc);
        var sprint = PayrollCalculator.ComputeTotal(crew, members, nowUtc.AddDays(-14), nowUtc);
        var month  = PayrollCalculator.ComputeTotal(crew, members, nowUtc.AddDays(-30), nowUtc);

        // Sparkline — one bucket per local day for the last 14 days.
        var sparkline = new decimal[14];
        for (var i = 0; i < 14; i++)
        {
            // Bucket i covers local-day (today - (13 - i)).
            var dayLocal = todayLocal.AddDays(i - 13);
            var dayStartUtc = dayLocal.ToUniversalTime();
            var dayEndUtc = dayLocal.AddDays(1).ToUniversalTime();
            // Clip the last bucket (today) at "now" so we don't count
            // hours that haven't been worked yet.
            var clipEnd = dayEndUtc > nowUtc ? nowUtc : dayEndUtc;
            sparkline[i] = PayrollCalculator.ComputeTotal(crew, members, dayStartUtc, clipEnd);
        }

        return Results.Ok(new CrewPayroll(crew.Id, today, sprint, month, sparkline));
    }

    /// <summary>
    /// Phase 14.8 — token-spend rollup under the new <c>/spend</c> name.
    /// Mirrors the existing salary payload shape (today / sprint / month /
    /// 14-day sparkline) but sourced directly from <c>agent_usage</c>.
    /// </summary>
    private static async Task<IResult> GetSpend(
        string id,
        ControlDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        var nowUtc = DateTimeOffset.UtcNow;
        var tz = ScheduleGate.ResolveTimeZone(crew.Timezone);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var todayStartLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var todayStartUtc = todayStartLocal.ToUniversalTime();
        var sprintStart = nowUtc.AddDays(-14);
        var monthStart  = nowUtc.AddDays(-30);

        // SQLite can't filter DateTimeOffset at the SQL layer; pull the
        // crew's full set then bucket in memory. Volume per crew is
        // bounded — usage stays small enough that the in-memory walk is
        // cheaper than the round-trip cost of multiple queries.
        var allRows = await db.AgentUsage.AsNoTracking()
            .Where(u => u.CrewId == id)
            .Select(u => new { u.StartedAt, u.CostUsd })
            .ToListAsync(ct);
        var rows = allRows.Where(r => r.StartedAt >= monthStart).ToList();

        var today  = rows.Where(r => r.StartedAt >= todayStartUtc).Sum(r => r.CostUsd);
        var sprint = rows.Where(r => r.StartedAt >= sprintStart).Sum(r => r.CostUsd);
        var month  = rows.Sum(r => r.CostUsd);

        var sparkline = new decimal[14];
        for (var i = 0; i < 14; i++)
        {
            var dayLocal = todayStartLocal.AddDays(i - 13);
            var dayStartUtc = dayLocal.ToUniversalTime();
            var dayEndUtc = dayLocal.AddDays(1).ToUniversalTime();
            var clipEnd = dayEndUtc > nowUtc ? nowUtc : dayEndUtc;
            sparkline[i] = rows
                .Where(r => r.StartedAt >= dayStartUtc && r.StartedAt < clipEnd)
                .Sum(r => r.CostUsd);
        }

        // Reuse the existing CrewSalary record shape so callers can swap the
        // path without touching their type bindings (Q14.7: same shape, new
        // semantics — even though *this* path is the spend semantic, the
        // shape was already correct for spend).
        return Results.Ok(new
        {
            crewId = crew.Id,
            today,
            sprint,
            month,
            sparkline,
        });
    }
}
