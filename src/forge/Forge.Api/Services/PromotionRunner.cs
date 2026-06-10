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
    /// <summary>
    /// Runs with <see cref="PromotionRunOptions.Manual"/> — tier policy only,
    /// promotions and demotions applied, no extra gate. This is the
    /// behaviour the admin <c>POST /api/admin/promotion/run</c> endpoint and
    /// the existing tests expect.
    /// </summary>
    Task<PromotionRunResult> RunAsync(CancellationToken ct);

    /// <summary>
    /// Phase 91 — runs with explicit options. The nightly scheduler passes
    /// <see cref="PromotionRunOptions.NightlyAuto"/> (promotion-only, with the
    /// avg &gt; <see cref="ScoringConstants.AutoPromoteMinAvg"/> + throughput
    /// &ge; <see cref="ScoringConstants.AutoPromoteMinThroughput"/> gate).
    /// </summary>
    Task<PromotionRunResult> RunAsync(PromotionRunOptions options, CancellationToken ct);
}

/// <summary>
/// Phase 91 — knobs that select how a promotion run behaves. The displayed
/// tier verdict / Promo-ETA on the Performance scoreboard are unaffected by
/// these — they only govern which decisions the runner actually <i>applies</i>.
/// </summary>
/// <param name="RequireAutoPromotionGate">
/// When true, a <see cref="PromotionPolicy.ReasonPromote"/> verdict is applied
/// only if the trailing-window average is strictly &gt;
/// <see cref="ScoringConstants.AutoPromoteMinAvg"/> AND the window holds at
/// least <see cref="ScoringConstants.AutoPromoteMinThroughput"/> deliverables.
/// Otherwise the decision is recorded as
/// <see cref="PromotionPolicy.ReasonPromoteGated"/> and no level change occurs.
/// </param>
/// <param name="ApplyDemotions">
/// When false, <see cref="PromotionPolicy.ReasonDemote"/> verdicts are recorded
/// as <see cref="PromotionPolicy.ReasonDemoteSkipped"/> but not applied — the
/// nightly auto path is promotion-only.
/// </param>
public sealed record PromotionRunOptions(bool RequireAutoPromotionGate, bool ApplyDemotions)
{
    /// <summary>Admin / on-demand: tier policy only, promotions + demotions, no gate.</summary>
    public static readonly PromotionRunOptions Manual =
        new(RequireAutoPromotionGate: false, ApplyDemotions: true);

    /// <summary>Nightly scheduler: promotion-only, with the avg/throughput gate.</summary>
    public static readonly PromotionRunOptions NightlyAuto =
        new(RequireAutoPromotionGate: true, ApplyDemotions: false);
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
    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly ProductFanout? fanout;
    private readonly IForgeDbPaths? dbPaths;
    private readonly ILogger<PromotionRunner> log;

    public PromotionRunner(ControlDbContext control, ProductDbContext db, ILogger<PromotionRunner> log)
        : this(control, db, fanout: null, dbPaths: null, log)
    {
    }

    public PromotionRunner(
        ControlDbContext control,
        ProductDbContext db,
        ProductFanout? fanout,
        IForgeDbPaths? dbPaths,
        ILogger<PromotionRunner> log)
    {
        this.control = control;
        this.db = db;
        this.fanout = fanout;
        this.dbPaths = dbPaths;
        this.log = log;
    }

    public Task<PromotionRunResult> RunAsync(CancellationToken ct)
        => this.RunAsync(PromotionRunOptions.Manual, ct);

    public async Task<PromotionRunResult> RunAsync(PromotionRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var agents = await this.control.Agents.AsNoTracking().ToListAsync(ct);

        // Phase 73 split — when per-product DBs are live, the injected
        // `db` ProductDbContext points at a SINGLE product (or, for admin
        // endpoints with no product header, the empty control DB). Either
        // way it does not contain deliverables for cross-product agents.
        // Pre-aggregate every agent's deliverables across every product
        // via ProductFanout so the policy sees the full picture, mirroring
        // PerformanceEndpoints / docs/forge/split-db.md guidance.
        IReadOnlyDictionary<string, List<DeliverableRow>> agentDeliverablesAll =
            await this.LoadDeliverablesByAgentAsync(ct);

        var decisions = new List<PromotionAgentResult>();
        var promoted = 0;
        var demoted = 0;

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();

            var window = BuildTrailingWindowFrom(
                agentDeliverablesAll.TryGetValue(agent.Id, out var rows) ? rows : new List<DeliverableRow>());

            // Last-promotion lookup — most recent promotion row in the
            // history. A demotion does NOT reset the cooldown clock per
            // spec §4 (the cooldown gates promotion only). SQLite can't
            // sort DateTimeOffset at the SQL layer, so we pull + sort
            // client-side. Phase 89: operator-promote rows count toward
            // the cooldown alongside nightly-job rows.
            var promotions = await this.control.AgentLevelHistory.AsNoTracking()
                .Where(h => h.AgentId == agent.Id
                            && (h.Reason == PromotionPolicy.ReasonPromote
                                || h.Reason == PromotionPolicy.ReasonOperatorPromote))
                .Select(h => h.CreatedAt)
                .ToListAsync(ct);
            DateTimeOffset? lastPromotion = promotions.Count == 0
                ? null
                : promotions.Max();

            var decision = PromotionPolicy.Evaluate(agent.Level, lastPromotion, window, now);

            // Default: nothing applied — NewLevel mirrors OldLevel unless we
            // actually move the agent below. Reason starts at the policy
            // verdict and is rewritten to a *-gated / *-skipped marker when an
            // options gate suppresses the change.
            var appliedNewLevel = decision.OldLevel;
            var effectiveReason = decision.Reason;

            if (decision.Reason == PromotionPolicy.ReasonPromote)
            {
                // Phase 91 — nightly safety gate. RollingAvg and WindowSize are
                // the trailing-window average + final-deliverable count, i.e.
                // exactly the AVG and THROUGHPUT columns the operator reads on
                // the Performance scoreboard.
                var gatePass = !options.RequireAutoPromotionGate
                    || (decision.RollingAvg > ScoringConstants.AutoPromoteMinAvg
                        && decision.WindowSize >= ScoringConstants.AutoPromoteMinThroughput);

                if (gatePass)
                {
                    await ApplyLevelChangeAsync(agent.Id, decision, now, ct);
                    appliedNewLevel = decision.NewLevel;
                    promoted++;
                }
                else
                {
                    effectiveReason = PromotionPolicy.ReasonPromoteGated;
                }
            }
            else if (decision.Reason == PromotionPolicy.ReasonDemote)
            {
                if (options.ApplyDemotions)
                {
                    await ApplyLevelChangeAsync(agent.Id, decision, now, ct);
                    appliedNewLevel = decision.NewLevel;
                    demoted++;
                    // Q14.5 — demoting a current crew lead below 7 should fire
                    // crew.lead-vacated. Logged here; full lead-pending state
                    // machine lands in a follow-up batch (no schema field for
                    // LeadPendingSince yet).
                    if (decision.NewLevel < ScoringConstants.LeadMinLevel)
                    {
                        var crewLed = await this.control.Crews
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
                else
                {
                    // Nightly auto path is promotion-only — surface the
                    // demote intent in the decisions list but do not apply it.
                    effectiveReason = PromotionPolicy.ReasonDemoteSkipped;
                }
            }
            else
            {
                // noop / insufficient-data — NewLevel already equals OldLevel.
                appliedNewLevel = decision.NewLevel;
            }

            decisions.Add(new PromotionAgentResult(
                agent.Id, decision.OldLevel, appliedNewLevel, decision.RollingAvg,
                effectiveReason, decision.WindowSize));
        }

        return new PromotionRunResult(agents.Count, promoted, demoted, decisions, now);
    }

    /// <summary>
    /// Phase 73-aware loader: when the split marker is present AND a
    /// fanout helper was injected, iterate every per-product DB and
    /// concatenate deliverables keyed by agentId (generator OR evaluator).
    /// Otherwise fall back to the single injected <see cref="db"/>
    /// context (legacy + test path: ForgeWebApplicationFactory does not
    /// register the split, so all rows live in the test ProductDbContext).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<DeliverableRow>>> LoadDeliverablesByAgentAsync(
        CancellationToken ct)
    {
        var splitApplied = this.dbPaths is not null && SplitMarkerCache.IsPhase73Applied(this.dbPaths);
        var byAgent = new Dictionary<string, List<DeliverableRow>>(StringComparer.OrdinalIgnoreCase);

        if (splitApplied && this.fanout is not null)
        {
            var lockObj = new object();
            await this.fanout.ForEachProductAsync(
                async (control2, productDb, slug, c) =>
                {
                    var rows = await productDb.Deliverables.AsNoTracking().ToListAsync(c);
                    lock (lockObj)
                    {
                        foreach (var d in rows)
                        {
                            AddRow(byAgent, d.GeneratorAgentId, d);
                            if (!string.IsNullOrEmpty(d.EvaluatorAgentId)
                                && !string.Equals(d.EvaluatorAgentId, d.GeneratorAgentId, StringComparison.OrdinalIgnoreCase))
                            {
                                AddRow(byAgent, d.EvaluatorAgentId, d);
                            }
                        }
                    }
                },
                ct);
        }
        else
        {
            var rows = await this.db.Deliverables.AsNoTracking().ToListAsync(ct);
            foreach (var d in rows)
            {
                AddRow(byAgent, d.GeneratorAgentId, d);
                if (!string.IsNullOrEmpty(d.EvaluatorAgentId)
                    && !string.Equals(d.EvaluatorAgentId, d.GeneratorAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    AddRow(byAgent, d.EvaluatorAgentId, d);
                }
            }
        }

        return byAgent;

        static void AddRow(Dictionary<string, List<DeliverableRow>> map, string? agentId, DeliverableRow row)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            if (!map.TryGetValue(agentId, out var list))
            {
                list = new List<DeliverableRow>();
                map[agentId] = list;
            }
            list.Add(row);
        }
    }

    /// <summary>
    /// Compute the trailing-window for an agent from the pre-loaded set
    /// of deliverables (generator OR evaluator). Matches the original
    /// <c>BuildTrailingWindowAsync</c> semantics: order by <c>CreatedAt</c>,
    /// keep the latest row per <c>WorkItemId</c> (= final iter), cap at
    /// <see cref="ScoringConstants.MaxPromotionWindow"/> so any tier's
    /// data needs can be satisfied from the returned buffer. The policy
    /// evaluator slices this further per tier.
    /// </summary>
    private static IReadOnlyList<DeliverableRow> BuildTrailingWindowFrom(IReadOnlyList<DeliverableRow> raw)
    {
        // Group by WorkItemId across the agent's entire history (rows are
        // already in memory from LoadDeliverablesByAgentAsync, so no SQL
        // bound is needed). Take the latest row per work item, then sort
        // newest-first and cap at the max tier window.
        return raw
            .GroupBy(d => d.WorkItemId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .OrderByDescending(d => d.CreatedAt)
            .Take(ScoringConstants.MaxPromotionWindow)
            .ToList();
    }

    /// <summary>
    /// Legacy single-product trailing-window builder, kept for tests /
    /// callers that still rely on per-agent ad-hoc lookup against the
    /// injected <see cref="db"/>. Production paths use
    /// <see cref="LoadDeliverablesByAgentAsync"/> + <see cref="BuildTrailingWindowFrom"/>.
    /// </summary>
    private async Task<IReadOnlyList<DeliverableRow>> BuildTrailingWindowAsync(string agentId, CancellationToken ct)
    {
        // Pull every row touching this agent and order client-side: SQLite
        // can't sort DateTimeOffset at the SQL layer.
        var raw = (await this.db.Deliverables.AsNoTracking()
                .Where(d => d.GeneratorAgentId == agentId || d.EvaluatorAgentId == agentId)
                .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .Take(ScoringConstants.MaxPromotionWindow * 5)
            .ToList();

        // For each work item, keep only the row with the highest Iter.
        var byWorkItem = raw
            .GroupBy(d => d.WorkItemId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .OrderByDescending(d => d.CreatedAt)
            .Take(ScoringConstants.MaxPromotionWindow)
            .ToList();

        return byWorkItem;
    }

    private async Task ApplyLevelChangeAsync(string agentId, PromotionPolicy.Decision d, DateTimeOffset at, CancellationToken ct)
    {
        var row = await this.control.Agents.FirstAsync(a => a.Id == agentId, ct);
        row.Level = d.NewLevel;
        row.LevelUpdatedAt = at;
        row.HourlyUsd = ScoringConstants.HourlyForLevel(d.NewLevel);

        this.control.AgentLevelHistory.Add(new AgentLevelHistoryRow
        {
            AgentId = agentId,
            OldLevel = d.OldLevel,
            NewLevel = d.NewLevel,
            WindowAvg = d.RollingAvg,
            Reason = d.Reason,
            CreatedAt = at,
        });

        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
    }
}
