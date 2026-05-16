// -----------------------------------------------------------------------
// <copyright file="BudgetCooldownReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 51 — auto-recovery for per-task budget-cap stalls.
///
/// <para>When the deliverable loop hits <see
/// cref="Forge.Api.Persistence.CrewRow.MaxTaskBudgetUsd"/> it returns
/// <c>verdict="stalled-budget"</c> and <see cref="CrewQueueWorker"/> parks
/// the work item at
/// <see cref="ExecutionStatuses.CooldownBudget"/> with
/// <c>BudgetCooldownUntilUtc = UtcNow + MaxWait</c>.</para>
///
/// <para>This reconciler ticks every <see
/// cref="BudgetCooldownOptions.TickInterval"/> (default 15 s) and resumes
/// the work item under one of two conditions:</para>
/// <list type="bullet">
///   <item><b>Cap raised</b> — operator increased <c>MaxTaskBudgetUsd</c>
///   on the crew so cumulative cost is now under cap. Resume immediately,
///   keep the same pair, clear <c>LastPairAgentIdsJson</c>. The user sees
///   the workstream un-stall within one tick of the PATCH.</item>
///   <item><b>Cooldown elapsed</b> — <c>BudgetCooldownUntilUtc</c> passed
///   without a cap raise. Resume with <c>LastPairAgentIdsJson</c> still
///   set so the claim picker rotates to a fresh pair.</item>
/// </list>
///
/// <para>On resume the reconciler also clears
/// <see cref="WorkstreamRow.Status"/>=<c>"stalled"</c> +
/// <see cref="WorkstreamRow.StallReason"/> on any workstream that
/// carries this work item, so the phase board's STALLED pill clears
/// without waiting for the orchestrator's next tick.</para>
/// </summary>
public sealed class BudgetCooldownReconciler : BackgroundService
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> ResumedCounter =
        Meter.CreateCounter<long>("forge.budget.cooldown.resumed");
    private static readonly Counter<long> SkippedCounter =
        Meter.CreateCounter<long>("forge.budget.cooldown.skipped");

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<BudgetCooldownOptions> options;
    private readonly ILogger<BudgetCooldownReconciler> logger;
    private readonly TaskChannel taskChannel;
    private readonly IHubContext<ForgeHub> hub;

    public BudgetCooldownReconciler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BudgetCooldownOptions> options,
        TaskChannel taskChannel,
        IHubContext<ForgeHub> hub,
        ILogger<BudgetCooldownReconciler> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.taskChannel = taskChannel;
        this.hub = hub;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a beat before the first tick so app boot finishes (migrations,
        // seed). One tick before the loop establishes parity with the other
        // reconcilers (CrewQueueWorker's boot-recovery sweep).
        var firstDelay = this.options.CurrentValue.TickInterval;
        try { await Task.Delay(firstDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (this.options.CurrentValue.Enabled)
                {
                    await this.RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "BudgetCooldownReconciler tick errored — continuing");
            }

            try { await Task.Delay(this.options.CurrentValue.TickInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick. Public so tests can drive deterministically without
    /// waiting for the interval timer.
    ///
    /// <para>Scans two row populations:</para>
    /// <list type="number">
    ///   <item><b>CooldownBudget</b> — the new-data path; full cap-raised /
    ///   cooldown-elapsed semantics apply.</item>
    ///   <item><b>Failed with stalled-budget in chain</b> — the legacy /
    ///   carry-forward path. Old code (pre-Phase 51) marked any non-pass
    ///   verdict as Failed; the reconciler couldn't reach those. We pick
    ///   them up here so a single cap-raise PATCH recovers them within
    ///   the same 15s tick as new-data rows. Only the <i>cap-raised</i>
    ///   recovery applies — a Failed row has no cooldown timer, and
    ///   auto-rotating its pair without an operator signal would
    ///   re-burn the same scenario the original failure trapped it in.</item>
    /// </list>
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        // Phase E — fan out per product. Each product's BudgetCooldown
        // recovery is independent; we sum the per-product resume counts.
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var totalResumed = 0;
        var lockObj = new object();
        var now = DateTimeOffset.UtcNow;

        await fanout.ForEachProductAsync(async (db, slug, fct) =>
        {
            // Pull both row sets in one EF query — the ExecutionStatus
            // index (Phase 51 migration) covers both predicates.
            var candidates = await db.WorkItems
                .Where(w => w.ExecutionStatus == ExecutionStatuses.CooldownBudget
                         || w.ExecutionStatus == ExecutionStatuses.Failed)
                .ToListAsync(fct);
            if (candidates.Count == 0) return;

            var perProductResumed = 0;
            foreach (var wi in candidates)
            {
                try
                {
                    // Failed rows must have "budget was the wall" before
                    // we consider them recoverable.
                    if (wi.ExecutionStatus == ExecutionStatuses.Failed
                        && !await this.IsBudgetRecoverableAsync(db, wi.Id, fct))
                    {
                        continue;
                    }
                    var didResume = await this.TryResumeAsync(db, queue, wi, now, fct);
                    if (didResume) perProductResumed++;
                }
                catch (Exception ex)
                {
                    SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "exception"));
                    this.logger.LogWarning(ex, "Cooldown resume failed for product={Slug} wi={Wi} — leaving parked", slug, wi.Id);
                }
            }

            if (perProductResumed > 0)
            {
                lock (lockObj) totalResumed += perProductResumed;
            }
        }, ct);

        return totalResumed;
    }

    /// <summary>
    /// A Failed work item is "budget-recoverable" when its deliverable
    /// chain contains at least one <c>stalled-budget</c> verdict AND no
    /// <c>pass</c> (a pass means it shipped; Failed-after-pass is a
    /// post-delivery problem the budget reconciler must not touch).
    /// Chain-scan (not last-row-only) so rows that hit budget, were
    /// re-attempted, and then failed for a downstream reason are still
    /// recovered when the operator raises the cap.
    /// </summary>
    private async Task<bool> IsBudgetRecoverableAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        // SQLite EF can do this query server-side — both Verdict columns
        // are plain TEXT. AnyAsync short-circuits, so this is cheap even
        // on long deliverable chains.
        var hasPass = await db.Deliverables.AsNoTracking()
            .AnyAsync(d => d.WorkItemId == workItemId && d.Verdict == "pass", ct);
        if (hasPass) return false;
        var hasBudgetStall = await db.Deliverables.AsNoTracking()
            .AnyAsync(d => d.WorkItemId == workItemId && d.Verdict == "stalled-budget", ct);
        return hasBudgetStall;
    }

    private async Task<bool> TryResumeAsync(
        ProductDbContext db,
        ITaskQueueService queue,
        WorkItemRow wi,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Resolve the crew from the most recent deliverable (the only place
        // that knows which crew ran the loop).
        var deliverableRows = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == wi.Id)
            .ToListAsync(ct);
        var lastDeliv = deliverableRows
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        if (lastDeliv is null || string.IsNullOrEmpty(lastDeliv.CrewId))
        {
            // Defensive — a cooldown row without a deliverable shouldn't
            // happen (CrewQueueWorker only parks AFTER a verdict). Treat
            // as a dead end and demote to Failed so the operator sees
            // something actionable instead of an infinite cooldown.
            this.logger.LogWarning(
                "Cooldown row has no deliverable to anchor crew lookup: wi={Wi}. Demoting to Failed.",
                wi.Id);
            wi.ExecutionStatus = ExecutionStatuses.Failed;
            wi.BudgetCooldownUntilUtc = null;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "no-deliverable"));
            return false;
        }

        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == lastDeliv.CrewId, ct);
        if (crew is null)
        {
            this.logger.LogWarning(
                "Cooldown row's crew not found: wi={Wi} crewId={Crew}. Demoting to Failed.",
                wi.Id, lastDeliv.CrewId);
            wi.ExecutionStatus = ExecutionStatuses.Failed;
            wi.BudgetCooldownUntilUtc = null;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "no-crew"));
            return false;
        }

        // Resume condition A — cap raised. Compare cost against the
        // crew's CURRENT MaxTaskBudgetUsd. The cost we compare is
        // WINDOWED — sum agent_usage rows since WorkItemRow.BudgetWindowStartUtc
        // (set on prior cooldown-elapsed resume). Null window means
        // "lifetime"; once a cooldown elapses the meter zeroes so
        // recovered tasks aren't locked out by pre-window spend.
        var windowStart = wi.BudgetWindowStartUtc;
        var usageRows = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == wi.Id)
            .ToListAsync(ct);
        var windowedCost = windowStart is DateTimeOffset ws
            ? usageRows.Where(u => u.StartedAt >= ws).Sum(u => u.CostUsd)
            : usageRows.Sum(u => u.CostUsd);
        var cumulativeCost = windowedCost; // local alias for the existing log/event payload
        var currentCap = crew.MaxTaskBudgetUsd;
        var capRaisedRecovery = currentCap is decimal cap && windowedCost < cap;
        this.logger.LogInformation(
            "BudgetCooldownReconciler eval wi={Wi} status={Status} crew={Crew} cost={Cost} cap={Cap} "
            + "capRaisedRecovery={CapRaised} cooldownUntil={Until}",
            wi.Id, wi.ExecutionStatus, crew.Id,
            cumulativeCost.ToString("0.00"),
            currentCap?.ToString("0.00") ?? "(null)",
            capRaisedRecovery,
            wi.BudgetCooldownUntilUtc?.ToString("o") ?? "(null)");

        // Resume condition B — cooldown elapsed. We force a resume so a
        // budget-stalled task can't sit forever even if the operator
        // never raises the cap (e.g. they left for the weekend); the
        // pair-rotation path runs a fresh attempt within the same
        // existing cap.
        //
        // Phase 51 follow-up: legacy Failed rows from before this fix
        // never had BudgetCooldownUntilUtc set (CrewQueueWorker used to
        // route every non-pass verdict straight to Failed). For those,
        // derive an equivalent "window" from the most recent
        // deliverable's CreatedAt — if MaxWait has elapsed since the
        // budget stall was recorded, treat it as cooldown-elapsed and
        // rotate the pair. Without this branch, the user's stage 3.1
        // (sitting in Failed for hours) couldn't auto-recover by time
        // — only by an explicit cap raise.
        var maxWait = this.options.CurrentValue.MaxWait;
        var cooldownElapsed = wi.BudgetCooldownUntilUtc is DateTimeOffset until
            ? now >= until
            : (string.Equals(wi.ExecutionStatus, ExecutionStatuses.Failed, StringComparison.Ordinal)
                && lastDeliv.CreatedAt + maxWait <= now);

        if (!capRaisedRecovery && !cooldownElapsed)
        {
            // Still parked. Common path — no action needed.
            return false;
        }

        var reason = capRaisedRecovery ? "cap-raised" : "cooldown-elapsed";

        // Snapshot the prior pair BEFORE we mutate the work item so the
        // emitted event always carries it (useful for both reason codes —
        // tells the UI which generator/evaluator just stalled).
        var priorPair = ParsePair(wi.LastPairAgentIdsJson);

        // Clear the cooldown markers. For cap-raised we also clear
        // LastPairAgentIdsJson so the picker uses its prior-agent
        // preference (continuity). For cooldown-elapsed we keep it set
        // so the picker excludes the prior pair (rotation), AND we
        // mark the start of a new budget window so the cost meter
        // resets to $0.
        wi.ExecutionStatus = ExecutionStatuses.Queued;
        wi.BudgetCooldownUntilUtc = null;
        if (!capRaisedRecovery)
        {
            // cooldown-elapsed path — reset the budget window so the
            // next attempt has a fresh $0 cap against the current
            // MaxTaskBudgetUsd. Pre-window spend stays in the ledger
            // for crew salary roll-ups but stops counting against the
            // per-task cap.
            wi.BudgetWindowStartUtc = now;
        }
        if (capRaisedRecovery)
        {
            wi.LastPairAgentIdsJson = null;
        }
        else if (string.IsNullOrWhiteSpace(wi.LastPairAgentIdsJson))
        {
            // Phase 51 follow-up: cooldown-elapsed on a legacy Failed row
            // (no LastPairAgentIdsJson written by old code). Backfill
            // from the most recent deliverable so the claim picker has a
            // pair to exclude — otherwise rotation degenerates to
            // round-robin and may re-pick the same generator.
            var fallbackPair = new[]
            {
                lastDeliv.GeneratorAgentId ?? string.Empty,
                lastDeliv.EvaluatorAgentId ?? string.Empty,
            };
            wi.LastPairAgentIdsJson = JsonSerializer.Serialize(fallbackPair);
        }
        wi.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        // Clear any workstream's stalled badge that points at this work
        // item so the phase board's STALLED pill drops on the next UI
        // refresh — without this the operator sees the chip still red
        // until the orchestrator's next tick re-evaluates the row.
        await this.ClearWorkstreamStallAsync(db, wi.Id, ct);

        // Re-enqueue. EnqueueAsync flips ExecutionStatus = Queued again
        // (idempotent) and inserts the queue row that CrewQueueWorker's
        // claim sweep will pick up. preferredAgentId is left null on the
        // rotation path so the picker's diversity resolver chooses; on
        // cap-raised we deliberately pass the prior generator so the
        // continuity branch in SweepClaimsAsync (the "prefer prior agent"
        // block) keeps the same pair.
        string? preferred = capRaisedRecovery ? lastDeliv.GeneratorAgentId : null;
        var queueRow = await queue.EnqueueAsync(
            wi.Id, "crew", crew.Id, ct,
            kind: TaskQueueKinds.Work,
            preferredAgentId: preferred);

        ResumedCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
        this.logger.LogInformation(
            "BudgetCooldownReconciler resumed wi={Wi} crew={Crew} reason={Reason} cost={Cost} cap={Cap} preferred={Preferred}",
            wi.Id, crew.Id, reason,
            cumulativeCost.ToString("0.00"),
            currentCap?.ToString("0.00") ?? "(null)",
            preferred ?? "(rotate)");

        await this.taskChannel.EmitAsync(wi.Id, "task.budget-resumed", new
        {
            crewId = crew.Id,
            reason,
            cumulativeCostUsd = cumulativeCost,
            currentCapUsd = currentCap,
            queueId = queueRow.Id,
            preferredAgentId = preferred,
            priorPair,
        }, ct: ct);

        await this.hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = queueRow }, ct);

        return true;
    }

    private async Task ClearWorkstreamStallAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        // SQLite EF can't translate JSON-array contains; materialize the
        // candidate set (every stalled workstream) and filter in memory.
        // The set is small (workstreams scale ~hundreds, not millions).
        //
        // Match heuristics, in priority order:
        //   1. WorkstreamRow.Id == workItemId — true for the spawn-flow
        //      case where the workstream IS the work item (stage-level
        //      rows: WorkItemRow and WorkstreamRow share the same id).
        //      This was the missed case for the failover-cluster:XRAFT
        //      stage 3.1 stall — the work item was the workstream itself.
        //   2. workItemId is an element of CardsJson — the legacy
        //      AutoLinkToWorkstreamAsync path where step-level work items
        //      are listed on the workstream's CardsJson array.
        // Both checked because the relationship isn't uniform across
        // spawn vs ad-hoc-claim code paths.
        var stalled = await db.Workstreams
            .Where(w => w.Status == WorkstreamStatuses.Stalled)
            .ToListAsync(ct);
        var touched = 0;
        foreach (var w in stalled)
        {
            var matches = string.Equals(w.Id, workItemId, StringComparison.Ordinal);
            if (!matches)
            {
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrEmpty(w.CardsJson) ? "[]" : w.CardsJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String
                                && string.Equals(el.GetString(), workItemId, StringComparison.Ordinal))
                            {
                                matches = true;
                                break;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // CardsJson malformed — skip; the operator can clean
                    // it up via the workstream detail panel.
                }
            }
            if (!matches) continue;

            w.Status = "active";
            w.StallReason = null;
            // WorkstreamRow.LastActive is a Unix epoch seconds double (the
            // prototype renders it client-side as "Xm ago"); mirror the
            // existing writer convention.
            w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            touched++;
        }
        if (touched > 0) await db.SaveChangesAsync(ct);
    }

    private static string[]? ParsePair(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch
        {
            return null;
        }
    }
}
