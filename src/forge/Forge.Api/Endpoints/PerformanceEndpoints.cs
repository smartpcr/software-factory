// -----------------------------------------------------------------------
// <copyright file="PerformanceEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// <c>GET /api/performance/scorecards</c> — single round-trip payload for the
/// Performance view. Computes per-agent scorecards, promotion decisions, and
/// monthly snapshots from <see cref="DeliverableRow"/>,
/// <see cref="AgentUsageRow"/>, <see cref="AgentRow"/>, and
/// <see cref="CrewRow"/> tables. KPI math follows the final-iteration
/// semantics defined in <see cref="PromotionRunner"/> and
/// <see cref="PromotionPolicy"/>.
/// </summary>
public static class PerformanceEndpoints
{
    private static readonly string[] TeamOrder = { "Architect", "Engineer", "Review", "QA" };

    private static readonly Dictionary<int, string> LevelTitles = new()
    {
        [0] = "Trainee",
        [1] = "Engineer I",
        [2] = "Engineer II",
        [3] = "Engineer III",
        [4] = "Senior I",
        [5] = "Senior II",
        [6] = "Senior III",
        [7] = "Staff",
        [8] = "Principal",
        [9] = "Distinguished",
        [10] = "Fellow",
    };

    public static WebApplication MapPerformanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/performance/scorecards", GetScorecards)
            .WithTags("Performance")
            .WithName("GetPerformanceScorecards");
        return app;
    }

    private static async Task<IResult> GetScorecards(string? window, ControlDbContext control, ProductDbContext db, ProductFanout fanout, IForgeDbPaths dbPaths, CancellationToken ct)
    {
        var windowKey = window ?? "trailing20";
        var nowUtc = DateTimeOffset.UtcNow;

        // ── Load all reference data (small tables) ─────────────────────
        var agents = await control.Agents.AsNoTracking().ToListAsync(ct);
        var crews = await control.Crews.AsNoTracking().ToListAsync(ct);
        var crewMap = crews.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        // ── Load all deliverables + work-item lookups across products ──
        // Phase 73 split — when the split marker is present, deliverables
        // and work items live in per-product DBs. The injected ProductDbContext
        // points at a single product (the one referenced by the request's
        // X-Forge-Product-Id header) or the empty control DB, so aggregate
        // across products via ProductFanout. Without this, agents that
        // worked across multiple products are under-counted and the
        // promotion ladder reports `insufficient-data` for everyone.
        var splitApplied = SplitMarkerCache.IsPhase73Applied(dbPaths);
        var allDeliverables = new List<DeliverableRow>();
        var wiRepo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (splitApplied)
        {
            var sync = new object();
            await fanout.ForEachProductAsync(
                async (control2, productDb, slug, c) =>
                {
                    var rows = await productDb.Deliverables.AsNoTracking().ToListAsync(c);
                    var wi = await productDb.WorkItems.AsNoTracking()
                        .Select(w => new { w.Id, w.RepoId })
                        .ToListAsync(c);
                    lock (sync)
                    {
                        allDeliverables.AddRange(rows);
                        foreach (var w in wi)
                        {
                            if (!string.IsNullOrEmpty(w.RepoId))
                            {
                                wiRepo[w.Id] = w.RepoId!;
                            }
                        }
                    }
                },
                ct);
        }
        else
        {
            allDeliverables.AddRange(await db.Deliverables.AsNoTracking().ToListAsync(ct));
            var wi = await db.WorkItems.AsNoTracking()
                .Select(w => new { w.Id, w.RepoId })
                .ToListAsync(ct);
            foreach (var w in wi)
            {
                if (!string.IsNullOrEmpty(w.RepoId))
                {
                    wiRepo[w.Id] = w.RepoId!;
                }
            }
        }

        allDeliverables = allDeliverables
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        // Following PromotionRunner.BuildTrailingWindowAsync: group by
        // WorkItemId, keep the row with the latest CreatedAt (= final iter).
        var finalDeliverables = allDeliverables
            .GroupBy(d => d.WorkItemId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .OrderByDescending(d => d.CreatedAt)
            .ToList();

        // Phase 80.B — cross-product repo→product mapping via the registry.
        var repos = await control.RepoRegistry.AsNoTracking()
            .Select(r => new { r.Id, r.ProductId })
            .ToListAsync(ct);
        var repoProduct = repos
            .Where(r => !string.IsNullOrEmpty(r.ProductId))
            .ToDictionary(r => r.Id, r => r.ProductId!, StringComparer.OrdinalIgnoreCase);
        var products = await control.Products.AsNoTracking()
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        var productNames = products.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        // ── Load cost aggregates per agent ─────────────────────────────
        var usageRows = await control.AgentUsage.AsNoTracking()
            .Select(u => new { u.AgentId, u.CostUsd, u.StartedAt })
            .ToListAsync(ct);

        var usageSlims = usageRows.Select(u => new UsageSlim(u.StartedAt, u.CostUsd)).ToList();

        var costByAgent = usageRows
            .GroupBy(u => u.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(u => (double)u.CostUsd),
                StringComparer.OrdinalIgnoreCase);

        // ── Load promotion history ─────────────────────────────────────
        var promoHistory = (await control.AgentLevelHistory.AsNoTracking().ToListAsync(ct))
            .GroupBy(h => h.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // ── Build per-agent scorecards ─────────────────────────────────
        var scorecards = new List<object>();
        var promotionDecisions = new List<object>();

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();

            var team = RoleToTeam(agent.Role);
            var crewName = crewMap.TryGetValue(agent.CrewId ?? "", out var crew)
                ? crew.Name
                : "Unassigned";
            var title = LevelTitles.GetValueOrDefault(agent.Level, $"L{agent.Level}");

            // Build trailing window exactly like PromotionRunner does:
            // final-iter rows where agent is generator OR evaluator.
            var agentFinals = finalDeliverables
                .Where(d => string.Equals(d.GeneratorAgentId, agent.Id, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(d.EvaluatorAgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Apply window filter
            var windowRows = ApplyWindowFilter(agentFinals, windowKey, nowUtc);

            // Also get ALL deliverable rows (not just finals) for iteration
            // percentile stats
            var allAgentRows = allDeliverables
                .Where(d => string.Equals(d.GeneratorAgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Compute KPIs
            var throughput = windowRows.Count;
            var passCount = windowRows.Count(d =>
                string.Equals(d.Verdict, "pass", StringComparison.OrdinalIgnoreCase));
            var rejectCount = windowRows.Count(d =>
                string.Equals(d.Verdict, "reject", StringComparison.OrdinalIgnoreCase));
            var stalledCount = windowRows.Count(d =>
                d.Verdict != null && d.Verdict.StartsWith("stalled", StringComparison.OrdinalIgnoreCase));
            var disputeCount = windowRows.Count(d => d.JudgeAgentId != null);

            var passRate = throughput > 0 ? (double)passCount / throughput : 0;
            var avgScore = throughput > 0 ? windowRows.Average(d => (double)d.Score) : 0;
            var rejectRate = throughput > 0 ? (double)rejectCount / throughput : 0;
            var stalledRate = throughput > 0 ? (double)stalledCount / throughput : 0;
            var disputeRate = throughput > 0 ? (double)disputeCount / throughput : 0;

            // Iteration percentiles — from all agent rows (not just finals),
            // looking at the max iter per work item
            var itersPerWorkItem = allAgentRows
                .GroupBy(d => d.WorkItemId)
                .Select(g => g.Max(x => x.Iter))
                .OrderBy(x => x)
                .ToList();
            var iterP50 = Percentile(itersPerWorkItem, 0.5);
            var iterP90 = Percentile(itersPerWorkItem, 0.9);

            // Cost per pass
            var totalGenCost = windowRows.Sum(d => (double)d.GenCostUsd);
            var totalEvalCost = windowRows.Sum(d => (double)d.EvalCostUsd);
            var totalDeliverableCost = totalGenCost + totalEvalCost;
            var costPerPass = passCount > 0 ? totalDeliverableCost / passCount : 0;

            // Utilization — approximate from agent_usage
            var agentCost = costByAgent.GetValueOrDefault(agent.Id);
            var utilization = agentCost > 0 ? Math.Min(1.0, agentCost / 500.0) : 0;

            // Score sparkline — trailing 20 final scores, oldest first
            var scoreSpark = agentFinals
                .Take(20)
                .Select(d => d.Score)
                .Reverse()
                .ToList();

            // Rubric dims — average from DimsJson. Default to score-proportional
            // when DimsJson is empty.
            var dims = ComputeAverageDims(windowRows, avgScore);

            // Monthly averages (last 6 months)
            var (monthlyAvg, monthlyMonths) = ComputeMonthlyAvg(agentFinals, nowUtc);

            // Promotion decision via the real policy
            var lastPromoAt = promoHistory.TryGetValue(agent.Id, out var hist)
                ? hist
                    .Where(h => h.Reason == PromotionPolicy.ReasonPromote)
                    .OrderByDescending(h => h.CreatedAt)
                    .Select(h => (DateTimeOffset?)h.CreatedAt)
                    .FirstOrDefault()
                : null;

            var policyWindow = agentFinals.Take(ScoringConstants.RollingWindow).ToList();
            var decision = PromotionPolicy.Evaluate(agent.Level, lastPromoAt, policyWindow, nowUtc);

            var verdict = decision.Reason switch
            {
                PromotionPolicy.ReasonPromote => "promote",
                PromotionPolicy.ReasonDemote => "demote",
                PromotionPolicy.ReasonInsufficientData => "watch",
                _ => decision.RollingAvg >= 88m ? "hold" : "watch",
            };
            var demotionRisk = decision.RollingAvg > 0 && decision.RollingAvg < ScoringConstants.DemoteAvgMax;

            // Promotion ETA — rough estimate: how many more deliverables
            // to reach the window size, or null if already eligible
            int? promotionEta = null;
            if (verdict == "promote") promotionEta = 0;
            else if (policyWindow.Count < ScoringConstants.RollingWindow)
                promotionEta = ScoringConstants.RollingWindow - policyWindow.Count;

            var lastPromotedAt = promoHistory.TryGetValue(agent.Id, out var ph)
                ? ph.Where(h => h.Reason == PromotionPolicy.ReasonPromote)
                     .OrderByDescending(h => h.CreatedAt)
                     .Select(h => h.CreatedAt.ToString("yyyy-MM-dd"))
                     .FirstOrDefault()
                : null;

            var cooldownEnd = lastPromoAt?.AddDays(ScoringConstants.PromotionCooldownDays);
            var nextEligibleAt = cooldownEnd.HasValue && cooldownEnd.Value > nowUtc
                ? cooldownEnd.Value.ToString("yyyy-MM-dd")
                : "now";

            var verdictNote = verdict switch
            {
                "promote" => $"Avg {decision.RollingAvg:F1} ≥ {ScoringConstants.PromoteAvgMin}, 0 rejects, cooldown clear.",
                "demote" => $"Avg {decision.RollingAvg:F1} < {ScoringConstants.DemoteAvgMax}. Immediate demotion.",
                "hold" => $"Avg {decision.RollingAvg:F1} — close to promo threshold ({ScoringConstants.PromoteAvgMin}). Hold for next window.",
                "watch" => policyWindow.Count < ScoringConstants.RollingWindow
                    ? $"Only {policyWindow.Count}/{ScoringConstants.RollingWindow} deliverables. Needs more data."
                    : $"Avg {decision.RollingAvg:F1}. Stable — no action.",
                _ => "",
            };

            scorecards.Add(new
            {
                agentId = agent.Id,
                name = agent.Name,
                team,
                crew = crewName,
                crewId = agent.CrewId ?? "",
                level = agent.Level,
                title,
                hourlyUsd = (double)agent.HourlyUsd,
                throughput,
                passRate,
                avgScore = Math.Round(avgScore, 1),
                iterP50,
                iterP90,
                rejectRate,
                disputeRate,
                stalledRate,
                utilization = Math.Round(utilization, 2),
                costPerPass = Math.Round(costPerPass, 2),
                scoreSpark,
                dims,
                promotionEta,
                demotionRisk,
                verdict,
                verdictNote,
                lastPromotedAt,
                nextEligibleAt,
                monthlyAvg,
                monthlyMonths,
                repos = agentFinals
                    .Select(d => wiRepo.GetValueOrDefault(d.WorkItemId, ""))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Distinct()
                    .ToList(),
                products = agentFinals
                    .Select(d => wiRepo.GetValueOrDefault(d.WorkItemId, ""))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Select(r => repoProduct.GetValueOrDefault(r, ""))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct()
                    .ToList(),
            });

            // Build promotion decision card for agents with meaningful data
            if (verdict == "promote" || verdict == "demote"
                || (verdict == "hold" && policyWindow.Count >= ScoringConstants.RollingWindow))
            {
                var toLevel = verdict == "promote" ? agent.Level + 1
                    : verdict == "demote" ? agent.Level - 1
                    : agent.Level;

                var cooldownDays = lastPromoAt.HasValue
                    ? Math.Max(0, ScoringConstants.PromotionCooldownDays - (int)(nowUtc - lastPromoAt.Value).TotalDays)
                    : 0;

                var rationale = new List<string>();
                var risks = new List<string>();
                if (verdict == "promote")
                {
                    rationale.Add($"Rolling avg {decision.RollingAvg:F1} ≥ {ScoringConstants.PromoteAvgMin}");
                    rationale.Add($"0 rejects in trailing {ScoringConstants.RollingWindow}");
                    rationale.Add($"Cooldown clear ({ScoringConstants.PromotionCooldownDays}d policy)");
                    if (toLevel >= ScoringConstants.LeadMinLevel)
                        risks.Add($"L{toLevel} is lead-eligible — verify crew capacity");
                }
                else if (verdict == "demote")
                {
                    rationale.Add($"Rolling avg {decision.RollingAvg:F1} < {ScoringConstants.DemoteAvgMax}");
                    if (agent.Level >= ScoringConstants.LeadMinLevel)
                        risks.Add($"Currently L{agent.Level} (lead-eligible) — demotion may vacate lead");
                }
                else
                {
                    rationale.Add($"Rolling avg {decision.RollingAvg:F1} — approaching threshold");
                }

                promotionDecisions.Add(new
                {
                    agentId = agent.Id,
                    name = agent.Name,
                    team,
                    fromLevel = agent.Level,
                    fromTitle = title,
                    toLevel,
                    toTitle = LevelTitles.GetValueOrDefault(toLevel, $"L{toLevel}"),
                    windowAvg = Math.Round((double)decision.RollingAvg, 1),
                    windowSize = policyWindow.Count,
                    rejectsInWindow = decision.HasRejectInWindow ? windowRows.Count(d =>
                        string.Equals(d.Verdict, "reject", StringComparison.OrdinalIgnoreCase)) : 0,
                    cooldownDaysRemaining = cooldownDays,
                    signedOff = false,
                    signedOffBy = (string?)null,
                    rationale,
                    risks,
                    nominatedAt = nowUtc.ToString("yyyy-MM-dd"),
                });
            }
        }

        // ── Monthly snapshots (last 6 months) ─────────────────────────
        var monthlySnapshots = ComputeMonthlySnapshots(
            finalDeliverables, usageSlims, agents, nowUtc);

        return Results.Ok(new
        {
            window = windowKey,
            scorecards,
            promotionDecisions,
            monthlySnapshots,
            filterOptions = new
            {
                products = products.Select(p => new { id = p.Id, name = p.Name }).ToList(),
                repos = repos.Select(r => new { id = r.Id, productId = r.ProductId ?? "" }).ToList(),
                teams = TeamOrder.ToList(),
            },
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string RoleToTeam(string role) => role.ToLowerInvariant() switch
    {
        "arch" => "Architect",
        "eng" => "Engineer",
        "rev" => "Review",
        "qa" => "QA",
        _ => "Engineer",
    };

    private static List<DeliverableRow> ApplyWindowFilter(
        List<DeliverableRow> rows, string windowKey, DateTimeOffset now)
    {
        return windowKey switch
        {
            "mtd" => rows.Where(d => d.CreatedAt.UtcDateTime.Year == now.UtcDateTime.Year
                                  && d.CreatedAt.UtcDateTime.Month == now.UtcDateTime.Month).ToList(),
            "lastMonth" =>
                rows.Where(d =>
                {
                    var prev = now.UtcDateTime.AddMonths(-1);
                    return d.CreatedAt.UtcDateTime.Year == prev.Year
                        && d.CreatedAt.UtcDateTime.Month == prev.Month;
                }).ToList(),
            _ => rows.Take(ScoringConstants.RollingWindow).ToList(), // trailing20
        };
    }

    private static int Percentile(List<int> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Max(0, idx)];
    }

    private static object ComputeAverageDims(List<DeliverableRow> rows, double avgScore)
    {
        // Try to extract from DimsJson. If all empty, derive from avgScore.
        int correctness = 0, quality = 0, tests = 0, scope = 0, risk = 0;
        var count = 0;

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.DimsJson) || row.DimsJson == "{}") continue;
            try
            {
                using var doc = JsonDocument.Parse(row.DimsJson);
                var root = doc.RootElement;
                correctness += GetDim(root, "correctness");
                quality += GetDim(root, "quality");
                tests += GetDim(root, "tests");
                scope += GetDim(root, "scope");
                risk += GetDim(root, "risk");
                count++;
            }
            catch
            {
                // malformed JSON — skip
            }
        }

        if (count > 0)
        {
            return new
            {
                correctness = correctness / count,
                quality = quality / count,
                tests = tests / count,
                scope = scope / count,
                risk = risk / count,
            };
        }

        // Fallback: distribute avgScore proportionally across rubric weights
        var pct = avgScore / 100.0;
        return new
        {
            correctness = (int)Math.Round(pct * ScoringConstants.CorrectnessWeight),
            quality = (int)Math.Round(pct * ScoringConstants.QualityWeight),
            tests = (int)Math.Round(pct * ScoringConstants.TestsWeight),
            scope = (int)Math.Round(pct * ScoringConstants.ScopeWeight),
            risk = (int)Math.Round(pct * ScoringConstants.RiskWeight),
        };
    }

    private static int GetDim(JsonElement root, string key)
    {
        return root.TryGetProperty(key, out var el) && el.TryGetInt32(out var v) ? v : 0;
    }

    private static (List<double> avgs, List<string> labels) ComputeMonthlyAvg(
        List<DeliverableRow> agentFinals, DateTimeOffset now)
    {
        var avgs = new List<double>();
        var labels = new List<string>();

        for (var i = 5; i >= 0; i--)
        {
            var monthDate = now.UtcDateTime.AddMonths(-i);
            var year = monthDate.Year;
            var month = monthDate.Month;
            var inMonth = agentFinals.Where(d =>
                d.CreatedAt.UtcDateTime.Year == year && d.CreatedAt.UtcDateTime.Month == month).ToList();

            labels.Add(monthDate.ToString("MMM"));
            avgs.Add(inMonth.Count > 0 ? Math.Round(inMonth.Average(d => (double)d.Score), 1) : 0);
        }

        return (avgs, labels);
    }

    private sealed record UsageSlim(DateTimeOffset StartedAt, decimal CostUsd);

    private static List<object> ComputeMonthlySnapshots(
        List<DeliverableRow> finalDeliverables,
        List<UsageSlim> usageSlims,
        List<AgentRow> agents,
        DateTimeOffset now)
    {
        var snapshots = new List<object>();

        for (var i = 5; i >= 0; i--)
        {
            var monthDate = now.UtcDateTime.AddMonths(-i);
            var year = monthDate.Year;
            var month = monthDate.Month;

            var inMonth = finalDeliverables.Where(d =>
                d.CreatedAt.UtcDateTime.Year == year && d.CreatedAt.UtcDateTime.Month == month).ToList();

            // Spend from agent_usage
            var spendInMonth = usageSlims
                .Where(u => u.StartedAt.UtcDateTime.Year == year && u.StartedAt.UtcDateTime.Month == month)
                .Sum(u => (double)u.CostUsd);

            // Payroll estimate: all agents × hourlyUsd × shift hours × work days in month
            var workDaysInMonth = Enumerable.Range(1, DateTime.DaysInMonth(year, month))
                .Count(d => ScoringConstants.WorkDays.Contains(new DateTime(year, month, d).DayOfWeek));
            var payrollUsd = agents.Sum(a => (double)a.HourlyUsd) * ScoringConstants.ShiftHoursPerDay * workDaysInMonth;

            var passCount = inMonth.Count(d =>
                string.Equals(d.Verdict, "pass", StringComparison.OrdinalIgnoreCase));
            var rejectCount = inMonth.Count(d =>
                string.Equals(d.Verdict, "reject", StringComparison.OrdinalIgnoreCase));

            var isCurrentMonth = year == now.UtcDateTime.Year && month == now.UtcDateTime.Month;

            snapshots.Add(new
            {
                month = monthDate.ToString("MMM yyyy"),
                agents = agents.Count,
                avgScore = inMonth.Count > 0 ? Math.Round(inMonth.Average(d => (double)d.Score), 1) : 0,
                promotions = 0, // from level history — currently empty
                demotions = 0,
                payrollUsd = Math.Round(payrollUsd, 0),
                spendUsd = Math.Round(spendInMonth, 2),
                passRate = inMonth.Count > 0 ? Math.Round((double)passCount / inMonth.Count, 2) : 0,
                rejectRate = inMonth.Count > 0 ? Math.Round((double)rejectCount / inMonth.Count, 2) : 0,
                locked = !isCurrentMonth,
            });
        }

        return snapshots;
    }
}
