// -----------------------------------------------------------------------
// <copyright file="StalledMergedRecoveryReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 2026-05-15 — auto-recovery sweep for workstreams that were wrongly
/// demoted from a shipped state (<c>complete</c> / <c>merging</c>) back
/// to <see cref="WorkstreamStatuses.Stalled"/> by a stale reconciler tick.
///
/// <para>The original failure scenario (failover-cluster:XRAFT stage 3.3
/// Log Replication): iters 1-3 produced <c>sloppy</c> verdicts so
/// <see cref="WorkItemRow.PairAttemptCount"/> reached
/// <see cref="ScoringConstants.MaxPairAttempts"/> (3). Iter 4 then passed,
/// the PR (#19) merged, and <see cref="WorkstreamRow.Status"/> moved to
/// <c>complete</c>. The next <see cref="SloppyReassignmentReconciler"/>
/// tick saw the stale <c>PairAttemptCount=3</c> and called
/// <c>MarkTerminalFailedAsync</c>, whose pre-fix filter
/// (<c>Where(w =&gt; w.Status != WorkstreamStatuses.Stalled)</c>) did NOT
/// exclude <c>complete</c> — so the merged workstream was forcibly
/// demoted to <c>stalled</c> with a bogus
/// <c>"Sloppy-3-attempts: ..."</c> reason. Downstream stage 4.1 had
/// already legitimately claimed against the brief <c>complete</c> window,
/// so the operator saw 4.1 running while 3.3 read as <c>stalled</c> —
/// looking like a dep-gate failure when in fact the upstream had been
/// retroactively un-completed.</para>
///
/// <para>The root cause was fixed in
/// <see cref="SloppyReassignmentReconciler"/>; this reconciler is the
/// belt-and-suspenders cleanup that promotes any
/// <c>stalled</c> workstream whose PR is in <c>merged</c> back to
/// <c>complete</c> (and clears <see cref="WorkstreamRow.StallReason"/>).
/// Idempotent and side-effect-free when no candidate rows exist.</para>
///
/// <para>Runs once at boot (with a one-tick warmup so migrations and seed
/// finish first, matching the convention used by other reconcilers) then
/// on a 60 s tick. The interval is intentionally longer than the
/// 15 s orchestrator tick because the only writer to the bad-stalled
/// state should be eliminated now; this is recovery for accumulated
/// rows and races, not the hot path.</para>
/// </summary>
public sealed class StalledMergedRecoveryReconciler : BackgroundService
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> RecoveredCounter =
        Meter.CreateCounter<long>("forge.stalled_merged.recovered");

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BootDelay = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<StalledMergedRecoveryReconciler> logger;

    public StalledMergedRecoveryReconciler(
        IServiceScopeFactory scopeFactory,
        IHubContext<ForgeHub> hub,
        ILogger<StalledMergedRecoveryReconciler> logger)
    {
        this.scopeFactory = scopeFactory;
        this.hub = hub;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(BootDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "StalledMergedRecoveryReconciler tick errored — continuing");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick. Returns the number of workstreams recovered. Public so
    /// tests can drive deterministically without waiting on the timer.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var recovered = 0;

        // Phase A — stalled rows whose PR is merged: promote to complete,
        // clear StallReason. Covers the original failure mode (sloppy-3
        // demotion stomped a row that had just shipped) and any future
        // races that bypass the cause fix in SloppyReassignmentReconciler.
        recovered += await this.RecoverStalledWithMergedPrAsync(db, ct);

        // Phase B — shipped rows (complete/merging/review/historic) that
        // still carry a non-null StallReason. This happens when one code
        // path lifts Status (e.g. WorkstreamMergeAdvancer flipping to
        // complete on PR merge detection) but doesn't clear the stale
        // failure reason from an earlier transient demotion. The reason
        // string is operator-visible (tooltip on the swarm card) so
        // leaving it stale misleads triage.
        recovered += await this.ClearStaleStallReasonOnShippedAsync(db, ct);

        // Phase C — 2026-05-15 — stalled rows with bogus
        // "Sloppy-3-attempts:" stall reason whose underlying WI is healthy
        // AND the most recent deliverable is a `pass` verdict, but no PR
        // has been opened yet. See xml-doc on
        // <see cref="RecoverBogusSloppyStallWithPassedDeliverableAsync"/>
        // for full failure-mode walkthrough.
        recovered += await this.RecoverBogusSloppyStallWithPassedDeliverableAsync(db, ct);

        if (recovered > 0)
        {
            await this.hub.Clients.All.SendAsync("forge:change", new { kind = "stalled-merged-recovered", count = recovered }, ct);
        }
        return recovered;
    }

    private async Task<int> RecoverStalledWithMergedPrAsync(ProductDbContext db, CancellationToken ct)
    {
        // Pull every stalled workstream up-front; in steady state this set
        // is small (operator-triage rows). Then resolve each row's PR list
        // and check if any are in `merged`. We avoid a join on PrsJson
        // because it's serialized as a JSON array — a server-side query
        // would need json_each and that's harder to keep portable.
        var stalled = await db.Workstreams
            .Where(w => w.Status == WorkstreamStatuses.Stalled)
            .ToListAsync(ct);
        if (stalled.Count == 0) return 0;

        // Collect candidate PR IDs across all stalled rows in one shot.
        var prIdsToCheck = new HashSet<string>(StringComparer.Ordinal);
        var prsByWs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var w in stalled)
        {
            var prIds = StoriesJsonHelpers.Deserialize(w.PrsJson);
            if (prIds is null || prIds.Count == 0) continue;
            prsByWs[w.Id] = prIds;
            foreach (var p in prIds) prIdsToCheck.Add(p);
        }
        if (prIdsToCheck.Count == 0) return 0;

        var mergedPrIds = await db.Prs.AsNoTracking()
            .Where(p => prIdsToCheck.Contains(p.Id) && p.Col == "merged")
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (mergedPrIds.Count == 0) return 0;
        var mergedSet = new HashSet<string>(mergedPrIds, StringComparer.Ordinal);

        var recovered = 0;
        foreach (var w in stalled)
        {
            if (!prsByWs.TryGetValue(w.Id, out var wsPrIds)) continue;
            if (!wsPrIds.Any(id => mergedSet.Contains(id))) continue;

            this.logger.LogInformation(
                "StalledMergedRecoveryReconciler: ws={WsId} stall->complete (merged PR present); prior StallReason={Reason}",
                w.Id, w.StallReason ?? "(null)");

            w.Status = "complete";
            w.StallReason = null;
            w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Mirror the workstream completion on the linked work item.
            var wi = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == w.Id, ct);
            if (wi is not null && !string.Equals(wi.State, "closed", StringComparison.OrdinalIgnoreCase))
            {
                wi.State = "closed";
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            recovered++;
        }

        if (recovered > 0)
        {
            await db.SaveChangesAsync(ct);
            RecoveredCounter.Add(recovered, new KeyValuePair<string, object?>("phase", "stalled->complete"));
        }
        return recovered;
    }

    /// <summary>
    /// Phase C — 2026-05-15 — recover stalled workstreams wrongly demoted
    /// by the bogus-sloppy chain (ImplSpawnGuard mis-counted no-progress
    /// during operator-question parking → routed to sloppy reassignment →
    /// SloppyReassignmentReconciler.MarkTerminalFailedAsync stamped
    /// "Sloppy-3-attempts: ..." stall + WI Failed) where the operator
    /// later answered the questions, the queue worker resumed the
    /// gen-eval loop independent of the bogus workstream status, and an
    /// iteration eventually scored pass — but the orchestrator's claim
    /// sweep at StorySpawnOrchestrator.cs:1801 keeps skipping the row
    /// because it's still flagged stalled, so the PR never opens and
    /// Phase A (which requires merged PR) never triggers. Permanent
    /// wedge observed 2026-05-15 on code-intelligence:AGENT-MEMORY ws 1.3
    /// (Episodic and concept schema migrations).
    ///
    /// <para>Recovery criteria (ALL must hold to avoid false-positive
    /// recovery of legitimately-stalled rows):</para>
    /// <list type="bullet">
    ///   <item><c>ws.Status == "stalled"</c></item>
    ///   <item><c>ws.StallReason</c> starts with <c>"Sloppy-3-attempts:"</c>
    ///   — the known-bogus prefix. Build-failure stalls (<c>"Gate failed"</c>),
    ///   no-progress stalls (<c>"No progress: ..."</c>), and other
    ///   stall reasons are left untouched.</item>
    ///   <item>No PR is currently linked to the workstream: empty
    ///   <c>PrsJson</c> AND no <c>PrRow</c> with
    ///   <c>Workstream == ws.Id</c>. A row with an existing PR should
    ///   either ride Phase A (when merged) or stay in the operator-
    ///   triage path.</item>
    ///   <item>Latest deliverable verdict is <c>"pass"</c>. This is the
    ///   primary signal that the work is ready to commit + open a PR.</item>
    ///   <item>The matching <c>WorkItemRow</c> has no terminal failure
    ///   indication: <c>FailureReason</c> is null AND
    ///   <c>ExecutionStatus</c> is not in
    ///   {<c>Failed</c>, <c>PendingReassign</c>, <c>Cancelled</c>}.</item>
    /// </list>
    ///
    /// <para>Effect: flip <c>ws.Status="active"</c>, clear
    /// <c>ws.StallReason</c>, clear stale <c>wi.FailureReason</c>. The
    /// orchestrator's next 15s tick picks the row up via
    /// <c>DriveOneWorkstreamImplAsync</c>'s pass-verdict branch and
    /// routes to <c>ReadCachedWorkstreamFilesAsync</c> for commit + PR
    /// open.</para>
    /// </summary>
    private async Task<int> RecoverBogusSloppyStallWithPassedDeliverableAsync(
        ProductDbContext db, CancellationToken ct)
    {
        const string BogusSloppyPrefix = "Sloppy-3-attempts:";

        var stalled = await db.Workstreams
            .Where(w => w.Status == WorkstreamStatuses.Stalled
                     && w.StallReason != null
                     && w.StallReason.StartsWith(BogusSloppyPrefix))
            .ToListAsync(ct);
        if (stalled.Count == 0) return 0;

        // Bulk-fetch PR rows that point at any of these workstreams.
        // PrsJson check + PrRow.Workstream check are complementary: the
        // JSON list is the workstream's view; the Workstream column is
        // the PR row's view. Either non-empty means the PR pipeline has
        // already touched this row and Phase C should NOT recover it.
        var wsIds = stalled.Select(w => w.Id).ToList();
        var prsByWs = await db.Prs.AsNoTracking()
            .Where(p => wsIds.Contains(p.Workstream))
            .Select(p => p.Workstream)
            .ToListAsync(ct);
        var wsWithPr = new HashSet<string>(prsByWs, StringComparer.Ordinal);

        // Bulk-fetch WI rows in one query.
        var wis = await db.WorkItems
            .Where(x => wsIds.Contains(x.Id))
            .ToListAsync(ct);
        var wiById = wis.ToDictionary(x => x.Id, StringComparer.Ordinal);

        // Bulk-fetch deliverables for these WIs. SQLite can't ORDER BY
        // DateTimeOffset — materialize then sort client-side, matching
        // the convention used elsewhere in this codebase.
        var delivs = await db.Deliverables.AsNoTracking()
            .Where(d => wsIds.Contains(d.WorkItemId))
            .Select(d => new { d.WorkItemId, d.Verdict, d.CreatedAt, d.Iter })
            .ToListAsync(ct);
        var latestByWs = delivs
            .GroupBy(d => d.WorkItemId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Iter).First(),
                StringComparer.Ordinal);

        var recovered = 0;
        foreach (var w in stalled)
        {
            string? rejectReason = null;

            // Skip if a PR is already linked anywhere — either the
            // workstream's PrsJson lists one, or a PR row points at us.
            var prIds = StoriesJsonHelpers.Deserialize(w.PrsJson);
            if ((prIds is not null && prIds.Count > 0) || wsWithPr.Contains(w.Id))
            {
                rejectReason = $"PR exists (PrsJson count={prIds?.Count ?? 0}, prRowMatch={wsWithPr.Contains(w.Id)})";
            }

            // Skip if no WI exists for this workstream — Phase C is
            // workstream-mode specific (WI.Id == ws.Id).
            else if (!wiById.TryGetValue(w.Id, out var wi))
            {
                rejectReason = "no WI found for this workstream id";
            }
            else
            {
                // WI must show no terminal failure indication.
                if (!string.IsNullOrEmpty(wi.FailureReason))
                {
                    rejectReason = $"WI.FailureReason set ('{wi.FailureReason}')";
                }
                else if (string.Equals(wi.ExecutionStatus, ExecutionStatuses.Failed, StringComparison.Ordinal)
                    || string.Equals(wi.ExecutionStatus, ExecutionStatuses.PendingReassign, StringComparison.Ordinal)
                    || string.Equals(wi.ExecutionStatus, ExecutionStatuses.Cancelled, StringComparison.Ordinal))
                {
                    rejectReason = $"WI.ExecutionStatus is terminal ('{wi.ExecutionStatus}')";
                }
                // Latest deliverable verdict must be pass.
                else if (!latestByWs.TryGetValue(w.Id, out var latest))
                {
                    rejectReason = "no deliverables for this WI";
                }
                else if (!string.Equals(latest.Verdict, "pass", StringComparison.Ordinal))
                {
                    rejectReason = $"latest deliverable verdict='{latest.Verdict}' (need 'pass')";
                }
                else
                {
                    // All filters pass — recover.
                    this.logger.LogInformation(
                        "StalledMergedRecoveryReconciler Phase C: ws={WsId} stall->active "
                        + "(bogus Sloppy-3-attempts cleared, latest verdict=pass iter={Iter}, no PR yet); "
                        + "prior StallReason={Reason}",
                        w.Id, latest.Iter, w.StallReason);

                    w.Status = "active";
                    w.StallReason = null;
                    w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    wi.FailureReason = null;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    recovered++;
                }
            }

            if (rejectReason is not null)
            {
                this.logger.LogInformation(
                    "StalledMergedRecoveryReconciler Phase C: ws={WsId} candidate REJECTED — {Reason}",
                    w.Id, rejectReason);
            }
        }

        if (recovered > 0)
        {
            await db.SaveChangesAsync(ct);
            RecoveredCounter.Add(recovered, new KeyValuePair<string, object?>("phase", "bogus-sloppy-stall->active"));
        }
        return recovered;
    }

    private async Task<int> ClearStaleStallReasonOnShippedAsync(ProductDbContext db, CancellationToken ct)
    {
        // A shipped row should never carry a StallReason. If one is set
        // here it's leftover state from a transient demotion that was
        // corrected by a different code path (e.g. WorkstreamMergeAdvancer
        // flipping Status="complete" when PR.Col="merged" arrived from
        // PrSyncJob) without clearing the failure reason. The tooltip on
        // the swarm card surfaces StallReason verbatim, so a stale value
        // makes the row look broken when it isn't.
        var dirty = await db.Workstreams
            .Where(w => w.StallReason != null && w.StallReason != string.Empty
                     && (w.Status == "complete"
                         || w.Status == "merging"
                         || w.Status == "review"
                         || w.Status == WorkstreamStatuses.Historic))
            .ToListAsync(ct);
        if (dirty.Count == 0) return 0;

        foreach (var w in dirty)
        {
            this.logger.LogInformation(
                "StalledMergedRecoveryReconciler: ws={WsId} (status={Status}) clearing stale StallReason={Reason}",
                w.Id, w.Status, w.StallReason);
            w.StallReason = null;
        }
        await db.SaveChangesAsync(ct);
        RecoveredCounter.Add(dirty.Count, new KeyValuePair<string, object?>("phase", "clear-stale-stallreason"));
        return dirty.Count;
    }
}
