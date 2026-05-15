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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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

        if (recovered > 0)
        {
            await this.hub.Clients.All.SendAsync("forge:change", new { kind = "stalled-merged-recovered", count = recovered }, ct);
        }
        return recovered;
    }

    private async Task<int> RecoverStalledWithMergedPrAsync(ForgeDbContext db, CancellationToken ct)
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
            recovered++;
        }

        if (recovered > 0)
        {
            await db.SaveChangesAsync(ct);
            RecoveredCounter.Add(recovered, new KeyValuePair<string, object?>("phase", "stalled->complete"));
        }
        return recovered;
    }

    private async Task<int> ClearStaleStallReasonOnShippedAsync(ForgeDbContext db, CancellationToken ct)
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
