// -----------------------------------------------------------------------
// <copyright file="MergedPrRescoreReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using System.Text.Json;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

/// <summary>
/// 2026-05-27 — recovery sweep that runs the evaluator against an
/// ALREADY-MERGED workstream's landed code and records a real
/// <c>DeliverableRow</c>.
///
/// <para><b>Why.</b> The dominant "COMPLETE with score 0 / never makes
/// progress" failure: an impl workstream's generator runs right up against
/// its wall-clock budget (e.g. an 8-pt workstream's 24-min Copilot SDK hard
/// timeout on a large repo), so the gen↔eval loop force-scores the
/// deliverable 0 / <c>verdict=iterate</c> with <c>FailureKind=generator-timeout</c>
/// — even though the work actually landed and its PR merged. The workstream
/// flips to <c>complete</c> (PR merged) but
/// <see cref="StalledMergedRecoveryReconciler"/> refuses to close the work
/// item because the latest deliverable isn't a pass. The operator then
/// Restarts repeatedly, each Restart re-driving merged work into the same
/// timeout (observed 5 days, ~$258 + ~64 min wasted per cycle on
/// <c>common-infra</c> ws 1.3 "Add banned-API analyzer wiring").</para>
///
/// <para><b>What.</b> For each workstream whose PR is <c>merged</c> but whose
/// latest deliverable is NOT a natural pass / operator-accept, invoke
/// <see cref="IStoryArtifactStrategy.ScoreMergedPrAsync"/> ONCE — the evaluator
/// reads the merged files in a worktree and returns a real score. A pass
/// (≥ <see cref="ScoringConstants.PassThreshold"/>) is written as a
/// <c>NaturalPass</c>-shaped deliverable (verdict=pass, no FailureKind), the
/// work item is closed, and the Restart loop ends. A sub-pass writes the
/// honest score (verdict=iterate) + the evaluator's notes and emits a
/// <c>task.merged-pr-rescored</c> signal so a review-fix follow-up can be
/// filed; the workstream is NOT re-driven into the impl timeout (the claim
/// sweep already skips <c>complete</c>).</para>
///
/// <para><b>Idempotency.</b> Every re-score deliverable carries
/// <see cref="RescoreMarker"/> in its Notes; a workstream whose latest
/// deliverable already carries the marker (or is already a pass) is skipped,
/// so the evaluator runs at most once per merge — never every tick. At most
/// one workstream per product is re-scored per tick to bound LLM cost; the
/// rest drain on subsequent ticks.</para>
///
/// <para>Runs once at boot (after a warmup so migrations/seed finish) then on
/// a relaxed 120 s tick — this is LLM-bound recovery, not a hot path. Gated by
/// <c>Forge:Stories:MergedPrRescoreEnabled</c> (default on).</para>
/// </summary>
public sealed class MergedPrRescoreReconciler : BackgroundService
{
    /// <summary>Notes marker stamped on every merged-PR re-score deliverable.
    /// Doubles as the idempotency guard (a workstream whose latest deliverable
    /// carries it is never re-scored).</summary>
    public const string RescoreMarker = "[MERGED-PR RE-SCORE]";

    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> RescoredCounter =
        Meter.CreateCounter<long>("forge.merged_pr.rescored");

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BootDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<MergedPrRescoreReconciler> logger;
    private readonly IStoryArtifactStrategy strategy;
    private readonly TaskChannel taskChannel;
    private readonly IConfiguration config;
    private readonly Forge.Api.Agents.IAgentRunner? agentRunner;

    public MergedPrRescoreReconciler(
        IServiceScopeFactory scopeFactory,
        IHubContext<ForgeHub> hub,
        ILogger<MergedPrRescoreReconciler> logger,
        IStoryArtifactStrategy strategy,
        TaskChannel taskChannel,
        IConfiguration config,
        Forge.Api.Agents.IAgentRunner? agentRunner = null)
    {
        this.scopeFactory = scopeFactory;
        this.hub = hub;
        this.logger = logger;
        this.strategy = strategy;
        this.taskChannel = taskChannel;
        this.config = config;
        this.agentRunner = agentRunner;
    }

    private bool Enabled =>
        this.config.GetValue("Forge:Stories:MergedPrRescoreEnabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(BootDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (this.Enabled)
                {
                    await this.RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "MergedPrRescoreReconciler tick errored — continuing");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick. Re-scores at most one eligible merged workstream per product
    /// and returns the number of workstreams re-scored. Public so tests can
    /// drive deterministically without the timer.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var total = 0;
        var lockObj = new object();

        await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
        {
            var n = await this.RescoreOneAsync(db, fct);
            if (n > 0)
            {
                lock (lockObj) total += n;
            }
        }, ct);

        if (total > 0)
        {
            await this.hub.Clients.All.SendAsync("forge:change", new { kind = "merged-pr-rescored", count = total }, ct);
        }
        return total;
    }

    /// <summary>
    /// Find the first eligible merged-but-non-pass workstream in this product
    /// and re-score it. Returns 1 when one was re-scored, else 0.
    /// </summary>
    private async Task<int> RescoreOneAsync(ProductDbContext db, CancellationToken ct)
    {
        // Candidate set: shipped (merged) impl workstreams. qa-e2e workstreams
        // own their own pass gate via the test suite and are excluded.
        var candidates = await db.Workstreams
            .Where(w => (w.Status == "complete" || w.Status == "merging")
                     && w.Category != WorkstreamCategories.QaE2e)
            .OrderBy(w => w.Sort)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        foreach (var ws in candidates)
        {
            // Concurrency: never re-score while an impl/agent invocation is
            // live for this workstream (mirrors the orchestrator's
            // IsTaskActive defer-guard for the review-dispatch race).
            if (this.agentRunner?.IsTaskActive(ws.Id) == true)
            {
                continue;
            }

            var mergedPrId = await ResolveMergedPrIdAsync(db, ws, ct);
            if (mergedPrId is null) continue;

            // Latest deliverable for this workstream (SQLite can't ORDER BY
            // DateTimeOffset — sort by the durable PairAttempt/Iter/Id keys).
            var latest = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == ws.Id)
                .OrderByDescending(d => d.PairAttempt ?? 0)
                .ThenByDescending(d => d.Iter)
                .ThenByDescending(d => d.Id)
                .FirstOrDefaultAsync(ct);

            // Idempotency: already re-scored this merge → skip.
            if (latest?.Notes is not null && latest.Notes.Contains(RescoreMarker, StringComparison.Ordinal))
            {
                continue;
            }

            // Already a clean pass / operator-accept → nothing to recover.
            var passClass = latest is null
                ? StorySpawnOrchestrator.PassGateClassification.NotPass
                : StorySpawnOrchestrator.ClassifyPassGate(latest.Verdict, latest.Score, latest.FailureKind, latest.Notes);
            if (passClass is StorySpawnOrchestrator.PassGateClassification.NaturalPass
                or StorySpawnOrchestrator.PassGateClassification.OperatorAccepted)
            {
                continue;
            }

            // Work item must still be open (no point re-scoring a closed WI).
            var wi = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == ws.Id, ct);
            if (wi is not null
                && string.Equals(wi.State, "closed", StringComparison.OrdinalIgnoreCase)
                && string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.Ordinal))
            {
                continue;
            }

            var spawn = string.IsNullOrEmpty(ws.StorySpawnId)
                ? null
                : await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (spawn is null) continue;

            await this.RescoreWorkstreamAsync(db, spawn, ws, wi, mergedPrId, latest, ct);
            return 1; // one per product per tick — bound LLM cost.
        }

        return 0;
    }

    private async Task RescoreWorkstreamAsync(
        ProductDbContext db,
        StorySpawnRow spawn,
        WorkstreamRow ws,
        WorkItemRow? wi,
        string mergedPrId,
        DeliverableRow? latest,
        CancellationToken ct)
    {
        this.logger.LogInformation(
            "MergedPrRescoreReconciler: re-scoring merged ws {WsId} (PR {PrId}); prior latest deliverable verdict={Verdict} score={Score} failureKind={Kind}",
            ws.Id, mergedPrId, latest?.Verdict ?? "(none)", latest?.Score ?? -1, latest?.FailureKind ?? "(none)");

        MergedPrScore score;
        try
        {
            score = await this.strategy.ScoreMergedPrAsync(spawn, ws, mergedPrId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient (worktree/git/agent transport) — leave the workstream
            // untouched so the next tick retries. Do NOT write a bogus 0.
            this.logger.LogWarning(ex,
                "MergedPrRescoreReconciler: ScoreMergedPrAsync threw for ws {WsId}; will retry next tick.",
                ws.Id);
            return;
        }

        if (score.ParseFailed)
        {
            // Evaluator produced nothing parseable — retry next tick rather
            // than persisting an idempotency marker that would wedge it.
            this.logger.LogWarning(
                "MergedPrRescoreReconciler: ws {WsId} re-score unparseable; retrying next tick.", ws.Id);
            return;
        }

        var isPass = score.Score >= ScoringConstants.PassThreshold
            && string.Equals(score.Verdict, "pass", StringComparison.OrdinalIgnoreCase);

        var notes = $"{RescoreMarker} Evaluator scored the MERGED PR {mergedPrId}'s landed code "
            + $"(prior deliverable was a non-pass — typically a generator wall-clock timeout that "
            + $"force-scored 0). Score {score.Score}, verdict {(isPass ? "pass" : "iterate")}.\n\n{score.Notes}";

        var row = new DeliverableRow
        {
            WorkItemId = ws.Id,
            CrewId = latest?.CrewId ?? "crew-rev",
            GeneratorAgentId = latest?.GeneratorAgentId ?? string.Empty,
            EvaluatorAgentId = score.EvaluatorAgentId ?? latest?.EvaluatorAgentId ?? string.Empty,
            Iter = (latest?.Iter ?? 0) + 1,
            PairAttempt = latest?.PairAttempt ?? 1,
            Score = score.Score,
            DimsJson = JsonSerializer.Serialize(score.Dims),
            // PASS path: no FailureKind so ClassifyPassGate => NaturalPass.
            Verdict = isPass ? "pass" : "iterate",
            Notes = notes,
            GenCostUsd = 0m,
            EvalCostUsd = score.EvalCostUsd,
            FailureKind = null,
            FailureReason = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Deliverables.Add(row);

        if (isPass && wi is not null)
        {
            // Mirror StalledMergedRecoveryReconciler's completion alignment:
            // a merged workstream with a (now) passing latest deliverable is
            // terminal. Closing the WI stops the operator's Restart loop.
            wi.State = "closed";
            wi.ExecutionStatus = ExecutionStatuses.Completed;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        RescoredCounter.Add(1, new KeyValuePair<string, object?>("verdict", isPass ? "pass" : "iterate"));

        // Surface the re-score on the workstream's Conversation tab.
        await this.taskChannel.EmitAsync(ws.Id, "task.merged-pr-rescored", new
        {
            wsId = ws.Id,
            spawnId = spawn.Id,
            prId = mergedPrId,
            score = score.Score,
            verdict = isPass ? "pass" : "iterate",
            evaluatorAgentId = score.EvaluatorAgentId,
            closedWorkItem = isPass,
            notes = score.Notes.Length > 2000 ? score.Notes[..2000] : score.Notes,
        }, agentId: score.EvaluatorAgentId, crewId: row.CrewId, ct: ct);

        if (isPass)
        {
            this.logger.LogInformation(
                "MergedPrRescoreReconciler: ws {WsId} merged PR re-scored {Score} (pass) — work item closed.",
                ws.Id, score.Score);
        }
        else
        {
            // Sub-pass: honest score recorded + marked (so we don't re-score
            // every tick). The claim sweep skips `complete`, so this does NOT
            // re-drive the impl timeout. Surfaced for a review-fix follow-up.
            this.logger.LogWarning(
                "MergedPrRescoreReconciler: ws {WsId} merged PR re-scored {Score} (< {Threshold}) — "
                + "honest score recorded; review-fix follow-up should be filed.",
                ws.Id, score.Score, ScoringConstants.PassThreshold);
        }
    }

    /// <summary>
    /// Resolve the id of a <c>merged</c> PR linked to this workstream, via the
    /// workstream's <c>PrsJson</c> list OR a <c>PrRow.Workstream</c> back-link.
    /// Returns null when none is merged.
    /// </summary>
    private static async Task<string?> ResolveMergedPrIdAsync(ProductDbContext db, WorkstreamRow ws, CancellationToken ct)
    {
        var prIds = StoriesJsonHelpers.Deserialize(ws.PrsJson) ?? new List<string>();
        var merged = await db.Prs.AsNoTracking()
            .Where(p => p.Col == "merged"
                && (p.Workstream == ws.Id || prIds.Contains(p.Id)))
            .OrderByDescending(p => p.Id)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);
        return merged;
    }
}
