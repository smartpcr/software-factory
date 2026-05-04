// -----------------------------------------------------------------------
// <copyright file="CostEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// <c>GET /api/cost/summary</c> — single round-trip rollup that feeds the
/// <c>/cost</c> dashboard. Aggregates the last 7 days of
/// <see cref="AgentUsageRow"/> by day × epic × agent so the UI doesn't have
/// to make four separate calls (header stats, daily-stack, per-agent burn,
/// today-vs-cap gauge). Most-expensive cards stay sourced from the
/// existing <c>/api/state</c> payload; alerts remain client-side text for
/// now (no signal yet on the server).
/// </summary>
public static class CostEndpoints
{
    private const decimal FallbackDailyCapUsd = 7.5m;

    private static readonly string[] DayLabels =
    {
        "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat",
    };

    public static WebApplication MapCostEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cost/summary", GetSummary)
            .WithTags("Cost")
            .WithName("GetCostSummary");
        return app;
    }

    private static async Task<IResult> GetSummary(int? days, ForgeDbContext db, CancellationToken ct)
    {
        var window = Math.Clamp(days ?? 7, 1, 30);

        // Day boundaries pinned to UTC midnight so the daily buckets line
        // up across timezones; matches the existing salary endpoint's
        // approach (CrewsEndpoints.GetSalary).
        var nowUtc = DateTimeOffset.UtcNow;
        var startOfToday = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var windowStart = startOfToday.AddDays(-(window - 1));
        var twentyFourAgo = nowUtc.AddDays(-1);

        // Pull only what we need from the ledger. SQLite EF can't filter
        // DateTimeOffset at the SQL layer reliably (and CostUsd uses a
        // decimal→double converter that won't push through SUM), so we
        // pull the columns and bucket in memory. Window of 7 days × all
        // crews stays small enough that this is cheap.
        var rows = await db.AgentUsage.AsNoTracking()
            .Select(u => new { u.AgentId, u.TaskId, u.InputTokens, u.OutputTokens, u.CostUsd, u.StartedAt })
            .ToListAsync(ct);
        rows = rows.Where(r => r.StartedAt >= windowStart).ToList();

        // Lookup tables. Cards keyed by id give us the epic; agents keyed
        // by id give us name + role for the burn table.
        var cards = await db.Cards.AsNoTracking()
            .Select(c => new { c.Id, c.Epic })
            .ToListAsync(ct);
        var cardEpic = cards
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Epic, StringComparer.OrdinalIgnoreCase);

        var agents = await db.Agents.AsNoTracking()
            .Select(a => new { a.Id, a.Name, a.Role })
            .ToListAsync(ct);
        var agentInfo = agents.ToDictionary(
            a => a.Id,
            a => (a.Name, a.Role),
            StringComparer.OrdinalIgnoreCase);

        // Daily cap = sum of every crew's DailyBudgetUsd cap. If no crew
        // has a cap configured, fall back to a sensible constant so the
        // gauge still renders something defensible.
        var caps = await db.Crews.AsNoTracking()
            .Where(c => c.DailyBudgetUsd != null)
            .Select(c => c.DailyBudgetUsd!.Value)
            .ToListAsync(ct);
        var dailyCap = caps.Count > 0 ? caps.Sum() : FallbackDailyCapUsd;

        // Day buckets — index 0 is the oldest, index (window-1) is today.
        var dayTotals = new decimal[window];
        var dayByEpic = new Dictionary<string, decimal>[window];
        for (var i = 0; i < window; i++)
        {
            dayByEpic[i] = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var perAgent = new Dictionary<string, AgentAccumulator>(StringComparer.OrdinalIgnoreCase);
        var epicTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            var dayIdx = (int)(r.StartedAt.UtcDateTime.Date - windowStart.UtcDateTime.Date).TotalDays;
            if (dayIdx < 0 || dayIdx >= window) continue;

            var epic = !string.IsNullOrEmpty(r.TaskId) && cardEpic.TryGetValue(r.TaskId, out var e)
                ? e
                : "unassigned";

            dayTotals[dayIdx] += r.CostUsd;
            dayByEpic[dayIdx][epic] = dayByEpic[dayIdx].GetValueOrDefault(epic) + r.CostUsd;
            epicTotals[epic] = epicTotals.GetValueOrDefault(epic) + r.CostUsd;

            if (!perAgent.TryGetValue(r.AgentId, out var acc))
            {
                acc = new AgentAccumulator(window);
                perAgent[r.AgentId] = acc;
            }
            acc.Cost7d += r.CostUsd;
            if (r.StartedAt >= twentyFourAgo) acc.Cost24h += r.CostUsd;
            acc.Tokens += r.InputTokens + r.OutputTokens;
            acc.Spark[dayIdx] += r.CostUsd;
        }

        // Materialise the response. Days flow oldest → today; agents are
        // sorted by 7d cost descending so the highest-burn agents show
        // first in the UI table.
        var dayPoints = new List<CostDailyPoint>(window);
        for (var i = 0; i < window; i++)
        {
            var date = windowStart.UtcDateTime.Date.AddDays(i);
            dayPoints.Add(new CostDailyPoint(
                Date: date.ToString("yyyy-MM-dd"),
                Label: DayLabels[(int)date.DayOfWeek],
                TotalUsd: Round(dayTotals[i]),
                ByEpic: dayByEpic[i].ToDictionary(kv => kv.Key, kv => Round(kv.Value), StringComparer.OrdinalIgnoreCase)));
        }

        var epicList = epicTotals
            .Select(kv => new CostByEpic(kv.Key, Round(kv.Value)))
            .OrderByDescending(e => e.Total7dUsd)
            .ToList();

        var agentList = perAgent
            .Select(kv =>
            {
                var (name, role) = agentInfo.TryGetValue(kv.Key, out var info)
                    ? info
                    : (kv.Key, string.Empty);
                return new AgentBurn(
                    Id: kv.Key,
                    Name: name,
                    Role: role,
                    Cost24hUsd: Round(kv.Value.Cost24h),
                    Cost7dUsd: Round(kv.Value.Cost7d),
                    Tokens: kv.Value.Tokens,
                    Spark: kv.Value.Spark.Select(Round).ToList());
            })
            .OrderByDescending(a => a.Cost7dUsd)
            .ToList();

        var todayUsd = Round(dayTotals[window - 1]);
        var weekUsd = Round(dayTotals.Sum());

        return Results.Ok(new CostSummary(
            TodayUsd: todayUsd,
            Week7dUsd: weekUsd,
            DailyCapUsd: Round(dailyCap),
            TodayIndex: window - 1,
            Days: dayPoints,
            Epics: epicList,
            Agents: agentList));
    }

    private static decimal Round(decimal v) => Math.Round(v, 6);

    private sealed class AgentAccumulator
    {
        public AgentAccumulator(int window)
        {
            this.Spark = new decimal[window];
        }

        public decimal Cost24h { get; set; }

        public decimal Cost7d { get; set; }

        public long Tokens { get; set; }

        public decimal[] Spark { get; }
    }
}
