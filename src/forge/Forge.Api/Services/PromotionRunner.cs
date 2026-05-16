// -----------------------------------------------------------------------
// <copyright file="PromotionRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 14.7 — applies <see cref="PromotionPolicy"/> across every agent
/// in the database. Reads each agent's trailing-20 final-iter
/// <see cref="DeliverableRow"/> set (filtered to one row per work item —
/// the maximum <c>iter</c>), evaluates the policy, and writes any
/// promotion / demotion to <see cref="AgentLevelHistoryRow"/> +
/// <see cref="AgentRow.Level"/> + <see cref="AgentRow.HourlyUsd"/>.
/// </summary>
public interface IPromotionRunner
{
    Task<PromotionRunResult> RunAsync(CancellationToken ct);
}

/// <summary>One agent's promotion decision + outcome.</summary>
public sealed record PromotionAgentResult(
    string AgentId,
    int OldLevel,
    int NewLevel,
    decimal RollingAvg,
    string Reason,
    int WindowSize);

/// <summary>Aggregate outcome of a single run.</summary>
public sealed record PromotionRunResult(
    int AgentsScanned,
    int Promoted,
    int Demoted,
    IReadOnlyList<PromotionAgentResult> Decisions,
    DateTimeOffset At);

/// <inheritdoc cref="IPromotionRunner"/>
public sealed class PromotionRunner : IPromotionRunner
{
    private readonly ProductDbContext db;
    private readonly ILogger<PromotionRunner> log;

    public PromotionRunner(ProductDbContext db, ILogger<PromotionRunner> log)
    {
        this.db = db;
        this.log = log;
    }

    public async Task<PromotionRunResult> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var agents = await this.db.Agents.AsNoTracking().ToListAsync(ct);

        var decisions = new List<PromotionAgentResult>();
        var promoted = 0;
        var demoted = 0;

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();

            var window = await BuildTrailingWindowAsync(agent.Id, ct);

            // Last-promotion lookup — most recent promotion row in the
            // history. A demotion does NOT reset the cooldown clock per
            // spec §4 (the cooldown gates promotion only). SQLite can't
            // sort DateTimeOffset at the SQL layer, so we pull + sort
            // client-side.
            var promotions = await this.db.AgentLevelHistory.AsNoTracking()
                .Where(h => h.AgentId == agent.Id && h.Reason == PromotionPolicy.ReasonPromote)
                .Select(h => h.CreatedAt)
                .ToListAsync(ct);
            DateTimeOffset? lastPromotion = promotions.Count == 0
                ? null
                : promotions.Max();

            var decision = PromotionPolicy.Evaluate(agent.Level, lastPromotion, window, now);

            decisions.Add(new PromotionAgentResult(
                agent.Id, decision.OldLevel, decision.NewLevel, decision.RollingAvg,
                decision.Reason, decision.WindowSize));

            if (decision.Reason == PromotionPolicy.ReasonPromote)
            {
                await ApplyLevelChangeAsync(agent.Id, decision, now, ct);
                promoted++;
            }
            else if (decision.Reason == PromotionPolicy.ReasonDemote)
            {
                await ApplyLevelChangeAsync(agent.Id, decision, now, ct);
                demoted++;
                // Q14.5 — demoting a current crew lead below 7 should fire
                // crew.lead-vacated. Logged here; full lead-pending state
                // machine lands in a follow-up batch (no schema field for
                // LeadPendingSince yet).
                if (decision.NewLevel < ScoringConstants.LeadMinLevel)
                {
                    var crewLed = await this.db.Crews
                        .Where(c => c.Lead == agent.Id)
                        .Select(c => c.Id)
                        .FirstOrDefaultAsync(ct);
                    if (crewLed is not null)
                    {
                        this.log.LogWarning(
                            "Crew {CrewId} lead {AgentId} demoted to L{Level} (< {Floor}) — manual replacement required.",
                            crewLed, agent.Id, decision.NewLevel, ScoringConstants.LeadMinLevel);
                    }
                }
            }
        }

        return new PromotionRunResult(agents.Count, promoted, demoted, decisions, now);
    }

    /// <summary>
    /// For an agent: pull their **final-iter** rows where the agent is
    /// either generator or evaluator, ordered most-recent first, capped
    /// at the rolling-window size. "Final iter" = max(<c>Iter</c>) per
    /// <c>WorkItemId</c>.
    /// </summary>
    private async Task<IReadOnlyList<DeliverableRow>> BuildTrailingWindowAsync(string agentId, CancellationToken ct)
    {
        // Pull every row touching this agent and order client-side: SQLite
        // can't sort DateTimeOffset at the SQL layer.
        var raw = (await this.db.Deliverables.AsNoTracking()
                .Where(d => d.GeneratorAgentId == agentId || d.EvaluatorAgentId == agentId)
                .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .Take(ScoringConstants.RollingWindow * 5)
            .ToList();

        // For each work item, keep only the row with the highest Iter.
        var byWorkItem = raw
            .GroupBy(d => d.WorkItemId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .OrderByDescending(d => d.CreatedAt)
            .Take(ScoringConstants.RollingWindow)
            .ToList();

        return byWorkItem;
    }

    private async Task ApplyLevelChangeAsync(string agentId, PromotionPolicy.Decision d, DateTimeOffset at, CancellationToken ct)
    {
        var row = await this.db.Agents.FirstAsync(a => a.Id == agentId, ct);
        row.Level = d.NewLevel;
        row.LevelUpdatedAt = at;
        row.HourlyUsd = ScoringConstants.HourlyForLevel(d.NewLevel);

        this.db.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = agentId,
            OldLevel = d.OldLevel,
            NewLevel = d.NewLevel,
            WindowAvg = d.RollingAvg,
            Reason = d.Reason,
            CreatedAt = at,
        });

        await this.db.SaveChangesAsync(ct);
    }
}
