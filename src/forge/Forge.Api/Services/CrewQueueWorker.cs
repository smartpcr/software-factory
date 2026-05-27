// -----------------------------------------------------------------------
// <copyright file="CrewQueueWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Phase 17.4 — hosted service driving the four sweeps over
/// <see cref="TaskQueueRow"/>:
///   1) ack-timeout — auto-ack rows whose 10s SLO elapsed.
///   2) claim — pop the head of every crew's queue when it has capacity.
///   3) strain detection — emit <c>queue.strained</c> when depth or ETA crosses thresholds.
///   4) abandoned recovery — revert <c>picked-up</c> rows older than 24 h to <c>queued</c>.
/// <para>
/// Boot scan runs all four sweeps once before the timer takes over (Q17.4)
/// so SLO breaches that elapsed during downtime catch up on the first tick.
/// </para>
/// </summary>
public sealed class CrewQueueWorker : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Phase 24 Q24.13 — toolset registered for <c>kind='comment-response'</c>
    /// generator runs. <c>open_pr</c> is intentionally NOT included — comment
    /// responses write against the existing PR. <c>post_pr_comment_reply</c>
    /// is the new tool surfaced for the must-reply path.
    /// </summary>
    internal static readonly string[] CommentResponseAllowedTools =
    {
        "read_file",
        "write_file",
        "run_command",
        "git_commit",
        "git_push",
        "post_pr_comment_reply",
    };

    /// <summary>
    /// Phase 27 Q27.10 — toolset registered for <c>kind='build-fix'</c>
    /// generator runs. Same shape as comment-response — same write/push
    /// surface, same reply tool, no <c>open_pr</c> (build-fix pushes to the
    /// existing branch). <c>read_file</c> is path-validated against both
    /// the worktree and the artifact root by the runner.
    /// </summary>
    internal static readonly string[] BuildFixAllowedTools =
    {
        "read_file",
        "write_file",
        "run_command",
        "git_commit",
        "git_push",
        "post_pr_comment_reply",
    };

    /// <summary>
    /// Evaluator runs across all kinds — read-only inspection only. No
    /// commit / push / reply tools because the evaluator scores the
    /// deliverable, it never acts on the PR.
    /// </summary>
    internal static readonly string[] EvaluatorAllowedTools =
    {
        "read_file",
        "run_command",
    };

    /// <summary>
    /// Plan tasks — architecture agents produce design docs and WIT trees.
    /// <c>write_file</c> is allowed (server-side path enforcement limits
    /// writes to <c>docs/specs/</c>); <c>run_command</c> is intentionally
    /// excluded so plan agents cannot modify source code indirectly.
    /// </summary>
    internal static readonly string[] PlanAllowedTools =
    {
        "read_file",
        "write_file",
        "git_commit",
        "git_push",
    };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly TaskChannel taskChannel;
    private readonly IHubContext<ForgeHub> hub;
    private readonly AgentSlotService slots;
    private readonly ILogger<CrewQueueWorker> logger;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<BudgetCooldownOptions> cooldownOptions;

    /// <summary>Per-crew strain debounce — when did we last emit a signal?</summary>
    private readonly ConcurrentDictionary<string, bool> strainArmed = new();

    public static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> EnqueuedCounter = Meter.CreateCounter<long>("forge.queue.enqueued");
    private static readonly Counter<long> AckTimeoutCounter = Meter.CreateCounter<long>("forge.queue.ack_timeout");
    private static readonly Counter<long> StrainFiredCounter = Meter.CreateCounter<long>("forge.queue.strain.fired");
    private static readonly Histogram<double> AckLatencyMs = Meter.CreateHistogram<double>("forge.queue.ack_latency_ms", "ms");
    private static readonly Histogram<double> DwellMs = Meter.CreateHistogram<double>("forge.queue.dwell_ms", "ms");

    private CancellationTokenSource? cts;
    private Task? loop;

    /// <summary>
    /// Set to <c>true</c> in <see cref="StopAsync"/> before the CTS is
    /// cancelled. Fire-and-forget agent tasks check this flag so they can
    /// distinguish a host shutdown (leave the row in <c>picked-up</c> for
    /// boot recovery) from a genuine runtime failure (mark as failed).
    /// </summary>
    private volatile bool shuttingDown;

    // Interlocked latch: 0 = not run, 1 = ran. Forge.Api.Tests' WebApplicationFactory
    // hosting the DI graph alongside the prod server (or any future code path that
    // accidentally re-invokes this sweep) caused 4036 spurious boot-recovery rows
    // in task_queue_history — every claim was racing with a recovery reset every
    // few hundred ms, ratio ~1:1 against `claimed`. After-the-fact: prevent any
    // post-boot call from doing work; log so the second-caller is identifiable.
    private int bootRecoveryLatch;

    // Tracks the fire-and-forget agent Task.Runs spawned in SweepClaimsAsync.
    // StopAsync awaits these (with a short timeout) before letting the DI
    // container dispose the scope factory — without this, an agent task that
    // outlives its parent host throws ObjectDisposedException reaching for a
    // disposed IServiceScope, which crashes the test host process even though
    // the unobserved-task handler calls SetObserved(). Bounded so a stuck
    // agent (real LLM call hung on the network) can't deadlock shutdown.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> runningAgentTasks = new();

    private readonly WorktreeChangeFeed worktreeFeed;

    public CrewQueueWorker(
        IServiceScopeFactory scopeFactory,
        TaskChannel taskChannel,
        IHubContext<ForgeHub> hub,
        WorktreeChangeFeed worktreeFeed,
        AgentSlotService slots,
        ILogger<CrewQueueWorker> logger,
        Microsoft.Extensions.Options.IOptionsMonitor<BudgetCooldownOptions> cooldownOptions)
    {
        this.scopeFactory = scopeFactory;
        this.taskChannel = taskChannel;
        this.hub = hub;
        this.worktreeFeed = worktreeFeed;
        this.slots = slots;
        this.logger = logger;
        this.cooldownOptions = cooldownOptions;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.loop = Task.Run(async () =>
        {
            // Boot recovery: requeue any items left in picked-up from the
            // previous server process (agent died with the old host).
            try { await this.SweepBootRecoveryAsync(this.cts.Token); }
            catch (Exception ex) { this.logger.LogDebug(ex, "CrewQueueWorker boot recovery errored"); }
            // Boot crash recovery: re-queue items that the previous process
            // marked failed during its death throes (connection-lost race).
            try { await this.SweepBootCrashRecoveryAsync(this.cts.Token); }
            catch (Exception ex) { this.logger.LogDebug(ex, "CrewQueueWorker boot crash recovery errored"); }
            // Boot scan once before the timer.
            try { await this.RunOnceAsync(this.cts.Token); }
            catch (Exception ex) { this.logger.LogDebug(ex, "CrewQueueWorker boot scan errored"); }
            await this.LoopAsync(this.cts.Token);
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.shuttingDown = true;
        this.cts?.Cancel();
        if (this.loop is not null)
        {
            try { await this.loop; } catch { /* shutdown */ }
        }

        // Drain in-flight fire-and-forget agent tasks before allowing the
        // DI container to dispose our dependencies. Bounded at 5s so a
        // genuinely-stuck agent (real LLM call, hung HTTP) can't deadlock
        // shutdown. In-process tests with the local-stub backend finish
        // in <1s so this rarely waits the full timeout.
        var inflight = this.runningAgentTasks.Keys.ToArray();
        if (inflight.Length > 0)
        {
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(inflight),
                    Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch { /* swallow — we tried */ }
        }
    }

    public void Dispose() => this.cts?.Dispose();

    /// <summary>
    /// Run all five sweeps once. Public so tests can drive it deterministically.
    /// Each sweep is isolated behind its own try/catch — a throw in one must
    /// NOT prevent the others from running. (Without this, a SQLite EF
    /// translation error in SweepClaims used to silently kill the whole
    /// chain, leaving SweepLiveBadgeReconciliationAsync — the last sweep —
    /// unable to heal stuck "live" badges.) Failures log at Warning so they
    /// surface in normal logs instead of being buried at Debug.
    /// </summary>
    public async Task<WorkerTickResult> RunOnceAsync(CancellationToken ct)
    {
        // Phase E — fan out per product. Every sweep below opens its own
        // child DI scope and resolves ProductDbContext fresh. The
        // AsyncLocal slug pushed by ProductFanout flows through every
        // child scope, so each sweep automatically targets the active
        // product's tables (task_queue, work_items, deliverables, etc.)
        // without any further refactoring of the sweep bodies themselves.
        using var outerScope = this.scopeFactory.CreateScope();
        var fanout = outerScope.ServiceProvider.GetRequiredService<ProductFanout>();
        var ackTotal = 0;
        var claimsTotal = 0;
        var strainTotal = 0;
        var recoveredTotal = 0;
        var reconciledTotal = 0;
        var allSkips = new List<ClaimSkip>();
        var lockObj = new object();

        await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
        {
            var ack = await this.SafeSweepAsync("SweepAckTimeoutsAsync", () => this.SweepAckTimeoutsAsync(fct), fct);
            var claimsRes = await this.SafeSweepAsync(
                "SweepClaimsAsync",
                async () => { var r = await this.SweepClaimsAsync(fct); return (object?)r; },
                fct,
                fallback: (object?)(0, (IReadOnlyList<ClaimSkip>)Array.Empty<ClaimSkip>()));
            var (cl, sk) = ((int, IReadOnlyList<ClaimSkip>))claimsRes!;
            var strain = await this.SafeSweepAsync("SweepStrainAsync", () => this.SweepStrainAsync(fct), fct);
            var rec = await this.SafeSweepAsync("SweepAbandonedAsync", () => this.SweepAbandonedAsync(fct), fct);
            var recon = await this.SafeSweepAsync("SweepLiveBadgeReconciliationAsync", () => this.SweepLiveBadgeReconciliationAsync(fct), fct);
            lock (lockObj)
            {
                ackTotal += ack;
                claimsTotal += cl;
                strainTotal += strain;
                recoveredTotal += rec;
                reconciledTotal += recon;
                allSkips.AddRange(sk);
            }
        }, ct);

        return new WorkerTickResult(ackTotal, claimsTotal, strainTotal, recoveredTotal, allSkips, reconciledTotal);
    }

    private async Task<int> SafeSweepAsync(string name, Func<Task<int>> sweep, CancellationToken ct)
    {
        try { return await sweep(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Sweep {Name} threw — continuing with remaining sweeps", name);
            return 0;
        }
    }

    private async Task<object?> SafeSweepAsync(string name, Func<Task<object?>> sweep, CancellationToken ct, object? fallback = null)
    {
        try { return await sweep(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Sweep {Name} threw — continuing with remaining sweeps", name);
            return fallback;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var jitter = new Random();
        while (!ct.IsCancellationRequested)
        {
            try { await this.RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { this.logger.LogDebug(ex, "CrewQueueWorker tick errored"); }
            var delay = PollInterval + TimeSpan.FromMilliseconds(jitter.Next(-1000, 1000));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ── Boot recovery — requeue picked-up items from previous run ─────────
    private async Task<int> SweepBootRecoveryAsync(CancellationToken ct)
    {
        // Single-shot per process. The intended caller is StartAsync exactly
        // once. A second invocation (test factory hosting this DI graph in the
        // same process as the prod server, future refactor wiring this into
        // the timer loop, etc.) would race with normal claims and produce a
        // boot-recovery → claimed → boot-recovery loop on every queue row.
        if (Interlocked.CompareExchange(ref this.bootRecoveryLatch, 1, 0) != 0)
        {
            this.logger.LogWarning(
                "SweepBootRecoveryAsync called more than once per process — second call ignored. "
                + "Stack: {Stack}",
                Environment.StackTrace);
            return 0;
        }

        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var stale = await db.TaskQueue
            .Where(r => r.State == TaskQueueStates.PickedUp)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        this.logger.LogInformation(
            "Boot recovery: resetting {Count} picked-up items to queued",
            stale.Count);

        foreach (var row in stale)
        {
            row.State = TaskQueueStates.Queued;
            row.PickedUpByAgent = null;
            row.PickedUpAt = null;
            row.LastUpdatedAt = DateTimeOffset.UtcNow;

            db.TaskQueueHistory.Add(new TaskQueueHistoryRow
            {
                QueueId = row.Id,
                Kind = "boot-recovery",
                FromValue = TaskQueueStates.PickedUp,
                ToValue = TaskQueueStates.Queued,
                At = DateTimeOffset.UtcNow,
            });

            // Phase 19 — re-queued items are back to "queued" status.
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
            if (wi is not null)
            {
                wi.ExecutionStatus = ExecutionStatuses.Queued;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await this.taskChannel.EmitAsync(row.WorkItemId, "task.boot-recovery", new
            {
                queueId = row.Id,
                crewId = row.RoutedToCrewId,
            }, crewId: row.RoutedToCrewId, ct: ct);
        }
        await db.SaveChangesAsync(ct);

        // Fan out workitem.changed for each row whose ExecutionStatus we touched.
        foreach (var row in stale)
        {
            var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
            if (wi is not null) await this.BroadcastWorkItemChangedAsync(wi, "exec-requeued", ct, db);
        }
        return stale.Count;
    }

    // ── Boot crash recovery — re-queue WIs marked failed by a prior crash ─
    /// <summary>
    /// Handles work items that were marked <c>failed</c> during the previous
    /// process's death throes (e.g. ChildProcessTracker killed the copilot
    /// CLI, the JSON-RPC error was caught, and the generic handler marked
    /// the queue row completed + WI failed before the process exited).
    /// <para>
    /// <see cref="SweepBootRecoveryAsync"/> only finds <c>picked-up</c> rows,
    /// but the crash path completes them. This sweep finds those orphaned
    /// items and re-queues them so execution resumes with no human action.
    /// </para>
    /// </summary>
    private async Task<int> SweepBootCrashRecoveryAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        // Find work items stuck in "failed" execution status whose ADO/GH
        // state is still open (active/proposed) — these are crash victims,
        // not items that the lead triaged or that reached a terminal verdict.
        var failedWis = await db.WorkItems
            .Where(w => w.ExecutionStatus == ExecutionStatuses.Failed
                        && (w.State == "active" || w.State == "proposed"))
            .ToListAsync(ct);
        if (failedWis.Count == 0) return 0;

        var recovered = 0;
        foreach (var wi in failedWis)
        {
            // Check deliverables — only resume if no terminal verdict exists.
            var delivCandidates = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == wi.Id)
                .ToListAsync(ct);
            var lastDeliv = delivCandidates
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
                .FirstOrDefault();

            // Terminal verdicts (pass/reject) mean the loop finished
            // naturally — don't re-run.
            if (lastDeliv is not null && lastDeliv.Verdict is "pass" or "reject")
                continue;

            // Determine the crew to re-queue into: last deliverable's crew,
            // or the most recent completed queue row's crew.
            var crewId = lastDeliv?.CrewId;
            if (string.IsNullOrEmpty(crewId))
            {
                var lastRow = (await db.TaskQueue.AsNoTracking()
                    .Where(r => r.WorkItemId == wi.Id)
                    .ToListAsync(ct))
                    .OrderByDescending(r => r.CompletedAt ?? r.LastUpdatedAt)
                    .FirstOrDefault();
                crewId = lastRow?.RoutedToCrewId;
            }
            if (string.IsNullOrEmpty(crewId)) continue;

            // Guard: skip if there's already an open queue row (EnqueueAsync
            // would return the existing one, but we'd still emit confusing events).
            var openStates = TaskQueueStates.OpenStates.ToList();
            var hasOpen = await db.TaskQueue.AsNoTracking()
                .AnyAsync(r => r.WorkItemId == wi.Id && openStates.Contains(r.State), ct);
            if (hasOpen) continue;

            try
            {
                var newRow = await queue.EnqueueAsync(wi.Id, "crew", crewId, ct);
                this.logger.LogInformation(
                    "Boot crash recovery: re-queued wi={Wi} into crew={Crew} (lastIter={Iter} verdict={Verdict})",
                    wi.Id, crewId, lastDeliv?.Iter ?? -1, lastDeliv?.Verdict ?? "(none)");
                await this.taskChannel.EmitAsync(wi.Id, "task.boot-crash-recovery", new
                {
                    queueId = newRow.Id,
                    crewId,
                    resumeFromIter = lastDeliv?.Iter,
                    priorVerdict = lastDeliv?.Verdict,
                }, crewId: crewId, ct: ct);
                var refreshed = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wi.Id, ct);
                if (refreshed is not null) await this.BroadcastWorkItemChangedAsync(refreshed, "exec-crash-recovered", ct, db);
                recovered++;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Boot crash recovery: EnqueueAsync failed for wi={Wi}", wi.Id);
            }
        }
        return recovered;
    }

    // ── Sweep 1 — auto-ack timeouts ───────────────────────────────────────
    private async Task<int> SweepAckTimeoutsAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var now = DateTimeOffset.UtcNow;
        var stale = await db.TaskQueue.AsNoTracking()
            .Where(r => r.State == TaskQueueStates.AwaitingAck && r.AckRequiredBy != null)
            .ToListAsync(ct);
        // SQLite EF can't compare DateTimeOffset in a Where clause reliably;
        // filter client-side after the index narrows by state.
        stale = stale.Where(r => r.AckRequiredBy <= now).ToList();

        var fired = 0;
        foreach (var row in stale)
        {
            var changed = await queue.AcknowledgeAsync(row.Id, byUserId: null, auto: true, ct);
            if (changed is null) continue;
            fired++;
            AckTimeoutCounter.Add(1, new KeyValuePair<string, object?>("targetKind", row.TargetKind));
            AckLatencyMs.Record((now - row.EnqueuedAt).TotalMilliseconds, new KeyValuePair<string, object?>("auto", true));
            await this.BroadcastQueueChangedAsync(changed, "acked", ct);
        }
        return fired;
    }

    // ── Sweep 2 — claim head of crew queues ───────────────────────────────
    private async Task<(int Claimed, IReadOnlyList<ClaimSkip> Skips)> SweepClaimsAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var crewsWithQueue = await db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == "crew" && r.State == TaskQueueStates.Queued)
            .Select(r => r.TargetId)
            .Distinct()
            .ToListAsync(ct);

        var claimed = 0;
        var skips = new List<ClaimSkip>();
        // Phase 47 — load currently-busy agents once per tick. We mutate
        // this set as we pick agents within the tick so a later crew's
        // claim sees the just-picked generator/evaluator as busy and
        // diversifies against them.
        var busyAgents = await BusyAgentsHelper.LoadBusyAgentsAsync(control, ct);
        foreach (var crewId in crewsWithQueue)
        {
            var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
            if (crew is null)
            {
                skips.Add(new ClaimSkip(crewId, "crew-not-found", null));
                continue;
            }

            // Phase 17 — the queue worker does NOT honor the Phase 14
            // schedule gate. The gate was specified for the human-driven
            // plan/code dispatch verbs (Q14.1: refuse new pickups outside
            // 9-9-6); the queue is the operator's "park and route" surface
            // and should keep draining regardless of the wall clock — the
            // operator already controls timing by deciding when to assign.
            // Crews that want a hard schedule cap can set ScheduleEnabled
            // and add their own gate to the runner; we don't impose one.

            // Capacity check — count current picked-up rows for the crew.
            var inFlight = await db.TaskQueue.AsNoTracking()
                .CountAsync(r => r.RoutedToCrewId == crewId && r.State == TaskQueueStates.PickedUp, ct);
            if (inFlight >= crew.MaxConcurrent)
            {
                skips.Add(new ClaimSkip(crewId, "capacity-full", $"inFlight={inFlight} cap={crew.MaxConcurrent}"));
                continue;
            }

            // Pick a member from the roster (round-robin over non-lead members).
            var allMembers = await control.CrewMembers.AsNoTracking()
                .Where(m => m.CrewId == crewId && m.AgentId != crew.Lead)
                .Select(m => m.AgentId)
                .ToListAsync(ct);

            // Phase 18.6 (Q18.3) — filter members through the schedule gate
            // before round-robin so off-shift members don't get a wedged
            // claim. The lead becomes a fallback when zero members are
            // currently on shift (Sunday / 03:00-Tuesday / sole-PO case).
            // The "exclude the lead" rule from Phase 16.7 stays as the
            // *preference*, not the *constraint* — the lead is in the pool
            // only as a Sunday / off-band fallback.
            var nowUtc = DateTimeOffset.UtcNow;
            var onShiftMembers = allMembers
                .Where(id => ScheduleGate.EvaluateAgent(crew, id, nowUtc).Verdict != ScheduleGate.Verdict.OffShift)
                .ToList();

            // SQLite EF cannot ORDER BY DateTimeOffset in-query — materialize
            // the queued candidates and sort in memory (mirrors
            // TaskQueueService.ClaimNextAsync at line 184).
            string? picked = null;
            string? pinReason = null;
            var headRow = (await db.TaskQueue.AsNoTracking()
                .Where(r => r.RoutedToCrewId == crewId && r.State == TaskQueueStates.Queued)
                .ToListAsync(ct))
                .OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt)
                .FirstOrDefault();

            // Q24.14 — when the head row carries PreferredAgentId, we are pinned to
            // that agent. Round-robin to a fresh member is *not* an acceptable
            // fallback for pinned rows; the chain is preferred → lead → blocked.
            // The pin applies to any row with PreferredAgentId set (today this
            // is comment-response; the same column will carry build-fix's pin
            // under Q27.7 when that phase ships).
            if (headRow is not null && !string.IsNullOrEmpty(headRow.PreferredAgentId))
            {
                var preferredId = headRow.PreferredAgentId!;
                var preferredAgent = await control.Agents.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == preferredId, ct);
                var inCrew = await control.CrewMembers.AsNoTracking()
                    .AnyAsync(m => m.CrewId == crewId && m.AgentId == preferredId, ct);

                if (preferredAgent is null)
                {
                    pinReason = "preferred-agent-missing";
                }
                else if (!inCrew && !string.Equals(preferredId, crew.Lead, StringComparison.Ordinal))
                {
                    pinReason = "preferred-agent-not-in-crew";
                }
                else if (string.Equals(preferredAgent.State, "paused", StringComparison.OrdinalIgnoreCase))
                {
                    pinReason = "preferred-agent-paused";
                }
                else
                {
                    picked = preferredId;
                    pinReason = "original-agent-pin";
                }

                // Fallback to lead per Q24.14 step 2 — the lead is the next-best
                // continuity holder because they routed the original work item.
                // Lead is exempt from the schedule gate per Q18.3.
                if (string.IsNullOrEmpty(picked) && !string.IsNullOrEmpty(crew.Lead))
                {
                    var lead = await control.Agents.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == crew.Lead, ct);
                    if (lead is not null && !string.Equals(lead.State, "paused", StringComparison.OrdinalIgnoreCase))
                    {
                        picked = crew.Lead;
                    }
                }

                // Q24.14 step 3 — neither pin nor lead available. Block the row;
                // the operator's manual-assign endpoint (Phase 22.4) is the
                // unblocker. Round-robin to a fresh member is never automatic.
                if (string.IsNullOrEmpty(picked))
                {
                    ForgeMetrics.NoOriginalAgentBlocked.Add(1,
                        new KeyValuePair<string, object?>("crewId", crewId),
                        new KeyValuePair<string, object?>("reason", pinReason ?? "unknown"));
                    await queue.BlockAsync(headRow.Id, "comment-response.no_original_agent",
                        reason: pinReason, ct: ct);
                    skips.Add(new ClaimSkip(crewId, "pinned-blocked", $"preferred={preferredId} reason={pinReason} lead={crew.Lead ?? "(null)"}"));
                    continue;
                }
            }

            // Phase 51 — pair-rotation on budget cooldown-elapsed resume.
            // When BudgetCooldownReconciler resumes a stalled-budget row
            // WITHOUT a cap raise (the 10m timer fired), it leaves
            // WorkItemRow.LastPairAgentIdsJson populated with the prior
            // (generatorId, evaluatorId) so the next claim rotates to a
            // fresh pair. We honor it by (a) treating those IDs as busy
            // for THIS pick AND (b) skipping the "prefer prior agent"
            // continuity branch below. The cap-raised path clears
            // LastPairAgentIdsJson so the continuity branch runs normally
            // (same pair, more budget).
            string[]? excludeForRotation = null;
            if (headRow is not null)
            {
                var headWi = await db.WorkItems.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.Id == headRow.WorkItemId, ct);
                if (headWi is not null && !string.IsNullOrWhiteSpace(headWi.LastPairAgentIdsJson))
                {
                    try
                    {
                        excludeForRotation = System.Text.Json.JsonSerializer.Deserialize<string[]>(headWi.LastPairAgentIdsJson);
                    }
                    catch (System.Text.Json.JsonException) { /* malformed — skip rotation */ }
                }
                // Phase 52 — also union the cumulative PriorPairsJson into
                // busyAgents. Every pair that's been judged sloppy on this
                // work item is excluded permanently (for the lifetime of
                // the work item), not just the most-recent pair. This is
                // distinct from Phase 51's LastPairAgentIdsJson, which is
                // a one-rotation hint cleared on the next successful pick.
                if (headWi is not null && !string.IsNullOrWhiteSpace(headWi.PriorPairsJson))
                {
                    try
                    {
                        var priorPairs = System.Text.Json.JsonSerializer.Deserialize<List<string[]>>(headWi.PriorPairsJson);
                        if (priorPairs is not null)
                        {
                            foreach (var p in priorPairs)
                            {
                                if (p is null) continue;
                                foreach (var id in p)
                                {
                                    if (!string.IsNullOrEmpty(id)) busyAgents.Add(id);
                                }
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException) { /* malformed — skip exclusion */ }
                }
            }
            if (excludeForRotation is not null)
            {
                foreach (var id in excludeForRotation)
                {
                    if (!string.IsNullOrEmpty(id)) busyAgents.Add(id);
                }
            }

            // Phase 51 — when the lead is in the rotation exclusion set we
            // must NOT let it back in via the empty-roster fallback inside
            // PairDiversityResolver.PickGenerator (Q18.3 24×7 exemption).
            // That fallback ignores busyAgents by design — without this
            // override a budget-stalled task whose generator WAS the lead
            // would re-claim back onto the lead the moment onShiftMembers
            // is empty (e.g., off-band hours, lightly-configured test crews).
            // Suppress the lead for this single claim; the row falls back
            // to "no-agent-available" and waits for the next tick when a
            // non-excluded member is on shift.
            string? leadForPick = crew.Lead;
            if (excludeForRotation is not null
                && !string.IsNullOrEmpty(leadForPick)
                && Array.Exists(excludeForRotation, id => string.Equals(id, leadForPick, StringComparison.Ordinal)))
            {
                leadForPick = null;
            }

            // Resume support — when a work item already has deliverables (boot-
            // recovery just put it back on the queue), prefer the same agent
            // that was driving it before. Without pinning, every restart spins
            // up a fresh round-robin pick and the live transcript looks like a
            // brand-new task instead of a continuation.
            // Skipped when excludeForRotation is set: the budget reconciler
            // explicitly wants a fresh pair, not continuity.
            if (string.IsNullOrEmpty(picked) && headRow is not null && excludeForRotation is null)
            {
                var priorAgent = (await db.Deliverables.AsNoTracking()
                    .Where(d => d.WorkItemId == headRow.WorkItemId)
                    .ToListAsync(ct))
                    .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
                    .Select(d => d.GeneratorAgentId)
                    .FirstOrDefault();
                // Honor the prior agent only if they're currently on shift
                // (or are the lead, which is always on shift). Without this,
                // a resume after midnight would hand the work back to a member
                // who is now off-shift.
                if (!string.IsNullOrEmpty(priorAgent)
                    && (onShiftMembers.Contains(priorAgent)
                        || string.Equals(priorAgent, crew.Lead, StringComparison.Ordinal)))
                {
                    picked = priorAgent;
                }
            }
            // Phase 47 + LRU — when not pinned/resumed above, pick the
            // least-recently-used (or never-used) free member so successive
            // claims rotate across the roster instead of re-selecting the
            // same generator. Recency comes from the cost ledger.
            if (string.IsNullOrEmpty(picked))
            {
                var genLastActive = await BusyAgentsHelper.LoadLastActiveAsync(control, onShiftMembers, ct);
                picked = ResolveDiverseGenerator(onShiftMembers, busyAgents, leadForPick, genLastActive);
            }

            // Phase 51 — once the rotation pick succeeds, clear the exclusion
            // semaphore on the work item so subsequent ticks (e.g. boot
            // recovery after this run) don't keep rotating away from the
            // original pair. The cleared value is committed alongside the
            // rest of the claim's writes below.
            if (excludeForRotation is not null && !string.IsNullOrEmpty(picked))
            {
                var headWiLive = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == headRow!.WorkItemId, ct);
                if (headWiLive is not null)
                {
                    headWiLive.LastPairAgentIdsJson = null;
                }
            }

            // Phase 18.6 (Q18.3) — when zero members are on-shift AND there's
            // no lead to fall back on, picked is null. Skip cleanly rather
            // than calling ClaimNextAsync(crewId, null), which would CAS the
            // row to picked-up with PickedUpByAgent=null and then run the
            // agent runner against a null agent id.
            // Phase 47 — `picked` is also null when every roster member is
            // currently busy on another invocation (the "no-free-pair-member"
            // case). Skip with the specific reason so the audit trail
            // distinguishes capacity-by-roster from capacity-by-cap.
            if (string.IsNullOrEmpty(picked))
            {
                var reason = onShiftMembers.Count > 0 ? "no-free-pair-member" : "no-agent-available";
                skips.Add(new ClaimSkip(crewId, reason, $"members={allMembers.Count} onShift={onShiftMembers.Count} busy={busyAgents.Count} lead={crew.Lead ?? "(null)"}"));
                continue;
            }

            var row = await queue.ClaimNextAsync(crewId, picked, ct);
            if (row is null)
            {
                skips.Add(new ClaimSkip(crewId, "claim-cas-failed", $"members={allMembers.Count} onShift={onShiftMembers.Count} lead={crew.Lead ?? "(null)"} picked={picked ?? "(null)"}"));
                continue;
            }

            // Q24.14 — record the pin reason on the queue row + history payload
            // so the audit trail is loud about why round-robin was skipped.
            if (!string.IsNullOrEmpty(pinReason))
            {
                row.PinReason = pinReason;
                await db.SaveChangesAsync(ct);
            }
            claimed++;

            // Phase 47 — mark the picked generator busy so subsequent
            // crews in the same tick don't pick the same agent. The
            // ledger update from the runner won't be visible until the
            // next tick; this in-memory mutation closes the gap.
            busyAgents.Add(picked!);

            // Resolve the paired evaluator crew (if any).
            CrewRow? evaluatorCrew = null;
            if (!string.IsNullOrEmpty(crew.PairedWithCrewId))
            {
                evaluatorCrew = await control.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct);
            }

            // Phase 16 compatibility — stamp AutoDispatchedAt + update assignment + emit task.delegated.
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
            if (wi is not null)
            {
                wi.AutoDispatchedAt = DateTimeOffset.UtcNow;
                wi.UpdatedAt = wi.AutoDispatchedAt.Value;
                // Phase 19 — mark live and bump trial count on claim.
                wi.ExecutionStatus = ExecutionStatuses.Live;
                wi.TrialCount++;
                // Move to 'active' on the board when an agent picks it up.
                // Items in 'proposed' or 'new' haven't been activated yet;
                // claiming them means work has started.
                if (wi.State is "proposed" or "new")
                {
                    wi.State = "active";
                }
                // Phase 22 — write AssignedAgentJson (not AssignedToJson).
                // AssignedToJson retains whatever WIT-level value it had.
                // Phase 47 — diversify the evaluator pick over the paired
                // crew's roster (or this crew's roster when standalone)
                // so two parallel claims don't share an evaluator. Falls
                // through to the legacy DefaultEvaluatorAgentId / lead
                // when no roster member is free; the diversity rule for
                // the evaluator side is best-effort, not blocking, so a
                // capacity-saturated pair doesn't wedge the queue.
                var evalAgentId = await ResolveDiverseEvaluatorAsync(
                    control, db, crew, evaluatorCrew, picked!, busyAgents, ct);
                var agents = new List<string> { picked! };
                if (evalAgentId is not null) agents.Add(evalAgentId);
                var now = DateTimeOffset.UtcNow;
                var crewTeam = TeamFromCrewName(crew.Name);
                var assignedAgent = new
                {
                    agents,
                    team = crewTeam,
                    crewId,
                    // The paired-crew end-to-end test asserts the evaluator
                    // crew id is recorded on the work item (Phase 16 — both
                    // sides of the pair are visible in the assignment row).
                    evaluatorCrewId = evaluatorCrew?.Id,
                    role = "generator",
                    assignedAt = now.ToString("o"),
                    assignedBy = "queue-worker",
                };
                wi.AssignedAgentJson = System.Text.Json.JsonSerializer.Serialize(assignedAgent);

                // Phase 47 — persist the resolved evaluator on the queue
                // row so the deliverable loop's evaluator-resolution path
                // (RunAgentAsync) reads the same agent that
                // AssignedAgentJson advertised. Without this, the runner
                // re-derives evaluator from CrewRow.DefaultEvaluatorAgentId
                // and disagrees with the diverse pick.
                if (!string.IsNullOrEmpty(evalAgentId))
                {
                    row.RoutedEvaluatorAgentId = evalAgentId;
                    busyAgents.Add(evalAgentId);
                }

                // Phase 22 — append agent_assignments history row atomically.
                db.AgentAssignments.Add(new AgentAssignmentRow
                {
                    WorkItemId = wi.Id,
                    Kind = AgentAssignmentKinds.Claimed,
                    AgentIdsJson = System.Text.Json.JsonSerializer.Serialize(agents),
                    Team = crewTeam,
                    CrewId = crewId,
                    Role = "generator",
                    FromExecutionStatus = ExecutionStatuses.Queued,
                    ToExecutionStatus = ExecutionStatuses.Live,
                    AssignedBy = "queue-worker",
                    At = now,
                });

                // Auto-link work item to a workstream so the Workstreams view
                // shows the "● N live" badge. Find a workstream for this repo
                // whose cards don't already include this work item.
                await this.AutoLinkToWorkstreamAsync(control, db, wi, ct);

                await db.SaveChangesAsync(ct);
                await this.BroadcastWorkItemChangedAsync(wi, "claimed", ct, db);
            }
            DwellMs.Record((DateTimeOffset.UtcNow - row.EnqueuedAt).TotalMilliseconds);
            await this.taskChannel.EmitAsync(row.WorkItemId, "task.delegated", new
            {
                queueId = row.Id,
                generatorCrewId = crewId,
                evaluatorCrewId = evaluatorCrew?.Id,
                generatorAgentId = picked,
                memberAgentId = picked,
                paired = evaluatorCrew is not null,
                viaQueue = true,
            }, crewId: crewId, ct: ct);
            await this.BroadcastQueueChangedAsync(row, "picked-up", ct);

            // Kick off the agent runner in the background. The invocation
            // runs asynchronously — the claim sweep doesn't wait for it.
            // Use CancellationToken.None so the agent isn't cancelled when the
            // sweep loop's token is disposed — the agent manages its own lifetime.
            //
            // Wrap in a try/catch swallowing ObjectDisposedException so test
            // host shutdown (factory.Dispose) racing with a still-running
            // agent doesn't crash the test process. Production hosts run
            // forever; this is a no-op there.
            var agentTask = Task.Run(async () =>
            {
                try
                {
                    await RunAgentAsync(row.WorkItemId, crewId, picked!, crew, wi, row.Kind, row.RoutedEvaluatorAgentId, CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                    // Host shutdown raced with agent — service provider /
                    // DbContext disposed mid-flight. Nothing to do.
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
                {
                    // Same as above (some EF / DI paths surface as InvalidOperationException
                    // rather than ObjectDisposedException).
                }
            });
            // Register so StopAsync can drain in-flight agents before
            // the DI scope is disposed. Self-deregister on completion to
            // bound memory growth in long-running production hosts.
            this.runningAgentTasks.TryAdd(agentTask, 0);
            _ = agentTask.ContinueWith(
                t => this.runningAgentTasks.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return (claimed, skips);
    }

    // ── Agent runner kickoff ───────────────────────────────────────────────
    private async Task RunAgentAsync(
        string workItemId, string crewId, string agentId,
        CrewRow crew, WorkItemRow? wi, string kind,
        string? routedEvaluatorAgentId,
        CancellationToken ct)
    {
        // Phase 44 — claim the agent slot before doing any work.
        await this.slots.TryClaimAsync(agentId, workItemId, "generator", ct);
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            // Guard: skip execution if the work item already has a delivered PR
            // (state "in-review", "resolved", or "closed"). This prevents boot-
            // recovery and manual re-queues from re-running already-completed items.
            var freshWi = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (freshWi is not null && freshWi.State is "in-review" or "resolved" or "closed")
            {
                this.logger.LogInformation(
                    "Skipping agent run: wi={Wi} already in state={State}, marking completed",
                    workItemId, freshWi.State);
                await this.CompleteQueueRowAsync(workItemId, ct);
                await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Completed, ct);
                return;
            }

            // Resolve the agent row for model/soul/backend overrides.
            var agent = await control.Agents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == agentId, ct);

            // Model: agent override → crew default.
            var model = !string.IsNullOrEmpty(agent?.GeneratorModel) ? agent!.GeneratorModel : crew.Model;

            // Backend: agent override → crew default → Copilot.
            var backendStr = !string.IsNullOrEmpty(agent?.Backend) ? agent!.Backend : crew.Backend;
            var backend = string.Equals(backendStr, "claude", StringComparison.OrdinalIgnoreCase)
                ? AgentBackend.Claude : AgentBackend.Copilot;

            // InvokeMode: agent override → crew default → Sdk.
            var invokeModeStr = !string.IsNullOrEmpty(agent?.InvokeMode) ? agent!.InvokeMode : crew.InvokeMode;
            var invokeMode = string.Equals(invokeModeStr, "cli", StringComparison.OrdinalIgnoreCase)
                ? AgentInvokeMode.Cli : AgentInvokeMode.Sdk;

            // Soul.md — always loaded when the agent has a SoulPath; the
            // skip-soul-prompt flag was removed (no caller ever set it).
            // ResolveSoulPath handles cross-machine deploy fallback.
            var soulMd = "";
            var resolvedSoulPath = ForgeSeed.ResolveSoulPath(agent?.SoulPath);
            if (!string.IsNullOrEmpty(resolvedSoulPath))
            {
                try { soulMd = await File.ReadAllTextAsync(resolvedSoulPath, ct); }
                catch { /* best effort */ }
            }

            // Phase 24 Q24.13 — comment-response runs prepend the per-task
            // system template ahead of soul.md so the must-reply rubric is
            // surfaced regardless of the agent's persona. Missing template
            // is a soft fallback (empty string) so the rest of the loop
            // still runs; tests cover the loaded path.
            if (string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase))
            {
                var template = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.CommentResponseFile, ct);
                if (!string.IsNullOrEmpty(template))
                {
                    soulMd = string.IsNullOrEmpty(soulMd)
                        ? template
                        : template + "\n\n---\n\n" + soulMd;
                }
            }

            // Phase 27 Q27.10 — build-fix runs prepend the build-fix template
            // ahead of soul.md so the must-act rubric is surfaced regardless
            // of the agent's persona.
            if (string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase))
            {
                var template = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.BuildFixFile, ct);
                if (!string.IsNullOrEmpty(template))
                {
                    soulMd = string.IsNullOrEmpty(soulMd)
                        ? template
                        : template + "\n\n---\n\n" + soulMd;
                }
            }

            // Plan tasks — architecture agents doing regular work get the
            // plan preamble so they produce design docs, not application code.
            // Detected via explicit kind=plan or agent role=arch on work tasks.
            var isPlanTask = string.Equals(kind, TaskQueueKinds.Plan, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(kind, TaskQueueKinds.Work, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(agent?.Role, "arch", StringComparison.OrdinalIgnoreCase));
            if (isPlanTask)
            {
                var template = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.PlanFile, ct);
                if (!string.IsNullOrEmpty(template))
                {
                    soulMd = string.IsNullOrEmpty(soulMd)
                        ? template
                        : template + "\n\n---\n\n" + soulMd;
                }
            }

            // Worktree: prefer the workstream's worktree path over the bare repo root.
            var worktreePath = "";
            var workstreamId = "";
            if (!string.IsNullOrEmpty(wi?.RepoId))
            {
                var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);

                // Find the workstream whose CardsJson references this work item.
                var workstreams = await db.Workstreams.AsNoTracking().ToListAsync(ct);
                WorkstreamRow? linkedWs = null;
                foreach (var ws in workstreams)
                {
                    try
                    {
                        var cards = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ws.CardsJson);
                        if (cards is not null && cards.Any(c => c == workItemId || c == $"#{workItemId}" || c.Contains(workItemId)))
                        {
                            linkedWs = ws;
                            break;
                        }
                    }
                    catch { /* malformed JSON — skip */ }
                }

                if (linkedWs is not null)
                {
                    workstreamId = linkedWs.Id;
                    try
                    {
                        var wtParts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(linkedWs.WorktreeJson);
                        if (wtParts is not null && wtParts.Count >= 2 && !string.IsNullOrEmpty(wtParts[0]) && !wtParts[0].StartsWith("("))
                        {
                            worktreePath = Path.Combine(wtParts[0], wtParts[1]);
                        }
                    }
                    catch { /* fallback below */ }
                }

                // Fallback: repo clone root.
                if (string.IsNullOrEmpty(worktreePath) && !string.IsNullOrEmpty(repo?.CloneRoot))
                {
                    worktreePath = repo.CloneRoot;
                }
            }

            // If the resolved path doesn't exist on disk, run with no working
            // directory rather than dumping the agent into %TEMP% (which makes
            // the SDK auto-index the user's entire temp folder and blow past
            // the model's prompt-token limit). The agent can still read files
            // by absolute path; tools that try to write/commit will fail loud.
            if (!string.IsNullOrEmpty(worktreePath) && !Directory.Exists(worktreePath))
            {
                this.logger.LogWarning(
                    "Worktree path {Path} does not exist for wi={Wi}; running with no working directory",
                    worktreePath, workItemId);
                worktreePath = string.Empty;
            }

            // Worktree feed subscription and prompt emission happen AFTER the
            // worktree is created so they reflect the final working directory.

            // ── Phase 18 — full delivery pipeline ────────────────────────────
            // 1) Create git worktree so agent doesn't commit on the base branch.
            // 2) Run generator ↔ evaluator loop via DeliverableLoop.
            // 3) On pass, create PR and transition work item to "resolved".

            // (1) Worktree setup — create a dedicated branch + directory for
            //     this work item. We run git directly against the repo clone
            //     root (not the singleton GitWorktreeService, which is pinned
            //     to a single RepoPath from config).
            // 2026-05-14 — route engineer workstreams through the canonical
            // path spec so boot-crash-recovery (and any ad-hoc invocation)
            // lands on the SAME branch + worktree directory the spawn-flow
            // owns. Without this the worker synthesised `ws/{workItemId}`
            // / `.worktree/ws-{workItemId}` which double-prefixed to
            // `ws/ws-…` / `.worktree/ws-ws-…` for any workstream id
            // already starting with `ws-`, and iter-N commits landed on a
            // sibling branch the orchestrator never saw — PR creation then
            // either pushed to the wrong branch or skipped on "0 commits
            // ahead". See WorktreePathResolver.GetCanonicalForWorkstreamAsync.
            var canonicalSpec = await Forge.Api.Services.Stories.WorktreePathResolver
                .GetCanonicalForWorkstreamAsync(control, db, workItemId, ct);

            string branchName;
            string baseBranch;
            string? repoCloneRoot;
            Persistence.RepoRow? wiRepo;
            string? canonicalWorktreeDir = null;

            if (canonicalSpec is not null)
            {
                branchName = canonicalSpec.Branch;
                baseBranch = canonicalSpec.BaseBranch;
                repoCloneRoot = Directory.Exists(canonicalSpec.RepoCloneRoot) ? canonicalSpec.RepoCloneRoot : null;
                wiRepo = canonicalSpec.Repo;
                canonicalWorktreeDir = canonicalSpec.WorktreePath;
            }
            else
            {
                // Ad-hoc (non-spawn) work item — preserve the pre-Phase-49
                // legacy convention: branch = ws/{workItemId},
                // worktree = .worktree/ws-{sanitizedId}.
                branchName = $"ws/{workItemId}";
                baseBranch = "main";
                repoCloneRoot = null;
                wiRepo = null;
                if (!string.IsNullOrEmpty(wi?.RepoId))
                {
                    wiRepo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                    if (!string.IsNullOrEmpty(wiRepo?.DefaultBranch))
                        baseBranch = wiRepo.DefaultBranch;
                    if (!string.IsNullOrEmpty(wiRepo?.CloneRoot) && Directory.Exists(wiRepo.CloneRoot))
                        repoCloneRoot = wiRepo.CloneRoot;
                }
            }

            if (!string.IsNullOrEmpty(repoCloneRoot))
            {
                // Canonical spec (when present) wins; otherwise fall back to
                // the legacy `.worktree/ws-{sanitizedId}` slug for ad-hoc
                // work items.
                var wtRoot = Path.Combine(repoCloneRoot, ".worktree");
                var worktreeDir = canonicalWorktreeDir
                    ?? Path.Combine(
                        wtRoot,
                        $"ws-{workItemId.Replace("/", "-").Replace("\\", "-").Replace(" ", "-")}");

                // Ensure .worktree/ and .forge/ are in .gitignore so they're never committed.
                // 2026-05-25 — extracted to GateLogPaths.EnsureGitignoreLinesAsync
                // so BuildValidationWorker (repo-validate runs, which never go
                // through workstream init) gets identical treatment for the
                // log + TRX files it writes under .forge/logs/.
                await Forge.Api.Services.Stories.GateLogPaths.EnsureGitignoreLinesAsync(
                    repoCloneRoot,
                    new[] { ".worktree/", ".forge/" },
                    ct);

                try
                {
                    // Check if worktree already exists for this branch.
                    var (listCode, listOut, _) = await RunGitAsync(repoCloneRoot, "worktree", "list", "--porcelain");
                    var existingWtPath = ParseWorktreePath(listOut, branchName);

                    if (!string.IsNullOrEmpty(existingWtPath) && Directory.Exists(existingWtPath))
                    {
                        // If the existing worktree is under the repo's .worktree/ dir, reuse it.
                        // Otherwise it's an old path (e.g. E:\forge\wt\) — remove and recreate
                        // under the repo so the agent works in the correct location.
                        var normalizedExisting = Path.GetFullPath(existingWtPath);
                        var normalizedWtRoot = Path.GetFullPath(wtRoot);
                        if (normalizedExisting.StartsWith(normalizedWtRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            worktreePath = existingWtPath;
                            this.logger.LogInformation(
                                "Reusing existing worktree: path={Path} branch={Branch}",
                                worktreePath, branchName);
                        }
                        else
                        {
                            this.logger.LogInformation(
                                "Removing stale worktree at {OldPath} (outside {WtRoot})",
                                existingWtPath, wtRoot);
                            await RunGitAsync(repoCloneRoot, "worktree", "remove", "--force", existingWtPath);
                            existingWtPath = null; // fall through to creation below
                        }
                    }

                    if (string.IsNullOrEmpty(existingWtPath) || !Directory.Exists(existingWtPath))
                    {
                        // Create the worktree root directory and the worktree.
                        Directory.CreateDirectory(wtRoot);
                        // Branch may already exist from a prior run — use checkout instead of -b
                        var (checkCode, _, _) = await RunGitAsync(repoCloneRoot,
                            "rev-parse", "--verify", branchName);
                        var (code, _, stderr) = checkCode == 0
                            ? await RunGitAsync(repoCloneRoot, "worktree", "add", worktreeDir, branchName)
                            : await RunGitAsync(repoCloneRoot, "worktree", "add", "-b", branchName, worktreeDir, baseBranch);
                        if (code == 0)
                        {
                            worktreePath = worktreeDir;
                            this.logger.LogInformation(
                                "Created worktree: path={Path} branch={Branch} base={Base}",
                                worktreePath, branchName, baseBranch);
                        }
                        else
                        {
                            this.logger.LogWarning(
                                "git worktree add failed (exit {Code}): {Stderr}", code, stderr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex,
                        "Worktree creation failed for wi={Wi}, continuing with repo root", workItemId);
                }
            }

            // Subscribe the worktree feed now that the final path is known.
            if (!string.IsNullOrEmpty(worktreePath))
            {
                // Phase 49 follow-up — anchor the live branchDiff at the
                // workstream's REAL base branch (spawn.BaseBranch when
                // present) so the Worktree-tab pills agree with what
                // /commits computes. Without this the feed would diff
                // against repo default (often `main`) and inflate the
                // pills with every upstream PR's contents.
                var resolvedBase = await Forge.Api.Services.Stories.WorkItemBaseBranchResolver
                    .ResolveAsync(control, db, workItemId, ct);
                this.worktreeFeed.Subscribe(workItemId, workstreamId, worktreePath, resolvedBase);

                // Scrub already-tracked .forge/ files from the index
                // BEFORE the agent runs. info/exclude only prevents
                // NEW files from being staged; this handles files
                // committed in a prior iteration.
                try
                {
                    await Forge.Api.Services.Stories.CrewStoryArtifactStrategy.TryAddGitInfoExcludeAsync(
                        worktreePath, ".forge/", ct);
                    await RunGitAsync(worktreePath,
                        "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");
                }
                catch { /* best-effort */ }
            }

            // Build prompt from the work item.
            var title = wi?.Title ?? workItemId;
            var desc = wi?.DescriptionMd ?? "";

            string prompt;
            if (string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase))
            {
                // Phase 24.9 — comment-response prompt loads the open PR comments
                // needing response and asks the agent to reply or commit a fix.
                prompt = await BuildCommentResponsePromptAsync(control, db, workItemId, title, ct);
            }
            else if (string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase))
            {
                // Phase 27 Q27.10 — build-fix prompt carries the failed
                // build's pipeline header, parsed FailurePoint rows, the
                // 256 KB log tail of failed stages, the artifact index, the
                // work-item brief, and recent commit messages. Logs and
                // artifacts are NOT inlined verbatim — the agent uses
                // `read_file` to descend into specific artifact files.
                prompt = await BuildBuildFixPromptAsync(control, db, workItemId, title, desc, ct);
            }
            else if (isPlanTask)
            {
                var basePrompt = string.IsNullOrWhiteSpace(desc)
                    ? $"Work item: {title}"
                    : $"Work item: {title}\n\n{desc}";

                prompt = basePrompt + "\n\n"
                    + "IMPORTANT: This is a PLAN task. You are producing design artifacts, NOT application code.\n"
                    + "1. Write the design narrative to `docs/specs/{slug}/plan.md` (derive slug from the work item title).\n"
                    + "2. Create the WIT tree of phase → stage → step rows as described in the plan-system preamble.\n"
                    + "3. Commit your design documents with a descriptive commit message.\n"
                    + "4. Push the branch to the remote with `git push --force-with-lease origin HEAD`.\n"
                    + "   The remote branch may already exist from a prior run — force-push is expected.\n"
                    + "5. End your response with a section titled '## Plan Summary' that explains:\n"
                    + "   - The architectural approach and key design decisions\n"
                    + "   - The phase → stage → step decomposition rationale\n"
                    + "   - Out-of-scope items and open questions\n"
                    + "Do NOT write application code, tests, or modify source files outside `docs/specs/`.\n"
                    + "You MUST push before finishing — the server creates the PR automatically after you push.";
            }
            else
            {
                var basePrompt = string.IsNullOrWhiteSpace(desc)
                    ? $"Work item: {title}"
                    : $"Work item: {title}\n\n{desc}";

                // Append PR summary instruction so the generator's final text
                // block can be used as the "Generator perspective" in the PR.
                prompt = basePrompt + "\n\n"
                    + "IMPORTANT: After completing all code changes and tests:\n"
                    + "1. Commit your changes with a descriptive commit message.\n"
                    + "2. Push the branch to the remote with `git push --force-with-lease origin HEAD`.\n"
                    + "   The remote branch may already exist from a prior run — force-push is expected.\n"
                    + "3. End your response with a section titled '## Change Summary' that explains:\n"
                    + "   - **What** you changed (files and code paths modified)\n"
                    + "   - **Why** this fixes the problem or improves the system\n"
                    + "   - **How** the fix works (technical approach)\n"
                    + "Keep the summary concise (3-6 sentences). This will appear in the PR description.\n"
                    + "You MUST push before finishing — the server creates the PR automatically after you push.";
            }

            // (Operator-note injection moved into the !isResumeDelivery block
            // below so it runs after isResumeDelivery is computed and only on
            // the path that actually invokes the agent.)

            // Phase 21 — per-task budgets scaled by story points.
            // Wall-clock: 30 min per point. Max turns: 30 turns per point.
            // Falls back to the global defaults when points = 0.
            var points = wi?.Points ?? 0;
            TimeSpan? taskBudget = points > 0
                ? TimeSpan.FromMinutes(points * 30)
                : null;
            int? taskMaxTurns = points > 0
                ? points * 30
                : null;

            this.logger.LogInformation(
                "Starting agent invocation: wi={Wi} agent={Agent} crew={Crew} model={Model} backend={Backend} mode={Mode} worktree={Worktree} budget={Budget}m maxTurns={MaxTurns}",
                workItemId, agentId, crewId, model, backend, invokeMode, worktreePath, taskBudget?.TotalMinutes ?? 10, taskMaxTurns ?? 0);

            // Resume-delivery short-circuit — when the prior run's iter loop
            // already produced a verdict=pass deliverable but the post-loop
            // delivery failed (build gate, push, PR), the operator can re-
            // dispatch with kind=resume-delivery to skip the gen+eval loop and
            // re-run only the delivery phase. We synthesize a DeliverableLoopResult
            // from the last persisted deliverable and fall through to the
            // existing `if (result.FinalVerdict == "pass")` delivery branch.
            // Only validation runs here; the actual task.prompt emit + loop
            // invocation is gated by `isResumeDelivery` below so the agent
            // SDK is never started.
            var isResumeDelivery = string.Equals(kind, TaskQueueKinds.ResumeDelivery, StringComparison.OrdinalIgnoreCase);
            DeliverableLoopResult? resumeResult = null;
            DeliverableLoopResult? loopResult = null;
            // Hoisted for the post-loop delivery section (used in both the
            // normal pass-then-deliver flow and the resume-delivery path).
            string? evaluatorAgentId = null;
            string? evaluatorCrewId = null;
            string loopKind = string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase)
                ? DeliverableKinds.CommentResponse
                : string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase)
                    ? DeliverableKinds.BuildFix
                    : isPlanTask
                        ? DeliverableKinds.Plan
                        : DeliverableKinds.Coding;
            if (isResumeDelivery)
            {
                var allDeliv = await db.Deliverables.AsNoTracking()
                    .Where(d => d.WorkItemId == workItemId)
                    .ToListAsync(ct);
                var lastDeliv = allDeliv
                    .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
                    .FirstOrDefault();
                if (lastDeliv?.Verdict != "pass")
                {
                    this.logger.LogWarning(
                        "Resume-delivery refused: wi={Wi} last verdict={Verdict} (need 'pass')",
                        workItemId, lastDeliv?.Verdict ?? "(none)");
                    await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                    {
                        agentId, crewId,
                        error = $"Resume-delivery refused: last deliverable verdict='{lastDeliv?.Verdict ?? "(none)"}', expected 'pass'.",
                    }, crewId: crewId, ct: ct);
                    await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                    await this.CompleteQueueRowAsync(workItemId, ct);
                    return;
                }
                this.logger.LogInformation(
                    "Resume-delivery: wi={Wi} priorIter={Iter} priorScore={Score} — skipping gen+eval loop",
                    workItemId, lastDeliv.Iter, lastDeliv.Score);
                await this.taskChannel.EmitAsync(workItemId, "task.delivery-resumed", new
                {
                    agentId, crewId,
                    priorIter = lastDeliv.Iter,
                    priorScore = lastDeliv.Score,
                    priorAgent = lastDeliv.GeneratorAgentId,
                }, crewId: crewId, ct: ct);
                resumeResult = new DeliverableLoopResult("pass", lastDeliv.Iter, lastDeliv.Score, Array.Empty<long>(), 0m);

                // Pin evaluator from the prior deliverable so the PR body
                // byline reads "evaluated by {agent}" with the same scorer
                // that signed off on the iteration loop.
                evaluatorAgentId = lastDeliv.EvaluatorAgentId;
                if (!string.IsNullOrEmpty(evaluatorAgentId))
                {
                    var evalAgent = await control.Agents.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == evaluatorAgentId, ct);
                    evaluatorCrewId = evalAgent?.CrewId;
                }
            }

            if (!isResumeDelivery)
            {

            // Operator-note injection — the /api/work-items/{id}/resume endpoint
            // emits a `task.operator-note` transcript event when the operator
            // hits Resume on a non-pass deliverable with a steering note. We
            // pick up the latest one *since the last deliverable* so stale
            // notes from earlier pauses don't leak into the current run, and
            // prepend it to the user prompt so the generator sees it first.
            // SQLite can't ORDER BY DateTimeOffset; use Id (monotonic by
            // insertion) and re-read CreatedAt client-side.
            var lastDelivAt = (await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == workItemId)
                .OrderByDescending(d => d.Id)
                .Select(d => new { d.CreatedAt })
                .FirstOrDefaultAsync(ct))?.CreatedAt;
            // SQLite EF cannot translate ANY DateTimeOffset comparison in a
            // Where clause — not even non-nullable vs non-nullable. The
            // first fix attempt collapsed the nullable away but still
            // threw `could not be translated` for `e.At > @p`. Narrow the
            // server query to the cheap `Kind` predicate, materialize the
            // latest few candidates, then filter At in memory.
            // qq:MICROSOFT-TEAMS-MESS stage 2.1 (2026-05-13 22:41:45 →
            // 23:34/23:36/23:52/23:59 retry storm post-restart).
            var noteThreshold = lastDelivAt ?? DateTimeOffset.MinValue;
            var noteCandidates = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == workItemId
                    && e.Kind == "task.operator-note")
                .OrderByDescending(e => e.Id)
                .Take(10)
                .ToListAsync(ct);
            var noteEvt = noteCandidates.FirstOrDefault(e => e.At > noteThreshold);
            if (noteEvt is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(noteEvt.PayloadJson);
                    if (doc.RootElement.TryGetProperty("note", out var n)
                        && n.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        prompt = "OPERATOR NOTE (steering for this iteration — read first):\n"
                            + n.GetString()!.Trim()
                            + "\n\n---\n\n"
                            + prompt;
                    }
                }
                catch { /* best-effort */ }
            }

            // Emit the system + user prompts so the live execution view shows
            // what the agent is being asked to do.
            await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
            {
                agentId,
                crewId,
                systemPrompt = string.IsNullOrEmpty(soulMd) ? null : soulMd,
                userPrompt = prompt,
                model,
                backend = backendStr,
                invokeMode = invokeModeStr,
                worktreePath,
                branch = branchName,
            }, crewId: crewId, ct: ct);

            // Rebuild context with the (possibly updated) worktree path.
            // Phase 24 Q24.13 — when kind='comment-response' the runner registers
            // post_pr_comment_reply alongside the standard read/write/git toolset.
            // open_pr is intentionally NOT in this list — comment-response writes
            // against the existing PR, never opens a new one.
            var allowedTools = string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase)
                ? CommentResponseAllowedTools
                : string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase)
                    ? BuildFixAllowedTools
                    : isPlanTask
                        ? PlanAllowedTools
                        : Array.Empty<string>();

            var context = new AgentContext(
                CrewId: crewId,
                Model: model,
                SoulMd: soulMd,
                WorktreePath: worktreePath,
                AllowedTools: allowedTools,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                Backend: backend,
                InvokeMode: invokeMode,
                WallClockBudget: taskBudget,
                MaxTurns: taskMaxTurns);

            // (2) Resolve evaluator crew + agent for the gen-eval loop.
            // (`evaluatorAgentId`/`evaluatorCrewId` are declared at the top of
            // the function; the resume-delivery branch fills them from the
            // last deliverable. Here we resolve them for the normal path.)
            CrewRow? evalCrew = null;
            if (!string.IsNullOrEmpty(crew.PairedWithCrewId))
            {
                evalCrew = await control.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct);
                evaluatorCrewId = evalCrew?.Id;
                // Phase 47 — prefer the evaluator the queue worker resolved
                // at claim time so this iteration scores against the same
                // diverse pick that AssignedAgentJson advertised. Falls
                // back to the legacy DefaultEvaluatorAgentId / lead chain
                // when null (legacy claims pre-dating Phase 47).
                evaluatorAgentId = !string.IsNullOrEmpty(routedEvaluatorAgentId)
                    ? routedEvaluatorAgentId
                    : crew.DefaultEvaluatorAgentId ?? evalCrew?.Lead;
            }

            // (3) Run the deliverable loop (generator ↔ evaluator iterations).
            var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();

            // Generator delegate: invokes the agent runner and collects output.
            async Task<GeneratorRunOutput> GeneratorDelegate(int iter, string evalNotes, CancellationToken iterCt)
            {
                // On a retry, the generator has to know (a) the running chain
                // of evaluator feedback and (b) what was actually committed
                // in the prior round. Without (b) we've watched the model
                // re-discover the codebase from scratch and re-introduce the
                // exact diff that just got rejected (WI-1035 score collapse
                // 72→68→35→35→10). The instruction below pins the model to
                // diff-first inspection before any new edits.
                // Per-iteration summary requirement — appended on every
                // round so the deliverable row's GeneratorSummary captures
                // *this* iteration's work (not cumulative). The Summary tab
                // in the live-exec UI lists these per iter so the operator
                // can see what each round actually did.
                var iterSummaryAddendum =
                    "\n\n---\n\n"
                  + $"END-OF-RESPONSE REQUIREMENT (iteration {iter}):\n"
                  + "End your response with a section titled `## Iteration Summary` covering:\n"
                  + $"- **What changed this iteration ({iter})** — files touched, functions added/modified, tests added.\n"
                  + "- **Why** these changes address the work item (and, on retry, the prior evaluator's feedback).\n"
                  + "- **Notable decisions or trade-offs** — anything the evaluator should know to grade fairly.\n"
                  + "Keep it 4-8 sentences. This section is parsed and shown verbatim in the Summary tab.";

                var iterPrompt = iter == 1
                    ? prompt + iterSummaryAddendum
                    : $"{prompt}\n\n---\n\n{evalNotes}\n"
                      + "---\n\n"
                      + "BEFORE editing anything this round:\n"
                      + "1. Run `git log --oneline -5` and `git diff HEAD~1 --stat -p` to see what the prior iteration actually committed.\n"
                      + "2. Cross-check the prior diff against the evaluator chain above. If the chain says a specific symbol/file/contract is wrong and the prior diff did not change it, that is the gap to close — not the rest of the change.\n"
                      + "3. If you find yourself about to write the same code that was just rejected, stop and choose a different approach (different file, different abstraction, different test).\n"
                      + "Only after you've read the prior diff should you start editing."
                      + iterSummaryAddendum;

                // Phase 82.2 — prepend operator decision pins + prior-iter notes
                // when present. Without this block, audit goal #2 (prior-iter
                // context) and goal #4 (global rules / pins) succeed only on
                // plan-doc + qa-e2e prompts; impl-path generators re-discover
                // the codebase every iter and silently drop operator pins.
                //
                // Decision pins ALWAYS go first when present — they override
                // every other piece of guidance (eval feedback, brief).
                // Prior-iter notes follow because the agent session has no
                // memory across runner.StartAsync calls; .forge/iter-notes.md
                // is the only continuity. Order matches CrewStoryArtifactStrategy.
                //
                // Persist failures here MUST NOT abort the iteration — log
                // a warning, emit telemetry, and fall back to the plain
                // iterPrompt so the agent still gets a usable brief.
                try
                {
                    var pinsBlock = await Forge.Api.Services.Stories.CrewStoryArtifactStrategy
                        .BuildDecisionPinsBlockAsync(this.scopeFactory, workItemId, iterCt);
                    var notesBlock = string.IsNullOrEmpty(worktreePath)
                        ? string.Empty
                        : await Forge.Api.Services.Stories.CrewStoryArtifactStrategy
                            .BuildPriorIterNotesPromptBlockAsync(worktreePath, iterCt);

                    if (!string.IsNullOrEmpty(pinsBlock) || !string.IsNullOrEmpty(notesBlock))
                    {
                        var sb = new System.Text.StringBuilder();
                        if (!string.IsNullOrEmpty(pinsBlock))
                        {
                            sb.AppendLine(pinsBlock);
                            sb.AppendLine("────────────────────────────────────────────────────────────");
                            sb.AppendLine();
                        }
                        if (!string.IsNullOrEmpty(notesBlock))
                        {
                            sb.AppendLine(notesBlock);
                            sb.AppendLine();
                            sb.AppendLine("────────────────────────────────────────────────────────────");
                            sb.AppendLine();
                        }
                        sb.Append(iterPrompt);
                        iterPrompt = sb.ToString();
                    }
                }
                catch (Exception enrichEx)
                {
                    this.logger.LogWarning(enrichEx,
                        "Phase 82.2 — failed to enrich iterPrompt with pins/notes for {WorkItemId} iter {Iter}; falling back to plain prompt",
                        workItemId, iter);
                }

                // Phase 82.1 — emit the *actual* per-iter prompt (the one the
                // model sees, with evalNotes + diff-first preamble + iter-
                // summary addendum) so the live UI's per-iter Prompt tab and
                // the durable iteration_prompt audit row both reflect what
                // really ran. The pre-loop "task.prompt" emit at the top of
                // RunAgentAsync captures only iter-1's base brief without
                // evalNotes; it stays for backward compat (older tabs that
                // look at the worktreePath/branch fields).
                await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
                {
                    agentId,
                    crewId,
                    systemPrompt = string.IsNullOrEmpty(soulMd) ? null : soulMd,
                    userPrompt = iterPrompt,
                    model,
                    backend = backendStr,
                    invokeMode = invokeModeStr,
                    role = "generator",
                    iter,
                }, crewId: crewId, ct: iterCt);

                // Phase 82.1 — durable per-iter prompt audit. Mirrors the
                // task.prompt emit above so the audit survives transcript
                // retention pruning (last-10 open / last-1 closed) and
                // TaskChannel's 10 000-row cap. CrewQueueWorker is a
                // singleton; IterationPromptStore is scoped on
                // ProductDbContext so each save runs in a fresh DI scope.
                // Persistence failures MUST NOT abort the iteration — log
                // + emit telemetry, never throw.
                try
                {
                    using var promptScope = this.scopeFactory.CreateScope();
                    var promptStore = promptScope.ServiceProvider
                        .GetRequiredService<Forge.Api.Services.IterationPromptStore>();
                    await promptStore.SaveAsync(
                        workItemId: workItemId,
                        iter: iter,
                        role: "generator",
                        agentId: agentId,
                        crewId: crewId,
                        systemPrompt: string.IsNullOrEmpty(soulMd) ? null : soulMd,
                        userPrompt: iterPrompt,
                        model: model,
                        backend: backendStr,
                        invokeMode: invokeModeStr,
                        ct: iterCt);
                }
                catch (Exception persistEx)
                {
                    this.logger.LogWarning(persistEx,
                        "Phase 82.1 — failed to persist generator prompt for {WorkItemId} iter {Iter}",
                        workItemId, iter);
                    try
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.prompt.persist-failed", new
                        {
                            role = "generator",
                            iter,
                            error = persistEx.Message,
                        }, crewId: crewId, ct: iterCt);
                    }
                    catch
                    {
                        // best-effort — transcript channel itself may be down
                    }
                }

                var invocation = await runner.StartAsync(workItemId, agentId, iterPrompt, context, iterCt);
                var output = new System.Text.StringBuilder();
                decimal cost = 0m;
                long inTok = 0, outTok = 0;
                // 2026-05-16 Phase 72 — accumulate cache / reasoning /
                // conversation tokens off the AgentEvent stream so they
                // can be threaded onto GeneratorRunOutput and persisted
                // by DeliverableLoop.WriteUsageAsync.
                long cacheReadTok = 0, cacheWriteTok = 0, reasoningTok = 0, conversationTokLatest = 0;
                string? capturedSessionId = null;
                // 2026-05-13 root-cause fix — capture the dispatcher's
                // Failed-event error so the loop can short-circuit before
                // the evaluator runs against a cap-aborted partial output.
                // See DeliverableLoop.cs FailureReason short-circuit and
                // GeneratorFailureKinds.Classify for the receiving end.
                string? failureReason = null;
                // Phase 88 -- tool-call counter for the DeliverableLoop
                // refusal guard (zero tool calls + refusal-like text
                // triggers immediate sloppy/agent-refusal exit instead of
                // burning a full iter-cap window).
                long toolCallCount = 0;

                await foreach (var evt in invocation.Events.WithCancellation(iterCt))
                {
                    var kind = evt.Kind switch
                    {
                        AgentEventKind.Token => "task.token",
                        AgentEventKind.ToolCall => "task.tool-call",
                        AgentEventKind.ToolResult => "task.tool-result",
                        AgentEventKind.Finished => "task.finished",
                        AgentEventKind.Failed => "task.failed",
                        _ => $"task.{evt.Kind.ToString().ToLowerInvariant()}",
                    };
                    await this.taskChannel.EmitAsync(workItemId, kind, new
                    {
                        invocationId = invocation.InvocationId,
                        agentId,
                        crewId,
                        text = evt.Text,
                        toolName = evt.ToolName,
                        toolPayload = evt.ToolPayload,
                        error = evt.Error,
                        inputTokens = evt.InputTokens,
                        outputTokens = evt.OutputTokens,
                        // 2026-05-16 Phase 72 — propagate cache /
                        // reasoning / conversation + per-tool detail
                        // to the SignalR stream so the UI Tools tab
                        // and the iter-kpis strip can render the new
                        // fields without re-fetching the deliverables.
                        cacheReadTokens = evt.CacheReadTokens,
                        cacheWriteTokens = evt.CacheWriteTokens,
                        reasoningTokens = evt.ReasoningTokens,
                        conversationTokens = evt.ConversationTokens,
                        toolCallId = evt.ToolCallId,
                        parentToolCallId = evt.ParentToolCallId,
                        durationMs = evt.ToolDurationMs,
                        exitCode = evt.ToolExitCode,
                        mcpServerName = evt.McpServerName,
                        sessionId = evt.SessionId,
                    }, crewId: crewId, ct: iterCt);

                    if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                    if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                    if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                    if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
                    if (evt.CacheReadTokens is > 0) cacheReadTok += evt.CacheReadTokens.Value;
                    if (evt.CacheWriteTokens is > 0) cacheWriteTok += evt.CacheWriteTokens.Value;
                    if (evt.ReasoningTokens is > 0) reasoningTok += evt.ReasoningTokens.Value;
                    if (evt.ConversationTokens is > 0) conversationTokLatest = evt.ConversationTokens.Value;
                    if (!string.IsNullOrEmpty(evt.SessionId)) capturedSessionId = evt.SessionId;
                    if (evt.Kind == AgentEventKind.Failed && !string.IsNullOrEmpty(evt.Error))
                    {
                        failureReason = evt.Error;
                    }
                    // Phase 88 — tool-call counter for the refusal guard.
                    if (evt.Kind == AgentEventKind.ToolCall)
                    {
                        toolCallCount++;
                    }
                }

                // 2026-05-13 root-cause fix: persist the agent's iter work as
                // a commit on the workstream branch and push it. Without this,
                // every iter leaves edits uncommitted; the iter N+1 prompt
                // (CrewQueueWorker.cs:1456-1463) instructs the model to use
                // `git log --oneline -5` / `git diff HEAD~1` to learn what
                // the prior iter did — which returns nothing when nothing
                // was committed, so the model re-discovers and re-edits the
                // same code from scratch. Observed on failover-cluster:XRAFT
                // stage 3.1: $96/iter × 2 iters with zero progress. Pushing
                // also means the pre-dispatch `git reset --hard origin/{branch}`
                // (RealStoryGitOps.MergeUpstreamIntoBranchAsync) no longer
                // destroys the work — committed-and-pushed state is what the
                // reset aligns to. Best-effort: failures log and continue so
                // a transient push hiccup doesn't fail the iter.
                if (!string.IsNullOrEmpty(worktreePath))
                {
                    try
                    {
                        var (statusCode, statusOut, _) = await RunGitAsync(worktreePath, "status", "--porcelain");
                        if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
                        {
                            await RunGitAsync(worktreePath, "add", "-A");
                            await RunGitAsync(worktreePath, "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");
                            await ScrubBuildArtifactsFromIndexAsync(worktreePath);
                            var commitMsg = $"forge[{agentId}] iter {iter}: {workItemId}";
                            var (commitCode, _, commitErr) = await RunGitAsync(
                                worktreePath,
                                "-c", "user.email=forge-engineer@local",
                                "-c", "user.name=Forge",
                                "commit", "-m", commitMsg);
                            if (commitCode != 0)
                            {
                                this.logger.LogWarning(
                                    "Per-iter commit failed for wi={Wi} iter={Iter} branch={Branch}: {Err}",
                                    workItemId, iter, branchName, commitErr);
                            }
                            else
                            {
                                var (pushCode, _, pushErr) = await RunGitAsync(worktreePath, "push", "origin", branchName);
                                if (pushCode != 0)
                                {
                                    this.logger.LogWarning(
                                        "Per-iter push failed for wi={Wi} iter={Iter} branch={Branch}: {Err}",
                                        workItemId, iter, branchName, pushErr);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        this.logger.LogWarning(ex,
                            "Per-iter commit step threw for wi={Wi} iter={Iter}; continuing.",
                            workItemId, iter);
                    }
                }

                return new GeneratorRunOutput(
                    output.ToString(),
                    cost,
                    inTok,
                    outTok,
                    TurnSamplesJson: null,
                    FailureReason: failureReason,
                    FailureKind: failureReason is null ? null : GeneratorFailureKinds.Classify(failureReason),
                    CacheReadTokens: cacheReadTok > 0 ? cacheReadTok : null,
                    CacheWriteTokens: cacheWriteTok > 0 ? cacheWriteTok : null,
                    ReasoningTokens: reasoningTok > 0 ? reasoningTok : null,
                    ConversationTokens: conversationTokLatest > 0 ? conversationTokLatest : null,
                    SessionId: capturedSessionId,
                    ToolCallCount: toolCallCount);
            }

            // Evaluator delegate: invokes the evaluator agent for critical review.
            async Task<EvaluatorRunOutput> EvaluatorDelegate(int iter, string genOutput, CancellationToken iterCt)
            {
                if (string.IsNullOrEmpty(evaluatorAgentId) || evalCrew is null)
                {
                    // No evaluator configured — auto-pass with perfect score.
                    return new EvaluatorRunOutput(
                        "{\"score\": 95, \"verdict\": \"pass\", \"notes\": \"No evaluator configured; auto-pass.\"}",
                        0m);
                }

                var evalAgent = await control.Agents.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == evaluatorAgentId, iterCt);
                var evalModel = !string.IsNullOrEmpty(evalAgent?.EvaluatorModel)
                    ? evalAgent!.EvaluatorModel
                    : !string.IsNullOrEmpty(evalCrew.EvaluatorModel)
                        ? evalCrew.EvaluatorModel
                        : evalCrew.Model;
                var evalSoul = "";
                if (!string.IsNullOrEmpty(evalAgent?.SoulPath))
                {
                    try { evalSoul = await File.ReadAllTextAsync(evalAgent.SoulPath, iterCt); }
                    catch { /* best effort */ }
                }

                var evalBackendStr = !string.IsNullOrEmpty(evalAgent?.Backend) ? evalAgent!.Backend : evalCrew.Backend;
                var evalBackend = string.Equals(evalBackendStr, "claude", StringComparison.OrdinalIgnoreCase)
                    ? AgentBackend.Claude : AgentBackend.Copilot;

                // The evaluator inspects the worktree directly using read-only
                // tools (view, grep, git diff). No generator output is stuffed into
                // the prompt — the evaluator sees the actual deliverable, not
                // a parroted narration of how the generator produced it.
                var wiPointsForScoring = wi?.Points ?? 1;
                var rejectBelowForScoring = ScoringConstants.RejectThreshold(wiPointsForScoring);
                var evalSoulForScoring = (string.IsNullOrEmpty(evalSoul) ? "" : evalSoul + "\n\n---\n\n")
                    + "CRITICAL RULES FOR THIS SESSION:\n"
                    + "1. Use tools (view, grep, powershell) to inspect the worktree and understand what changed.\n"
                    + "   Run `git diff HEAD~1 --stat -p` to see the actual code changes.\n"
                    + "   Read modified files to verify correctness.\n"
                    + "2. Do NOT modify any files. Do NOT commit, push, or run builds. Read-only inspection only.\n"
                    + "3. Output **only** a fenced JSON block. Put your full review inside the `notes` field.\n"
                    + "   Do NOT write prose outside the JSON. Example shape:\n"
                    + "```json\n"
                    + "{\n"
                    + "  \"score\": 87,\n"
                    + "  \"dims\": { \"correctness\": 35, \"quality\": 22, \"tests\": 13, \"scope\": 9, \"risk\": 8 },\n"
                    + "  \"verdict\": \"iterate\",\n"
                    + "  \"notes\": \"Improvements this iteration: …  Still needs improvement: …  Why this score: …\"\n"
                    + "}\n"
                    + "```\n"
                    + "   STRICT JSON: double-quoted keys (`\"score\"`, not `score`), no trailing commas, no // or /* */ comments.\n"
                    + "   If your JSON is malformed your entire review is discarded and the iteration is wasted.\n"
                    + "   Dimension caps (must stay within bounds — out-of-cap values are silently clamped\n"
                    + "   and signal rubric drift): correctness 0..40, quality 0..25, tests 0..15, scope 0..10,\n"
                    + "   risk 0..10. Caps sum to 100; `score` must equal the sum and stay in 0..100.\n"
                    + $"4. Score >= 90 = pass, {rejectBelowForScoring}-89 = iterate, < {rejectBelowForScoring} = reject (adjusted for {wiPointsForScoring}-point work item).\n"
                    + "5. Your `notes` field MUST be a per-iteration review covering, in order, three labeled parts:\n"
                    + "   **Improvements this iteration**: what the generator actually changed and what's better\n"
                    + "      compared to the prior iteration (or vs. the work item if this is iteration 1). Cite\n"
                    + "      specific files, functions, and tests.\n"
                    + "   **Still needs improvement**: concrete gaps the next iteration must close — naming files,\n"
                    + "      symbols, contracts, or behaviors. If unaddressed prior-iteration feedback persists,\n"
                    + "      call that out explicitly so the operator can see the regression.\n"
                    + "   **Why this score**: dimension-by-dimension rationale — correctness, quality, tests,\n"
                    + "      scope, risk — explaining how you arrived at the number.\n"
                    + "   Do NOT say 'build clean, tests pass' alone — that is self-evident. Use up to ~3500 chars\n"
                    + "   total across the three parts (the parser truncates at 4000).\n";

                // Phase 24 Q24.12 / Q24.13 — comment-response runs append the
                // must-reply rubric note so the evaluator scores against the
                // "every input comment is addressed via commit OR reply" rule.
                // Scope is interpreted as the union of input comments — a
                // reply-only deliverable scoring 100 on scope is valid.
                if (string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase))
                {
                    evalSoulForScoring += "\n"
                        + "COMMENT-RESPONSE RUBRIC ADDENDUM (Phase 24 Q24.13):\n"
                        + "- The deliverable's *scope* is the union of the input comments, NOT the entire work item.\n"
                        + "  A small focused reply that addresses every input comment is the right shape.\n"
                        + "- For every input comment the agent must take exactly one of two actions:\n"
                        + "  (a) commit a fix that the position-and-time heuristic will link, OR\n"
                        + "  (b) post a reply that names the comment id (Addresses comment <id>).\n"
                        + "- Silently dropping a comment is a graded failure. A deliverable that ignores any\n"
                        + "  RequiresAttention=true comment scores ≤ 70 on correctness regardless of the rest\n"
                        + "  of the work — verdict iterate or reject.\n"
                        + "- Reply-only deliverables (zero git commits, only post_pr_comment_reply calls\n"
                        + "  covering every input comment) ARE valid. They can score ≥ 90 against this rubric.\n"
                        + "- The agent does NOT auto-resolve threads. Only humans resolve. Pass means the agent\n"
                        + "  did its part; the reviewer's resolve click closes the thread upstream.\n";
                }

                // Phase 27 Q27.8 — build-fix runs append the build-fix
                // rubric extension so the evaluator scores against the
                // "did the fix address the parsed failure points?" rule.
                if (string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase))
                {
                    evalSoulForScoring += "\n"
                        + "BUILD-FIX RUBRIC ADDENDUM (Phase 27 Q27.8 / Q27.10):\n"
                        + "- The deliverable's *scope* is the union of the parsed FailurePoint rows from the\n"
                        + "  failed build, NOT the entire work item.\n"
                        + "- For every failure point the agent must take exactly one of two actions:\n"
                        + "  (a) commit a fix that plausibly addresses the failure point, OR\n"
                        + "  (b) post a PR reply explaining why the failure is non-code (flaky test, infra,\n"
                        + "      env, signing-key rotation) using the format `Build #<n> failure analysis: …`.\n"
                        + "- Silently exiting (no commit AND no reply) is a graded failure. A deliverable that\n"
                        + "  ignores any failure point scores ≤ 70 on correctness regardless of the rest of the\n"
                        + "  work — verdict iterate or reject.\n"
                        + "- Did the fix address the parsed failure points? If multiple failure points exist,\n"
                        + "  are they all addressed? Score against this rule explicitly.\n"
                        + "- Reply-only deliverables (zero git commits, only post_pr_comment_reply calls) ARE\n"
                        + "  valid when every failure point is genuinely non-code. They can score ≥ 90.\n"
                        + "- The build re-runs naturally on push — the runner does NOT manually re-trigger.\n";
                }

                // Pair-diversity gate (forge_implementation.md Phase 13e).
                // Generator + evaluator MUST run on different models so the
                // evaluator catches blind spots the generator's training
                // distribution can't see. Throw before dispatch so a bad
                // crew config surfaces as a task.failed event the operator
                // can diagnose, instead of silently shipping pass-stamped
                // subpar work that no second eye ever caught.
                if (!string.IsNullOrEmpty(model)
                    && !string.IsNullOrEmpty(evalModel)
                    && string.Equals(model, evalModel, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Pair-diversity rule violated: generator and evaluator both resolved to model '{model}'. " +
                        $"Generator: agent={agentId} crew={crewId}. Evaluator: agent={evaluatorAgentId} crew={evaluatorCrewId}. " +
                        $"Set the evaluator crew's EvaluatorModel (or evaluator agent's EvaluatorModel override) to a different model.");
                }

                // Evaluator stays read-only across kinds; no post_pr_comment_reply
                // here. The evaluator scores the deliverable, it does not act on
                // the PR.
                var evalContext = new AgentContext(
                    CrewId: evaluatorCrewId!,
                    Model: evalModel,
                    SoulMd: evalSoulForScoring,
                    WorktreePath: worktreePath,
                    AllowedTools: EvaluatorAllowedTools,
                    MaxTaskBudgetUsd: evalCrew.MaxTaskBudgetUsd,
                    Backend: evalBackend,
                    InvokeMode: AgentInvokeMode.Sdk,
                    WallClockBudget: taskBudget,
                    MaxTurns: taskMaxTurns);

                // The evaluator prompt is lean — just the task context + work item
                // title. The evaluator uses tools to inspect the worktree itself.
                var wiTitle = wi?.Title ?? workItemId;
                var wiPoints = wi?.Points ?? 1;
                var rejectBelow = ScoringConstants.RejectThreshold(wiPoints);
                var evalPrompt = "You are a critical code reviewer scoring agent work.\n\n"
                    + "IMPORTANT: Output **only** a fenced JSON block. Put your full review inside the `notes`\n"
                    + "field. Do NOT write prose outside the JSON. Example shape:\n"
                    + "```json\n"
                    + "{\n"
                    + "  \"score\": 87,\n"
                    + "  \"dims\": { \"correctness\": 35, \"quality\": 22, \"tests\": 13, \"scope\": 9, \"risk\": 8 },\n"
                    + "  \"verdict\": \"iterate\",\n"
                    + "  \"notes\": \"Improvements this iteration: …  Still needs improvement: …  Why this score: …\"\n"
                    + "}\n"
                    + "```\n"
                    + "STRICT JSON: double-quoted keys (`\"score\"`, not `score`), no trailing commas, no // or /* */ comments.\n"
                    + "If your JSON is malformed your entire review is discarded and the iteration is wasted.\n"
                    + "Dimension caps (must stay within bounds — out-of-cap values are silently clamped and\n"
                    + "signal rubric drift): correctness 0..40, quality 0..25, tests 0..15, scope 0..10, risk 0..10.\n"
                    + "Caps sum to 100; `score` must equal the sum and stay in 0..100.\n\n"
                    + $"Scoring rubric (adjusted for {wiPoints}-point work item):\n"
                    + $"- score >= 90: pass (code is correct, tested, ready to merge)\n"
                    + $"- score {rejectBelow}-89: iterate (has issues that need fixing — describe them in notes)\n"
                    + $"- score < {rejectBelow}: reject (fundamentally wrong approach)\n\n"
                    + "Your `notes` field is the per-iteration evaluator summary shown in the live-exec\n"
                    + "Summary tab and the PR description. Structure it in three labeled parts:\n"
                    + "  1. **Improvements this iteration** — what changed and what's better than the prior round.\n"
                    + "  2. **Still needs improvement** — concrete gaps the next iteration must close (cite files\n"
                    + "     and symbols). Call out unaddressed prior-iteration feedback explicitly.\n"
                    + "  3. **Why this score** — dimension-by-dimension rationale (correctness, quality, tests,\n"
                    + "     scope, risk) so the operator can see how the number was derived.\n"
                    + "Do NOT say 'build clean, tests pass' alone. Focus on technical substance. ~3500 chars max\n"
                    + "(the parser truncates at 4000).\n\n"
                    + $"Review iteration {iter} for work item: **{wiTitle}**\n\n"
                    + "Start by running `git diff HEAD~1 --stat -p` to see what was changed, "
                    + "then read the modified files to verify the approach is correct.";

                var invocation = await runner.StartAsync(workItemId, evaluatorAgentId, evalPrompt, evalContext, iterCt);

                // Emit evaluator prompt so the live execution view shows it.
                await this.taskChannel.EmitAsync(workItemId, "task.prompt", new
                {
                    agentId = evaluatorAgentId,
                    crewId = evaluatorCrewId,
                    systemPrompt = string.IsNullOrEmpty(evalSoulForScoring) ? null : evalSoulForScoring,
                    userPrompt = evalPrompt,
                    model = evalModel,
                    backend = evalBackendStr,
                    invokeMode = "sdk",
                    role = "evaluator",
                    iter,
                }, crewId: evaluatorCrewId!, ct: iterCt);

                // Phase 82.1 — durable per-iter evaluator prompt audit.
                // Mirrors the generator-side save above. Same scoped-DI +
                // try/catch pattern: persistence failures log + emit
                // task.prompt.persist-failed but never abort the iteration.
                try
                {
                    using var promptScope = this.scopeFactory.CreateScope();
                    var promptStore = promptScope.ServiceProvider
                        .GetRequiredService<Forge.Api.Services.IterationPromptStore>();
                    await promptStore.SaveAsync(
                        workItemId: workItemId,
                        iter: iter,
                        role: "evaluator",
                        agentId: evaluatorAgentId!,
                        crewId: evaluatorCrewId!,
                        systemPrompt: string.IsNullOrEmpty(evalSoulForScoring) ? null : evalSoulForScoring,
                        userPrompt: evalPrompt,
                        model: evalModel,
                        backend: evalBackendStr,
                        invokeMode: "sdk",
                        ct: iterCt);
                }
                catch (Exception persistEx)
                {
                    this.logger.LogWarning(persistEx,
                        "Phase 82.1 — failed to persist evaluator prompt for {WorkItemId} iter {Iter}",
                        workItemId, iter);
                    try
                    {
                        await this.taskChannel.EmitAsync(workItemId, "task.prompt.persist-failed", new
                        {
                            role = "evaluator",
                            iter,
                            error = persistEx.Message,
                        }, crewId: evaluatorCrewId!, ct: iterCt);
                    }
                    catch
                    {
                        // best-effort — transcript channel itself may be down
                    }
                }

                var evalOutput = new System.Text.StringBuilder();
                decimal evalCost = 0m;
                long evalInTok = 0, evalOutTok = 0;
                // 2026-05-16 Phase 72 — same cache/reasoning/conversation
                // accumulation as the generator side; persisted as a
                // separate evaluator agent_usage row.
                long evalCacheRead = 0, evalCacheWrite = 0, evalReasoning = 0, evalConversationLatest = 0;
                string? evalSessionId = null;

                await foreach (var evt in invocation.Events.WithCancellation(iterCt))
                {
                    var kind = evt.Kind switch
                    {
                        AgentEventKind.Token => "task.token",
                        AgentEventKind.ToolCall => "task.tool-call",
                        AgentEventKind.ToolResult => "task.tool-result",
                        AgentEventKind.Finished => "task.finished",
                        AgentEventKind.Failed => "task.failed",
                        _ => $"task.{evt.Kind.ToString().ToLowerInvariant()}",
                    };
                    await this.taskChannel.EmitAsync(workItemId, kind, new
                    {
                        invocationId = invocation.InvocationId,
                        agentId = evaluatorAgentId,
                        crewId = evaluatorCrewId,
                        text = evt.Text,
                        toolName = evt.ToolName,
                        toolPayload = evt.ToolPayload,
                        error = evt.Error,
                        inputTokens = evt.InputTokens,
                        outputTokens = evt.OutputTokens,
                        // 2026-05-16 Phase 72 — mirror the generator
                        // delegate's enriched event payload so the UI
                        // Tools tab + iter-kpis cover evaluator turns.
                        cacheReadTokens = evt.CacheReadTokens,
                        cacheWriteTokens = evt.CacheWriteTokens,
                        reasoningTokens = evt.ReasoningTokens,
                        conversationTokens = evt.ConversationTokens,
                        toolCallId = evt.ToolCallId,
                        parentToolCallId = evt.ParentToolCallId,
                        durationMs = evt.ToolDurationMs,
                        exitCode = evt.ToolExitCode,
                        mcpServerName = evt.McpServerName,
                        sessionId = evt.SessionId,
                    }, crewId: evaluatorCrewId!, ct: iterCt);

                    if (!string.IsNullOrEmpty(evt.Text)) evalOutput.Append(evt.Text);
                    if (evt.CostUsd.HasValue) evalCost += evt.CostUsd.Value;
                    if (evt.InputTokens is > 0) evalInTok += evt.InputTokens.Value;
                    if (evt.OutputTokens is > 0) evalOutTok += evt.OutputTokens.Value;
                    if (evt.CacheReadTokens is > 0) evalCacheRead += evt.CacheReadTokens.Value;
                    if (evt.CacheWriteTokens is > 0) evalCacheWrite += evt.CacheWriteTokens.Value;
                    if (evt.ReasoningTokens is > 0) evalReasoning += evt.ReasoningTokens.Value;
                    if (evt.ConversationTokens is > 0) evalConversationLatest = evt.ConversationTokens.Value;
                    if (!string.IsNullOrEmpty(evt.SessionId)) evalSessionId = evt.SessionId;
                }

                return new EvaluatorRunOutput(
                    evalOutput.ToString(),
                    evalCost,
                    evalInTok,
                    evalOutTok,
                    TurnSamplesJson: null,
                    CacheReadTokens: evalCacheRead > 0 ? evalCacheRead : null,
                    CacheWriteTokens: evalCacheWrite > 0 ? evalCacheWrite : null,
                    ReasoningTokens: evalReasoning > 0 ? evalReasoning : null,
                    ConversationTokens: evalConversationLatest > 0 ? evalConversationLatest : null,
                    SessionId: evalSessionId);
            }

            // Phase 24 Q24.12 — `loopKind` is computed at the top of the
            // function so downstream consumers (cost ledger tagging, transcript
            // events, evaluator preamble selection, post-loop delivery) can
            // branch on it from both the normal and resume-delivery paths.

            var loopRequest = new DeliverableLoopRequest(
                WorkItemId: workItemId,
                CrewId: crewId,
                GeneratorAgentId: agentId,
                EvaluatorAgentId: evaluatorAgentId ?? agentId,
                Generator: GeneratorDelegate,
                Evaluator: EvaluatorDelegate,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                EvaluatorCrewId: evaluatorCrewId,
                // Scale the iteration cap by points (5 per pt). The runtime
                // PATCH override on the queue row still wins inside the loop
                // (DeliverableLoop re-reads MaxIterOverride each round) — this
                // is just the points-derived default.
                MaxIterations: ScoringConstants.MaxIterationsForPoints(wi?.Points ?? 0),
                Kind: loopKind,
                WorkItemPoints: wi?.Points ?? 1);

            loopResult = await loop.RunAsync(loopRequest, ct);

            } // end if (!isResumeDelivery)

            // For resume-delivery, the synthesized result was set above before
            // the prompt emit was skipped. Otherwise `loopResult` was just
            // assigned by loop.RunAsync. Either way, the rest of the function
            // reads from `result`.
            var result = resumeResult ?? loopResult!;

            this.logger.LogInformation(
                "Deliverable loop completed: wi={Wi} verdict={Verdict} score={Score} iters={Iters} cost={Cost}",
                workItemId, result.FinalVerdict, result.FinalScore, result.Iterations, result.TotalCostUsd);

            // Phase 24 Q24.13 livelock guard — for comment-response runs,
            // track consecutive stalls on the work item. After
            // CommentResponseStallCap stalls in a row, the next sync-tick
            // auto-enqueue refuses with a "blocked" path so the lead can
            // intervene rather than burning more tokens on a comment the
            // agent can't satisfy. Pass / reject reset the counter.
            if (string.Equals(loopKind, DeliverableKinds.CommentResponse, StringComparison.Ordinal))
            {
                var wiForCount = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
                if (wiForCount is not null)
                {
                    var stalled = result.FinalVerdict == "stalled"
                        || result.FinalVerdict == "stalled-budget";
                    if (stalled)
                    {
                        wiForCount.CommentResponseStallCount++;
                        if (wiForCount.CommentResponseStallCount >= PrSyncJob.CommentResponseStallCap)
                        {
                            ForgeMetrics.CommentResponseRepeatedStall.Add(1,
                                new KeyValuePair<string, object?>("workItemId", workItemId));
                        }
                    }
                    else
                    {
                        // pass / reject / paused-eod — clear the counter so the
                        // next stall-streak starts fresh from zero.
                        wiForCount.CommentResponseStallCount = 0;
                    }
                    wiForCount.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            // (4) On pass, push the branch server-side and create a PR.
            //     We don't rely on the agent having pushed — agents
            //     frequently skip the push, causing delivery failures.
            if (result.FinalVerdict == "pass")
            {
                // (4a) Server-side push — ensure branch exists on remote.
                var pushCwd = !string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath)
                    ? worktreePath
                    : repoCloneRoot;
                if (!string.IsNullOrEmpty(pushCwd) && Directory.Exists(pushCwd))
                {
                    // Auto-commit any uncommitted changes the agent left behind.
                    var (statusCode, statusOut, _) = await RunGitAsync(pushCwd, "status", "--porcelain");
                    if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
                    {
                        this.logger.LogInformation(
                            "Auto-committing uncommitted changes: wi={Wi}", workItemId);
                        await RunGitAsync(pushCwd, "add", "-A");
                        await RunGitAsync(pushCwd, "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");
                        await ScrubBuildArtifactsFromIndexAsync(pushCwd);
                        await RunGitAsync(pushCwd, "commit", "-m",
                            $"chore: auto-commit for {workItemId}", "--allow-empty");
                    }

                    // Fetch to update tracking refs, then check if the branch
                    // has any commits ahead of the base. If not, skip push + PR
                    // — the agent produced no changes.
                    await RunGitAsync(pushCwd, "fetch", "origin").ConfigureAwait(false);
                    var (countCode, countOut, _) = await RunGitAsync(
                        pushCwd, "rev-list", "--count", $"origin/{baseBranch}..HEAD");
                    var commitsAhead = 0;
                    if (countCode == 0 && int.TryParse(countOut.Trim(), out var n)) commitsAhead = n;

                    if (commitsAhead == 0)
                    {
                        this.logger.LogWarning(
                            "Branch has 0 commits ahead of {Base} — agent produced no changes. Skipping push + PR: wi={Wi}",
                            baseBranch, workItemId);
                        await this.taskChannel.EmitAsync(workItemId, "task.no-changes", new
                        {
                            agentId, crewId, branch = branchName,
                            reason = $"Branch ws/{workItemId} has 0 commits ahead of {baseBranch}",
                        }, crewId: crewId, ct: ct);
                    }
                    else
                    {
                    // ── Phase 26 — build/test gate ────────────────────────────
                    // Run the repo's build + test commands before pushing the
                    // branch. If either fails the delivery is aborted so we
                    // never push broken code or create a PR that can't build.
                    var buildTestPassed = true;
                    if (wiRepo is not null
                        && (!string.IsNullOrWhiteSpace(wiRepo.BuildCommand)
                            || !string.IsNullOrWhiteSpace(wiRepo.TestCommand)))
                    {
                        // (3a) Build gate
                        if (!string.IsNullOrWhiteSpace(wiRepo.BuildCommand))
                        {
                            this.logger.LogInformation(
                                "Running build gate: wi={Wi} cmd={Cmd}", workItemId, wiRepo.BuildCommand);
                            await this.taskChannel.EmitAsync(workItemId, "task.build-started", new
                            {
                                agentId, crewId, command = wiRepo.BuildCommand,
                            }, crewId: crewId, ct: ct);

                            var (buildCode, buildOut, buildErr, buildTimedOut) = await RunCommandAsync(
                                pushCwd, wiRepo.BuildCommand, wiRepo.BuildTimeoutSec);

                            if (buildCode == 0)
                            {
                                this.logger.LogInformation("Build passed: wi={Wi}", workItemId);
                                await this.taskChannel.EmitAsync(workItemId, "task.build-passed", new
                                {
                                    agentId, crewId, durationHint = "completed",
                                }, crewId: crewId, ct: ct);
                            }
                            else
                            {
                                buildTestPassed = false;
                                var detail = buildTimedOut
                                    ? $"Build timed out after {wiRepo.BuildTimeoutSec ?? DefaultCommandTimeoutSec}s"
                                    : $"Build failed (exit {buildCode})";
                                this.logger.LogWarning(
                                    "Build gate failed: wi={Wi} exit={Code} timedOut={TimedOut}",
                                    workItemId, buildCode, buildTimedOut);
                                await this.taskChannel.EmitAsync(workItemId, "task.build-failed", new
                                {
                                    agentId, crewId,
                                    exitCode = buildCode,
                                    timedOut = buildTimedOut,
                                    stdout = buildOut.Length > 4000 ? buildOut[^4000..] : buildOut,
                                    stderr = buildErr.Length > 4000 ? buildErr[^4000..] : buildErr,
                                    reason = detail,
                                }, crewId: crewId, ct: ct);
                            }
                        }

                        // (3b) Test gate — only run if build passed
                        if (buildTestPassed && !string.IsNullOrWhiteSpace(wiRepo.TestCommand))
                        {
                            this.logger.LogInformation(
                                "Running test gate: wi={Wi} cmd={Cmd}", workItemId, wiRepo.TestCommand);
                            await this.taskChannel.EmitAsync(workItemId, "task.test-started", new
                            {
                                agentId, crewId, command = wiRepo.TestCommand,
                            }, crewId: crewId, ct: ct);

                            var (testCode, testOut, testErr, testTimedOut) = await RunCommandAsync(
                                pushCwd, wiRepo.TestCommand, wiRepo.TestTimeoutSec);

                            if (testCode == 0)
                            {
                                this.logger.LogInformation("Tests passed: wi={Wi}", workItemId);
                                await this.taskChannel.EmitAsync(workItemId, "task.test-passed", new
                                {
                                    agentId, crewId, durationHint = "completed",
                                }, crewId: crewId, ct: ct);
                            }
                            else
                            {
                                buildTestPassed = false;
                                var detail = testTimedOut
                                    ? $"Tests timed out after {wiRepo.TestTimeoutSec ?? DefaultCommandTimeoutSec}s"
                                    : $"Tests failed (exit {testCode})";
                                this.logger.LogWarning(
                                    "Test gate failed: wi={Wi} exit={Code} timedOut={TimedOut}",
                                    workItemId, testCode, testTimedOut);
                                await this.taskChannel.EmitAsync(workItemId, "task.test-failed", new
                                {
                                    agentId, crewId,
                                    exitCode = testCode,
                                    timedOut = testTimedOut,
                                    stdout = testOut.Length > 4000 ? testOut[^4000..] : testOut,
                                    stderr = testErr.Length > 4000 ? testErr[^4000..] : testErr,
                                    reason = detail,
                                }, crewId: crewId, ct: ct);
                            }
                        }

                        // After build/test, re-check for any newly modified
                        // files (e.g., generated code, lockfiles) and auto-commit.
                        if (buildTestPassed)
                        {
                            var (postStatus, postOut2, _) = await RunGitAsync(pushCwd, "status", "--porcelain");
                            if (postStatus == 0 && !string.IsNullOrWhiteSpace(postOut2))
                            {
                                this.logger.LogInformation(
                                    "Auto-committing post-build/test changes: wi={Wi}", workItemId);
                                await RunGitAsync(pushCwd, "add", "-A");
                                await RunGitAsync(pushCwd, "rm", "-r", "--cached", "--ignore-unmatch", "--", ".forge/");
                                await ScrubBuildArtifactsFromIndexAsync(pushCwd);
                                await RunGitAsync(pushCwd, "commit", "-m",
                                    $"chore: post-build auto-commit for {workItemId}", "--allow-empty");
                            }
                        }
                    }

                    if (!buildTestPassed)
                    {
                        // Abort delivery — build or tests failed.
                        await this.CompleteQueueRowAsync(workItemId, ct);
                        await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                        await this.taskChannel.EmitAsync(workItemId, "task.delivery-failed", new
                        {
                            agentId,
                            crewId,
                            branchPushed = false,
                            prCreated = false,
                            reason = "Build or tests failed — PR not created.",
                        }, crewId: crewId, ct: ct);
                        await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                        {
                            agentId, crewId,
                            error = "Build or tests failed — PR not created.",
                        }, crewId: crewId, ct: ct);
                        SystemNotify.AgentFailed(workItemId, "Build or tests failed");
                        return;
                    }

                    // ── End Phase 26 gate ─────────────────────────────────────

                    this.logger.LogInformation(
                        "Server-side push: wi={Wi} branch={Branch} cwd={Cwd}",
                        workItemId, branchName, pushCwd);
                    var (pushCode, _, pushErr) = await RunGitAsync(
                        pushCwd, "push", "--force-with-lease", "origin", branchName);
                    if (pushCode != 0)
                    {
                        // Retry with --force when tracking refs are stale from prior runs.
                        this.logger.LogWarning(
                            "force-with-lease failed, retrying with --force: {Err}", pushErr);
                        (pushCode, _, pushErr) = await RunGitAsync(
                            pushCwd, "push", "--force", "origin", branchName);
                    }

                    if (pushCode == 0)
                    {
                        this.logger.LogInformation(
                            "Server-side push succeeded: wi={Wi} branch={Branch}",
                            workItemId, branchName);
                        await this.taskChannel.EmitAsync(workItemId, "task.branch-pushed", new
                        {
                            agentId, crewId, branch = branchName,
                        }, crewId: crewId, ct: ct);
                    }
                    else
                    {
                        this.logger.LogWarning(
                            "Server-side push failed: wi={Wi} exit={Code} err={Err}",
                            workItemId, pushCode, pushErr);
                        await this.taskChannel.EmitAsync(workItemId, "task.push-failed", new
                        {
                            agentId, crewId, branch = branchName, error = pushErr,
                        }, crewId: crewId, ct: ct);
                    }
                    } // end: commitsAhead > 0
                } // end: pushCwd exists

                // (4b) Create PR via provider API.
                // Phase 24 Q24.12 — comment-response runs target the EXISTING PR,
                // never open a new one. The push above flushes the new commit;
                // the build re-runs naturally on push and the existing PR
                // continues its lifecycle. Skip the entire PR-create block
                // for this kind.
                // Phase 27 Q27.9 — build-fix never opens a new PR (it pushes
                // to the existing branch and the build re-runs on push).
                var skipPrCreate = string.Equals(loopKind, DeliverableKinds.CommentResponse, StringComparison.Ordinal)
                    || string.Equals(loopKind, DeliverableKinds.BuildFix, StringComparison.Ordinal);

                // Track PR creation locally — VerifyPrCreatedAsync checks ALL
                // historical transcript events, so a task.pr-opened from a
                // previous execution would incorrectly pass verification.
                var prCreatedInThisExecution = false;
                string? prErrorDetail = null;
                if (skipPrCreate)
                {
                    this.logger.LogInformation(
                        "Comment-response run — skipping PR create; delivering via push to existing PR: wi={Wi}", workItemId);
                    await this.taskChannel.EmitAsync(workItemId, "task.comment-response-pushed", new
                    {
                        agentId, crewId, branch = branchName,
                    }, crewId: crewId, ct: ct);
                    // Treat as PR-created for the verification path so the loop
                    // exits the in-review transition without trying to find a
                    // new PR row that doesn't exist.
                    prCreatedInThisExecution = true;
                }
                else
                try
                {
                    // Resolve the correct provider based on the work item's repo kind.
                    var providers = scope.ServiceProvider
                        .GetRequiredService<IEnumerable<Forge.Api.Providers.IForgeProvider>>();
                    var repoKind = !string.IsNullOrEmpty(wiRepo?.Kind) ? wiRepo.Kind : "ado";
                    var (prRepoName, prProjectName) = Forge.Api.Providers.PrCoordsResolver.Resolve(wiRepo, repoKind);

                    var provider = providers.FirstOrDefault(p => p.Kind == repoKind && p.IsConfigured)
                        ?? providers.FirstOrDefault(p => p.IsConfigured);
                    if (provider is null)
                        throw new InvalidOperationException($"No configured provider for kind '{repoKind}'");

                    var prTitle = wi?.Title ?? workItemId;

                    // Resolve agent full name + role for the PR header.
                    var agentRow = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
                    var agentDisplayName = agentRow?.Name ?? agentId;
                    var agentRole = string.IsNullOrEmpty(agentRow?.Role) ? "agent" : agentRow!.Role;

                    // Pull the latest evaluator notes so the PR description
                    // explains what changed, not just the work item brief.
                    var lastDeliverable = (await db.Deliverables.AsNoTracking()
                        .Where(d => d.WorkItemId == workItemId)
                        .ToListAsync(ct))
                        .OrderByDescending(d => d.CreatedAt)
                        .ThenByDescending(d => d.Iter)
                        .FirstOrDefault();
                    var changeNotes = lastDeliverable?.Notes ?? string.Empty;
                    var generatorSummary = lastDeliverable?.GeneratorSummary ?? string.Empty;

                    // Extract the "## Change Summary" section from the generator output if present.
                    var summaryIdx = generatorSummary.IndexOf("## Change Summary", StringComparison.OrdinalIgnoreCase);
                    if (summaryIdx >= 0)
                    {
                        generatorSummary = generatorSummary[(summaryIdx + "## Change Summary".Length)..].Trim();
                        // Take until the next ## heading or end
                        var nextHeading = generatorSummary.IndexOf("\n## ", StringComparison.Ordinal);
                        if (nextHeading > 0) generatorSummary = generatorSummary[..nextHeading].Trim();
                    }
                    if (generatorSummary.Length > 800) generatorSummary = generatorSummary[..800] + "...";

                    // Build rich PR description. Includes work item link,
                    // agent avatar from allocation.json, full name + role,
                    // change summary from evaluator notes. NO cost or token
                    // counts — the user expressly does not want PRs to leak
                    // spend numbers into the merge history.
                    // Resolve evaluator agent for the PR header
                    var evalAgentRow = !string.IsNullOrEmpty(evaluatorAgentId)
                        ? await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evaluatorAgentId, ct)
                        : null;
                    var evalDisplayName = evalAgentRow?.Name ?? evaluatorAgentId;

                    // Load real avatar filenames from allocation.json.
                    string? genAvatarFile = null, evalAvatarFile = null;
                    try
                    {
                        var allocPath = Path.Combine(
                            Forge.Api.Persistence.ForgeSeed.FindRepoRoot(), "agents", "allocation.json");
                        if (File.Exists(allocPath))
                        {
                            using var allocDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(allocPath));
                            foreach (var el in allocDoc.RootElement.EnumerateArray())
                            {
                                var elId = el.GetProperty("id").GetString();
                                if (elId == agentId && el.TryGetProperty("avatar", out var av))
                                    genAvatarFile = av.GetString();
                                if (elId == evaluatorAgentId && el.TryGetProperty("avatar", out var ev))
                                    evalAvatarFile = ev.GetString();
                            }
                        }
                    }
                    catch { /* fallback to no avatar */ }

                    // Avatar emoji — use a simple character instead of base64
                    // data URIs. Base64-encoded PNGs are ~68K each, far exceeding
                    // ADO's 4000-char PR description limit.
                    var genEmoji = !string.IsNullOrEmpty(genAvatarFile) ? "🤖 " : "";
                    var evalEmoji = !string.IsNullOrEmpty(evalAvatarFile) ? "🤖 " : "";

                    var prBodyBuilder = new System.Text.StringBuilder();
                    prBodyBuilder.AppendLine($"## {prTitle}");
                    prBodyBuilder.AppendLine();
                    var wiLine = $"**Work Item:** {workItemId}";
                    if (wi?.ProviderRef is int provRef) wiLine += $" (ADO #{provRef})";
                    prBodyBuilder.AppendLine(wiLine);
                    prBodyBuilder.AppendLine($"**Score:** {result.FinalScore} | **Iterations:** {result.Iterations}");
                    prBodyBuilder.AppendLine();

                    // Agent header — text only to stay within ADO's 4000-char limit.
                    prBodyBuilder.Append($"{genEmoji}**Generator:** {agentDisplayName}");
                    if (!string.IsNullOrEmpty(evalDisplayName))
                    {
                        prBodyBuilder.Append($" | {evalEmoji}**Evaluator:** {evalDisplayName}");
                    }
                    prBodyBuilder.AppendLine();
                    prBodyBuilder.AppendLine();

                    // What changed — content only, no repeated agent names.
                    prBodyBuilder.AppendLine("### What changed");
                    prBodyBuilder.AppendLine();
                    if (!string.IsNullOrWhiteSpace(generatorSummary))
                    {
                        prBodyBuilder.AppendLine(generatorSummary);
                    }
                    else
                    {
                        prBodyBuilder.AppendLine("_No change summary provided._");
                    }
                    prBodyBuilder.AppendLine();

                    // Evaluator review — content only.
                    if (!string.IsNullOrEmpty(evalDisplayName))
                    {
                        prBodyBuilder.AppendLine("### Evaluator Review");
                        prBodyBuilder.AppendLine();
                        if (!string.IsNullOrWhiteSpace(changeNotes))
                        {
                            var notes = changeNotes.Length > 800 ? changeNotes[..800] + "..." : changeNotes;
                            prBodyBuilder.AppendLine(notes);
                        }
                        else
                        {
                            prBodyBuilder.AppendLine("_No review comment provided._");
                        }
                    }
                    prBodyBuilder.AppendLine();
                    prBodyBuilder.AppendLine("---");
                    prBodyBuilder.AppendLine("*Auto-generated by Forge.*");
                    var prBody = prBodyBuilder.ToString();

                    // ADO enforces a 4000-character limit on PR descriptions.
                    if (prBody.Length > 3900)
                    {
                        prBody = prBody[..3900] + "\n\n…_(truncated — full details in evaluator notes)_";
                    }

                    // Pre-flight validation — refuse to call the provider
                    // when the PR would be missing required context.
                    var wiRefForLink = wi?.Provider == "ado" ? wi.ProviderRef : null;
                    if (wiRefForLink is null && string.IsNullOrEmpty(workItemId))
                        throw new InvalidOperationException("PR validation failed: missing work item association.");
                    if (string.IsNullOrWhiteSpace(prBody))
                        throw new InvalidOperationException("PR validation failed: empty PR body.");
                    if (string.IsNullOrWhiteSpace(generatorSummary) && string.IsNullOrWhiteSpace(changeNotes))
                        throw new InvalidOperationException("PR validation failed: neither generator summary nor evaluator notes are present.");
                    if (string.IsNullOrWhiteSpace(agentDisplayName))
                        throw new InvalidOperationException("PR validation failed: missing agent identity.");

                    var pr = await provider.CreatePullRequestAsync(
                        new Forge.Api.Providers.NewPullRequestRequest(
                            branchName, baseBranch, prTitle, prBody,
                            RepositoryName: prRepoName,
                            ProjectName: prProjectName,
                            WorkItemRef: wiRefForLink),
                        Forge.Api.Providers.ProviderCallContext.Anonymous, ct);

                    await this.taskChannel.EmitAsync(workItemId, "task.pr-opened", new
                    {
                        prId = pr.Id,
                        prTitle = pr.Title,
                        branch = branchName,
                        baseBranch,
                        webUrl = pr.WebUrl,
                    }, crewId: crewId, ct: ct);

                    prCreatedInThisExecution = true;
                    this.logger.LogInformation(
                        "PR created: wi={Wi} pr={Pr} branch={Branch}", workItemId, pr.Id, branchName);

                    // Phase 25 — upsert a local PrRow so the work-item card shows the
                    // PR badge immediately (before the periodic PR sync discovers it).
                    // Phase 46 — inherit the parent story's AutoMerge flag so a
                    // story-level "auto-merge me through" pre-authorisation
                    // propagates to every PR opened for any task in the story.
                    try
                    {
                        var inheritedAutoMerge = await Forge.Api.Services.Stories.SpawnAutoMergeResolver
                            .ResolveAsync(control, db, workItemId, ct);
                        var existingPr = await db.Prs.FirstOrDefaultAsync(p => p.Id == pr.Id, ct);
                        if (existingPr is null)
                        {
                            db.Prs.Add(new Persistence.PrRow
                            {
                                Id = pr.Id,
                                Col = "implementation",
                                Branch = branchName,
                                Base = baseBranch,
                                Title = pr.Title,
                                WorkItemId = workItemId,
                                RepoId = wiRepo?.Id,
                                IsActive = true,
                                LastSyncStatus = "idle",
                                WebUrl = pr.WebUrl,
                                Provider = wiRepo?.Kind,
                                AutoMerge = inheritedAutoMerge,
                                UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
                            });
                        }
                        else
                        {
                            existingPr.WorkItemId ??= workItemId;
                            existingPr.WebUrl ??= pr.WebUrl;
                            // Backfill AutoMerge on a row that pre-existed
                            // before the resolver was wired — only when
                            // inheriting "true" so we don't clobber an
                            // explicit user-set false.
                            if (inheritedAutoMerge && !existingPr.AutoMerge)
                            {
                                existingPr.AutoMerge = true;
                            }
                        }
                        await db.SaveChangesAsync(ct);
                    }
                    catch (Exception prRowEx)
                    {
                        this.logger.LogDebug(prRowEx, "Failed to upsert local PrRow for wi={Wi} pr={Pr}", workItemId, pr.Id);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "PR creation failed for wi={Wi}", workItemId);
                    prErrorDetail = ex.Message;
                    await this.taskChannel.EmitAsync(workItemId, "task.pr-failed", new
                    {
                        error = ex.Message,
                        branch = branchName,
                    }, crewId: crewId, ct: ct);
                }

                // (5) Verify delivery: branch pushed + PR created in THIS execution.
                var branchPushed = await this.VerifyBranchPushedAsync(
                    repoCloneRoot ?? worktreePath, branchName, ct);
                var prCreated = prCreatedInThisExecution;

                if (branchPushed && prCreated)
                {
                    this.logger.LogInformation(
                        "Delivery verified: wi={Wi} branch={Branch}", workItemId, branchName);

                    // Transition work item to "in-review" — PR review cycle
                    // drives the subsequent in-review → resolved → closed transitions.
                    try
                    {
                        var wiRow = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
                        if (wiRow is not null)
                        {
                            var prevState = wiRow.State;
                            wiRow.State = "in-review";
                            wiRow.UpdatedAt = DateTimeOffset.UtcNow;
                            // Phase 22 — record pr-opened assignment transition.
                            Forge.Api.Endpoints.WorkItemsEndpoints.RecordAssignmentTransition(
                                db, wiRow, prevState, "in-review");
                            await db.SaveChangesAsync(ct);
                            await this.BroadcastWorkItemChangedAsync(wiRow, "state-changed", ct, db);
                        }
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogDebug(ex, "Work item state transition failed for wi={Wi}", workItemId);
                    }
                }
                else
                {
                    var deliveryReason = !branchPushed
                        ? "Branch not pushed to remote"
                        : !string.IsNullOrEmpty(prErrorDetail)
                            ? $"PR creation failed: {prErrorDetail}"
                            : "PR not created";
                    this.logger.LogWarning(
                        "Delivery verification failed: wi={Wi} branchPushed={Pushed} prCreated={Pr} detail={Detail}",
                        workItemId, branchPushed, prCreated, deliveryReason);
                    await this.taskChannel.EmitAsync(workItemId, "task.delivery-failed", new
                    {
                        agentId,
                        crewId,
                        branchPushed,
                        prCreated,
                        reason = deliveryReason,
                    }, crewId: crewId, ct: ct);

                    // Mark as failed — delivery is incomplete.
                    await this.CompleteQueueRowAsync(workItemId, ct);
                    await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                    await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                    {
                        agentId,
                        crewId,
                        error = deliveryReason,
                    }, crewId: crewId, ct: ct);
                    SystemNotify.AgentFailed(workItemId, deliveryReason);
                    return; // skip the normal completion path
                }
            }
            else
            {
                // Non-pass verdict (stalled, stalled-budget, reject, iterate-cap):
                // the loop completed without a deliverable worth shipping, so
                // PR creation never happened. Surface this as "failed" rather
                // than "completed" so the ExecBadge stops pulsing AND clearly
                // distinguishes from the success path. Without this branch the
                // ExecutionStatus flipped to "completed" and the operator had
                // no signal that the run ended without a PR.
                this.logger.LogWarning(
                    "Deliverable loop ended without pass verdict: wi={Wi} verdict={Verdict} score={Score}",
                    workItemId, result.FinalVerdict, result.FinalScore);

                await this.taskChannel.EmitAsync(workItemId, "task.delivery-skipped", new
                {
                    agentId,
                    crewId,
                    verdict = result.FinalVerdict,
                    score = result.FinalScore,
                    iterations = result.Iterations,
                    reason = $"Loop ended with verdict='{result.FinalVerdict}' (score={result.FinalScore}); no PR created.",
                }, crewId: crewId, ct: ct);

                await this.CompleteQueueRowAsync(workItemId, ct);

                // Phase 51 — stalled-budget is recoverable: the operator can
                // raise the crew's MaxTaskBudgetUsd and the reconciler will
                // resume on the next 15s tick (or after MaxWait — default
                // 10m — with the pair rotated). Park the row in
                // CooldownBudget rather than Failed so the ExecBadge can
                // render "auto-resuming in Xm" and the reconciler picks it
                // up. Other non-pass verdicts (stalled iter-cap,
                // stalled-no-convergence, reject) are dead ends — agent
                // ran out of ideas; restart with operator steering is the
                // correct next move, not auto-retry.
                if (string.Equals(result.FinalVerdict, "stalled-budget", StringComparison.Ordinal))
                {
                    var maxWait = this.cooldownOptions.CurrentValue.MaxWait;
                    var cooldownUntil = DateTimeOffset.UtcNow.Add(maxWait);
                    var pair = new[] { agentId, evaluatorAgentId ?? agentId };
                    var pairJson = System.Text.Json.JsonSerializer.Serialize(pair);

                    var liveWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
                    if (liveWi is not null)
                    {
                        liveWi.ExecutionStatus = ExecutionStatuses.CooldownBudget;
                        liveWi.BudgetCooldownUntilUtc = cooldownUntil;
                        liveWi.LastPairAgentIdsJson = pairJson;
                        liveWi.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        await this.BroadcastWorkItemChangedAsync(liveWi, "cooldown-budget", ct, db);
                    }

                    await this.taskChannel.EmitAsync(workItemId, "task.budget-cooldown", new
                    {
                        agentId,
                        crewId,
                        evaluatorAgentId,
                        cooldownUntilUtc = cooldownUntil,
                        maxWaitSeconds = (int)maxWait.TotalSeconds,
                        priorPair = pair,
                        verdict = result.FinalVerdict,
                        score = result.FinalScore,
                        iterations = result.Iterations,
                        reason = "Per-task budget cap hit. Auto-resume when the cap is raised (immediate) or after the cooldown window (with pair rotation).",
                    }, crewId: crewId, ct: ct);
                    return;
                }

                // Phase 66 — stalled-questions verdict: operator must
                // answer questions via the conversation wizard before the
                // loop can resume. DeliverableLoop already set
                // ExecutionStatus=AwaitingAnswers and stamped the
                // pair-assignment as Cancelled; CompleteQueueRowAsync
                // above has flushed the queue row. Emit a parked event
                // for observability and return — TaskExecutionEndpoints.
                // AnswerQuestions / DismissQuestions kicks the resume
                // when the operator clears the gate.
                if (string.Equals(result.FinalVerdict, "stalled-questions", StringComparison.Ordinal))
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.questions-parked", new
                    {
                        agentId,
                        crewId,
                        evaluatorAgentId,
                        verdict = result.FinalVerdict,
                        score = result.FinalScore,
                        iterations = result.Iterations,
                        reason = "Operator questions unanswered. Auto-resume when answers are recorded via the conversation wizard.",
                    }, crewId: crewId, ct: ct);
                    return;
                }

                // Phase 52 — sloppy verdict (score plateau, generator token
                // cap, or iter cap). Route to pending-reassign so the
                // SloppyReassignmentReconciler picks it up next tick. The
                // reconciler decides whether to reassign to a fresh pair
                // (PairAttemptCount < MaxPairAttempts) or terminate with
                // the locked failure reason. We append the current pair
                // to PriorPairsJson here so the eventual picker excludes
                // them on the reassignment claim.
                if (string.Equals(result.FinalVerdict, "sloppy", StringComparison.Ordinal))
                {
                    var liveWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
                    if (liveWi is not null)
                    {
                        liveWi.ExecutionStatus = ExecutionStatuses.PendingReassign;
                        liveWi.UpdatedAt = DateTimeOffset.UtcNow;
                        var priorPairs = new List<string[]>();
                        if (!string.IsNullOrWhiteSpace(liveWi.PriorPairsJson))
                        {
                            try
                            {
                                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string[]>>(liveWi.PriorPairsJson);
                                if (parsed is not null) priorPairs = parsed;
                            }
                            catch (System.Text.Json.JsonException) { /* malformed — start fresh */ }
                        }
                        var currentPair = new[] { agentId ?? string.Empty, evaluatorAgentId ?? string.Empty };
                        var alreadyLast = priorPairs.Count > 0
                            && priorPairs[priorPairs.Count - 1].Length == 2
                            && string.Equals(priorPairs[priorPairs.Count - 1][0], currentPair[0], StringComparison.Ordinal)
                            && string.Equals(priorPairs[priorPairs.Count - 1][1], currentPair[1], StringComparison.Ordinal);
                        if (!alreadyLast) priorPairs.Add(currentPair);
                        liveWi.PriorPairsJson = System.Text.Json.JsonSerializer.Serialize(priorPairs);
                        await db.SaveChangesAsync(ct);
                        await this.BroadcastWorkItemChangedAsync(liveWi, "pending-reassign", ct, db);
                    }

                    await this.taskChannel.EmitAsync(workItemId, "task.sloppy-parked", new
                    {
                        agentId,
                        crewId,
                        evaluatorAgentId,
                        verdict = result.FinalVerdict,
                        score = result.FinalScore,
                        iterations = result.Iterations,
                        sloppyReason = result.SloppyReason,
                        pairAttemptCount = liveWi?.PairAttemptCount ?? 1,
                        maxPairAttempts = ScoringConstants.MaxPairAttempts,
                        reason = "Sloppy verdict — parked for SloppyReassignmentReconciler. New pair picked up to MaxPairAttempts; terminal failure thereafter.",
                    }, crewId: crewId, ct: ct);
                    return;
                }

                await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                {
                    agentId,
                    crewId,
                    error = $"Run ended with verdict='{result.FinalVerdict}' (score={result.FinalScore}). No PR was created.",
                }, crewId: crewId, ct: ct);
                return;
            }

            // Success — delivery verified above (branch pushed + PR opened).
            // Transition queue row to completed and explicitly set
            // ExecutionStatus = Completed. Status must be set explicitly:
            // CompleteQueueRowAsync no longer touches ExecutionStatus, so a
            // failure path that completes the queue row but fails to set
            // Failed (e.g. exception between calls) cannot leak through as
            // a stale Completed.
            await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Completed, ct);
            await this.CompleteQueueRowAsync(workItemId, ct);
            await this.taskChannel.EmitAsync(workItemId, "task.completed", new
            {
                agentId,
                crewId,
                verdict = result.FinalVerdict,
                score = result.FinalScore,
                iterations = result.Iterations,
            }, crewId: crewId, ct: ct);
            SystemNotify.AgentCompleted(workItemId, agentId, result.FinalScore);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* host shutting down — scope already disposed */ }
        catch (Exception ex)
        {
            // When the host is shutting down, ChildProcessTracker kills the
            // copilot CLI child process before the CancellationToken (which
            // is CancellationToken.None for fire-and-forget agent tasks) is
            // cancelled. The resulting JSON-RPC / transport error surfaces
            // here as a generic Exception, not OperationCanceledException.
            // If we mark the queue row completed + WI failed, boot recovery
            // on the next startup finds nothing to recover and the work item
            // stays stuck in "failed" forever.
            //
            // Fix: when shuttingDown is set (StopAsync ran), leave the queue
            // row in picked-up. SweepBootRecoveryAsync on the next startup
            // resets it to queued, and DeliverableLoop seeds iter from the
            // last persisted deliverable — true resume-from-disk.
            if (this.shuttingDown)
            {
                this.logger.LogInformation(
                    "Agent interrupted by shutdown: wi={Wi} agent={Agent} crew={Crew} — leaving picked-up for boot recovery",
                    workItemId, agentId, crewId);
                try
                {
                    await this.taskChannel.EmitAsync(workItemId, "task.interrupted", new
                    {
                        agentId,
                        crewId,
                        reason = "shutdown",
                        error = ex.Message,
                    }, crewId: crewId, ct: CancellationToken.None);
                }
                catch { /* best-effort — host may be tearing down */ }
                return;
            }

            this.logger.LogWarning(ex,
                "Agent invocation failed: wi={Wi} agent={Agent} crew={Crew}",
                workItemId, agentId, crewId);
            try
            {
                await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                {
                    agentId,
                    crewId,
                    error = ex.Message,
                }, crewId: crewId, ct: ct);

                // On failure, also complete the queue row so it doesn't stay
                // picked-up forever (abandoned recovery is a 24h fallback).
                await this.CompleteQueueRowAsync(workItemId, ct);

                // Phase 19 — mark execution as failed.
                await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                SystemNotify.AgentFailed(workItemId, ex.Message);
            }
            catch (ObjectDisposedException) { /* host shutting down */ }
        }
        finally
        {
            this.worktreeFeed.Unsubscribe(workItemId);
            // Phase 44 — release the agent slot.
            try { await this.slots.ReleaseByTaskAsync(workItemId, CancellationToken.None); }
            catch { /* best-effort — don't block cleanup */ }
        }
    }

    private async Task CompleteQueueRowAsync(string workItemId, CancellationToken ct)
    {
        // Queue-row completion only. ExecutionStatus is owned by the caller
        // and must be set explicitly via SetExecutionStatusAsync — the
        // success path sets Completed AFTER delivery has been verified
        // (branch pushed + PR opened); failure paths set Failed before
        // calling this method. Coupling "queue row completed" to
        // "execution status = completed" let failure paths leak through:
        // CompleteQueueRowAsync set Completed implicitly, then a follow-up
        // SetExecutionStatusAsync(Failed) had to overwrite it, and any
        // exception (scope dispose, ct cancellation) between the two left
        // the work item showing Completed even though delivery failed and
        // no PR was created.
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            // Find the picked-up row. Client-side ordering because SQLite
            // can't ORDER BY DateTimeOffset reliably.
            var rows = await db.TaskQueue
                .Where(r => r.WorkItemId == workItemId && r.State == TaskQueueStates.PickedUp)
                .ToListAsync(ct);
            var row = rows.OrderByDescending(r => r.EnqueuedAt).FirstOrDefault();
            if (row is not null)
            {
                await queue.CompleteAsync(row.Id, ct);
                await this.BroadcastQueueChangedAsync(row, "completed", ct);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to complete queue row for {Wi}", workItemId);
        }
    }

    /// <summary>
    /// Phase 19 — update <see cref="WorkItemRow.ExecutionStatus"/> from a
    /// separate scope (used by the failure path where CompleteQueueRowAsync
    /// already ran in a different scope).
    /// </summary>
    private async Task SetExecutionStatusAsync(string workItemId, string status, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is not null)
            {
                wi.ExecutionStatus = status;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await this.BroadcastWorkItemChangedAsync(wi, $"exec-{status}", ct, db);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to set execution status for {Wi}", workItemId);
        }
    }

    // ── Phase 19 — delivery helpers ───────────────────────────────────────

    /// <summary>
    /// Build the delivery-phase prompt. Instructs the agent to push the
    /// branch and create a PR via the Forge API endpoint.
    /// </summary>
    private static string BuildDeliveryPrompt(
        string workItemId, string branchName, string baseBranch, WorkItemRow? wi)
    {
        var title = wi?.Title ?? workItemId;
        var safeTitle = title.Replace("\"", "\\\"");
        var providerRef = wi?.ProviderRef?.ToString() ?? "";

        // Build a meaningful PR description
        var bodyLines = new System.Text.StringBuilder();
        bodyLines.Append("## " + title + "\\n\\n");
        bodyLines.Append("**Work Item:** " + workItemId);
        if (!string.IsNullOrEmpty(providerRef))
            bodyLines.Append(" (ADO #" + providerRef + ")");
        bodyLines.Append("\\n\\n");
        if (!string.IsNullOrEmpty(wi?.DescriptionMd))
        {
            // ADO caps PR descriptions at 4000 chars (the next overload adds the
            // "Auto-generated by Forge." footer + the body header, so we leave
            // ~400 chars of headroom and only truncate when we'd otherwise overflow).
            const int MaxDescChars = 3600;
            var desc = wi.DescriptionMd.Length > MaxDescChars
                ? wi.DescriptionMd[..MaxDescChars] + "\n\n_…description truncated to fit ADO's 4000-char limit._"
                : wi.DescriptionMd;
            desc = desc.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
            bodyLines.Append("### Description\\n" + desc + "\\n\\n");
        }
        bodyLines.Append("---\\n*Auto-generated by Forge.*");

        var jsonBody = "{\"branch\":\"" + branchName + "\",\"baseBranch\":\"" + baseBranch
            + "\",\"title\":\"" + safeTitle
            + "\",\"body\":\"" + bodyLines + "\"}";

        return "You have finished implementing the changes for work item " + workItemId + ".\n"
            + "Now you need to deliver your work by performing these two steps:\n\n"
            + "## Step 1: Push the branch to the remote\n"
            + "Run the following git commands:\n"
            + "```\n"
            + "git add -A\n"
            + "git status\n"
            + "git diff --cached --stat\n"
            + "git commit -m \"" + safeTitle + "\" --allow-empty\n"
            + "git push origin " + branchName + "\n"
            + "```\n"
            + "Verify the push succeeded (exit code 0). If it fails, diagnose and retry.\n\n"
            + "## Step 2: Create a Pull Request\n"
            + "Create a PR by calling the Forge API:\n"
            + "```\n"
            + "curl -X POST http://localhost:5181/api/work-items/" + workItemId + "/create-pr "
            + "-H \"Content-Type: application/json\" "
            + "-d '" + jsonBody + "'\n"
            + "```\n"
            + "Verify the response contains a prId. If it fails, check the error and retry once.\n\n"
            + "IMPORTANT:\n"
            + "- Do NOT modify any source code in this step. Only push and create the PR.\n"
            + "- Both steps must succeed for the delivery to be complete.";
    }

    /// <summary>
    /// Verify the branch was pushed to the remote by checking
    /// <c>git ls-remote --heads origin {branch}</c>.
    /// </summary>
    private async Task<bool> VerifyBranchPushedAsync(string cwd, string branchName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        try
        {
            var (code, stdout, _) = await RunGitAsync(cwd, "ls-remote", "--heads", "origin", branchName);
            return code == 0 && !string.IsNullOrWhiteSpace(stdout);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Branch verification failed for {Branch}", branchName);
            return false;
        }
    }

    /// <summary>
    /// Verify a PR was created by checking if a <c>task.pr-opened</c>
    /// transcript event exists for this work item.
    /// </summary>
    private static async Task<bool> VerifyPrCreatedAsync(ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct)
    {
        return await db.TaskTranscriptEvents
            .AnyAsync(e => e.WorkItemId == workItemId && e.Kind == "task.pr-opened", ct);
    }

    /// <summary>
    /// Phase 24.9 — build the comment-response prompt for the agent. Loads
    /// the open PR comments needing response and instructs the agent to
    /// either reply on the thread or commit a fix to the existing PR
    /// branch (no new PR — the existing PR receives the new commit).
    /// </summary>
    private static async Task<string> BuildCommentResponsePromptAsync(ControlDbContext control, ProductDbContext db, string workItemId, string workItemTitle, CancellationToken ct)
    {
        var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.WorkItemId == workItemId, ct);
        if (pr is null)
        {
            return $"Work item: {workItemTitle}\n\nThis task asked you to address PR comments, but no PR is linked to this work item.";
        }

        // SQLite can't ORDER BY DateTimeOffset; materialize then sort.
        var openComments = (await db.PrComments.AsNoTracking()
            .Where(c => c.PrId == pr.Id && c.RequiresAttention)
            .ToListAsync(ct))
            .OrderBy(c => c.CreatedAt)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Work item: {workItemTitle}");
        sb.AppendLine();
        sb.AppendLine($"You are addressing reviewer comments on PR {pr.Id} (branch `{pr.Branch}`, base `{pr.Base}`).");
        sb.AppendLine();
        sb.AppendLine($"There are {openComments.Count} open comment(s) requiring a response.");
        sb.AppendLine();
        var idx = 1;
        foreach (var c in openComments)
        {
            sb.AppendLine($"## Comment {idx} (id: {c.Id})");
            if (!string.IsNullOrEmpty(c.FilePath))
            {
                sb.AppendLine($"Location: `{c.FilePath}`{(c.Line.HasValue ? $" line {c.Line.Value}" : string.Empty)}");
            }
            sb.AppendLine($"Author: {c.AuthorId ?? "unknown"} ({c.AuthorKind})");
            sb.AppendLine();
            sb.AppendLine(c.Body);
            sb.AppendLine();
            idx++;
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine("For each comment, choose ONE of:");
        sb.AppendLine("- **Reply** on the thread when the comment is a question or asks for clarification.");
        sb.AppendLine("  Reply with `Addresses comment <id>` so the linker can map your reply.");
        sb.AppendLine("- **Commit a fix** when the comment requests a code change. Include the tag");
        sb.AppendLine("  `Addresses comment <id>` in the commit message so the linker can map the commit.");
        sb.AppendLine("Push to the existing branch with `git push --force-with-lease origin HEAD`.");
        sb.AppendLine("Do NOT open a new PR — the existing PR will receive the new commit.");
        sb.AppendLine("Do NOT mark comments as resolved — only the human reviewer can resolve threads.");

        return sb.ToString();
    }

    /// <summary>
    /// Phase 27 Q27.10 — build the build-fix prompt for the agent. Carries
    /// the failed build's pipeline header, parsed FailurePoint rows, the
    /// 256 KB log tail, the artifact index (file names + sizes only), the
    /// work-item brief, the PR diff, and recent commit messages. Logs and
    /// artifacts are NOT inlined verbatim — the agent reads specific files
    /// via <c>read_file</c>.
    /// </summary>
    private static async Task<string> BuildBuildFixPromptAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, string title, string desc, CancellationToken ct)
    {
        var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.WorkItemId == workItemId, ct);
        if (pr is null)
        {
            return $"Work item: {title}\n\nThis task asked you to fix a failed build, but no PR is linked to this work item.";
        }

        // Pull the most recent failed build for this PR.
        var failedBuild = await db.Builds.AsNoTracking()
            .Where(b => b.PrId == pr.Id && b.State == "failed")
            .OrderByDescending(b => b.FinishedAt ?? b.StartedAt ?? string.Empty)
            .FirstOrDefaultAsync(ct);
        if (failedBuild is null)
        {
            return $"Work item: {title}\n\nNo failed build found for PR {pr.Id}.";
        }

        var stages = await db.BuildStages.AsNoTracking()
            .Where(s => s.BuildId == failedBuild.Id)
            .OrderBy(s => s.StageOrder)
            .ToListAsync(ct);

        var stageIds = stages.Select(s => s.Id).ToList();
        var failurePoints = await db.BuildFailurePoints.AsNoTracking()
            .Where(f => stageIds.Contains(f.StageId))
            .OrderBy(f => f.StageId).ThenBy(f => f.SourceLogTailOffset)
            .ToListAsync(ct);

        var logRows = await db.BuildStageLogs.AsNoTracking()
            .Where(l => stageIds.Contains(l.StageId))
            .ToListAsync(ct);

        var artifacts = await db.BuildArtifacts.AsNoTracking()
            .Where(a => a.BuildId == failedBuild.Id)
            .ToListAsync(ct);
        var artifactIds = artifacts.Select(a => a.Id).ToList();
        var artifactFiles = await db.BuildArtifactFiles.AsNoTracking()
            .Where(f => artifactIds.Contains(f.ArtifactId))
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Work item: {title}");
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.AppendLine();
            sb.AppendLine("## Original brief");
            sb.AppendLine(desc.Length > 4000 ? desc[..4000] + "…" : desc);
        }
        sb.AppendLine();
        sb.AppendLine($"## Failed build #{failedBuild.Id} ({failedBuild.PipelineName ?? "unknown pipeline"})");
        sb.AppendLine($"- Pipeline: {failedBuild.PipelineName ?? "(none)"} (id={failedBuild.PipelineId ?? "(none)"})");
        sb.AppendLine($"- State: {failedBuild.State}{(string.IsNullOrEmpty(failedBuild.Result) ? string.Empty : $" / result: {failedBuild.Result}")}");
        sb.AppendLine($"- Branch: `{failedBuild.Branch ?? pr.Branch}`");
        sb.AppendLine($"- Triggering commit: {failedBuild.TriggeringCommit ?? "(unknown)"}");
        if (!string.IsNullOrEmpty(failedBuild.WebUrl)) sb.AppendLine($"- Web: {failedBuild.WebUrl}");
        sb.AppendLine();

        sb.AppendLine("## Parsed failure points");
        if (failurePoints.Count == 0)
        {
            sb.AppendLine("(none — descended into artifact logs; check the artifact index below)");
        }
        else
        {
            var i = 1;
            foreach (var f in failurePoints)
            {
                var loc = f.FilePath is null
                    ? string.Empty
                    : $" — `{f.FilePath}`" + (f.Line.HasValue ? $":{f.Line.Value}" : string.Empty);
                sb.AppendLine($"{i}. **[{f.Kind}]**{loc}");
                if (!string.IsNullOrWhiteSpace(f.Message))
                    sb.AppendLine($"   > {f.Message.Trim()}");
                if (!string.IsNullOrEmpty(f.PayloadJson))
                    sb.AppendLine($"   (in artifact: {f.PayloadJson})");
                i++;
            }
        }
        sb.AppendLine();

        // Truncated log tail of the failed stages.
        sb.AppendLine("## Failed stage log tails (truncated to ≤ 256 KB total)");
        const int promptLogBudget = 32 * 1024; // 32 KB chunk per stage in the prompt
        foreach (var stage in stages)
        {
            var log = logRows.FirstOrDefault(l => l.StageId == stage.Id);
            sb.AppendLine($"### Stage `{stage.StageName}` ({stage.Result})");
            if (log is null || string.IsNullOrEmpty(log.Content))
            {
                sb.AppendLine("(no log content captured)");
            }
            else
            {
                var snippet = log.Content.Length > promptLogBudget
                    ? "…" + log.Content[^promptLogBudget..]
                    : log.Content;
                sb.AppendLine("```");
                sb.AppendLine(snippet);
                sb.AppendLine("```");
                if (log.TruncatedFromBytes > 0)
                    sb.AppendLine($"_(log was truncated from {log.TruncatedFromBytes:N0} bytes)_");
            }
            sb.AppendLine();
        }

        // Artifact index (names + sizes only).
        if (artifacts.Count > 0)
        {
            sb.AppendLine("## Artifacts (use `read_file` to inspect contents)");
            foreach (var a in artifacts)
            {
                sb.AppendLine($"### {a.Name}");
                if (!string.IsNullOrEmpty(a.ExtractedPath))
                    sb.AppendLine($"Root: `{a.ExtractedPath}`");
                var files = artifactFiles.Where(f => f.ArtifactId == a.Id).Take(50).ToList();
                foreach (var f in files)
                {
                    sb.AppendLine($"- {(f.IsLog ? "[log] " : string.Empty)}`{f.RelPath}` ({f.SizeBytes:N0} bytes)");
                }
                if (artifactFiles.Count(f => f.ArtifactId == a.Id) > 50)
                {
                    sb.AppendLine($"…and {artifactFiles.Count(f => f.ArtifactId == a.Id) - 50} more files");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine("For each parsed failure point, choose ONE of:");
        sb.AppendLine("- **Commit a fix** that plausibly addresses the failure point. Push to the existing branch.");
        sb.AppendLine("- **Post a PR reply** explaining why the failure is non-code (flaky test, infra, env).");
        sb.AppendLine("  Use the `post_pr_comment_reply` tool with `Build #{n} failure analysis: …` as the body.");
        sb.AppendLine();
        sb.AppendLine("Do NOT open a new PR — the existing PR will receive the new commit and the build re-runs on push.");
        sb.AppendLine("Do NOT manually trigger a new build; the push trigger handles that.");

        return sb.ToString();
    }

    /// <summary>
    /// Auto-link a work item to an active workstream that shares the same repo,
    /// falling back to any active workstream in the same product. The fallback
    /// MUST stay inside the work item's product — without that fence, picked
    /// items from product A leak into a workstream owned by product B and
    /// vanish from A's Workstreams view (observed: asz-updateservice items
    /// dumped into atlas-platform's ws-retry-jitter).
    /// If no in-product target exists we mint a new active workstream for the
    /// work item's repo so the live pick is immediately visible in the
    /// Workstreams view of its own product.
    /// If the work item has no RepoId (or its repo is unknown) we can't
    /// determine the product and skip — auto-creating a "homeless" workstream
    /// would be worse than the empty view.
    /// </summary>
    /// <summary>
    /// Phase 47 — pure wrapper around <see cref="PairDiversityResolver.PickGenerator"/>
    /// that supplies the production round-robin seed (TickCount). Tests
    /// call <c>PairDiversityResolver.PickGenerator</c> directly with a
    /// deterministic seed.
    /// </summary>
    private static string? ResolveDiverseGenerator(
        IReadOnlyList<string> onShiftMembers,
        HashSet<string> busyAgents,
        string? lead,
        IReadOnlyDictionary<string, string>? lastActive)
    {
        var (agentId, _) = PairDiversityResolver.PickGenerator(
            onShiftMembers, busyAgents, lead, Environment.TickCount, lastActive);
        return agentId;
    }

    /// <summary>
    /// Phase 47 — resolve the evaluator agent for a row's claim. Pulls the
    /// paired crew's members (or the same crew's members minus the picked
    /// generator for a standalone crew) as the candidate roster and runs
    /// <see cref="PairDiversityResolver.PickEvaluator"/>. Best-effort:
    /// when the resolver returns null (every candidate busy), falls
    /// through to the legacy <c>DefaultEvaluatorAgentId</c> /
    /// <c>evaluatorCrew.Lead</c> chain so the row still claims (the
    /// evaluator can wait its turn rather than blocking the row).
    /// </summary>
    private static async Task<string?> ResolveDiverseEvaluatorAsync(
        ControlDbContext control,
        ProductDbContext db,
        CrewRow generatorCrew,
        CrewRow? evaluatorCrew,
        string generatorAgentId,
        HashSet<string> busyAgents,
        CancellationToken ct)
    {
        // Build the evaluator candidate roster.
        var rosterCrewId = evaluatorCrew?.Id ?? generatorCrew.Id;
        var roster = await control.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == rosterCrewId)
            .Select(m => m.AgentId)
            .ToListAsync(ct);

        // The evaluator-side roster must NOT include the just-picked
        // generator (Phase 39 gen != eval). PickEvaluator filters on this
        // too, but pre-filtering keeps the candidate list tidy in the
        // standalone-crew case where roster == generator-crew members.
        //
        // 2026-05-12 bug fix — only the operator-configured preferred
        // evaluator gets first-dibs in PickEvaluator. The previous code
        // ALSO promoted `evaluatorCrew?.Lead` into this slot, giving the
        // paired-crew lead unconditional first-dibs and pinning every
        // claim to the same evaluator. Lead is now reserved as a
        // last-resort fallback ONLY when PickEvaluator returns null.
        var preferredEvalAgentId = generatorCrew.DefaultEvaluatorAgentId
            ?? evaluatorCrew?.DefaultEvaluatorAgentId;
        var leadFallback = evaluatorCrew?.Lead;

        // LRU — recency for the eval roster (plus the preferred eval, which
        // may not be a roster member but is inserted at the head by
        // PickEvaluator) so the evaluator rotates oldest-first rather than
        // pinning to the configured default whenever it's free.
        var evalRecencyIds = string.IsNullOrEmpty(preferredEvalAgentId)
            ? (IReadOnlyCollection<string>)roster
            : roster.Append(preferredEvalAgentId).ToList();
        var evalLastActive = await BusyAgentsHelper.LoadLastActiveAsync(control, evalRecencyIds, ct);

        // Phase 50 fix (2026-05-11) — pass a real per-claim seed so the
        // tie-break shuffle inside PickEvaluator actually runs. The previous
        // call defaulted seed=0, which skips the shuffle, so concurrent
        // claims all picked the same fallback evaluator in roster order.
        // Mirrors the StorySpawnOrchestrator engineer-pair claim site.
        var (picked, _) = PairDiversityResolver.PickEvaluator(
            roster, busyAgents, preferredEvalAgentId, generatorAgentId,
            seed: Random.Shared.Next(), lastActiveByAgent: evalLastActive);

        // Q47.3 implication — evaluator diversity is best-effort. If
        // every roster member is busy, fall back to the legacy chain so
        // the row still claims and the iteration can still run; the
        // ledger contention will sort itself out as runs finish.
        if (picked is null)
        {
            picked = preferredEvalAgentId ?? leadFallback;
        }
        return picked;
    }

    private async Task AutoLinkToWorkstreamAsync(ControlDbContext control, ProductDbContext db, WorkItemRow wi, CancellationToken ct)
    {
        try
        {
            // Check if any workstream already lists this work item.
            var allWs = await db.Workstreams.ToListAsync(ct);
            foreach (var ws in allWs)
            {
                var cards = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ws.CardsJson) ?? new();
                if (cards.Contains(wi.Id)) return; // already linked
            }

            if (string.IsNullOrEmpty(wi.RepoId)) return;

            var wiRepo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            if (wiRepo is null || string.IsNullOrEmpty(wiRepo.ProductId)) return;

            var sameProductRepoIds = await db.Repos.AsNoTracking()
                .Where(r => r.ProductId == wiRepo.ProductId)
                .Select(r => r.Id)
                .ToListAsync(ct);
            var productRepoSet = new HashSet<string>(sameProductRepoIds, StringComparer.Ordinal);

            // Prefer an active workstream on the same repo, then any active
            // workstream in the same product. Cross-product targets are off limits.
            var target = allWs
                .Where(ws => ws.RepoId == wi.RepoId && ws.Status == "active")
                .OrderByDescending(ws => ws.LastActive)
                .FirstOrDefault();
            target ??= allWs
                .Where(ws => ws.Status == "active" && ws.RepoId != null && productRepoSet.Contains(ws.RepoId))
                .OrderByDescending(ws => ws.LastActive)
                .FirstOrDefault();

            if (target is null)
            {
                target = await CreateAutoWorkstreamAsync(control, db, wi, wiRepo, allWs, ct);
                if (target is null) return;
                allWs.Add(target);
            }

            // `target` came either from db.Workstreams.ToListAsync (already
            // tracked) or from CreateAutoWorkstreamAsync via db.Add (also
            // tracked). Mutate it directly — re-querying via FirstOrDefault
            // would miss the newly-added, not-yet-saved row.
            var existingCards = System.Text.Json.JsonSerializer.Deserialize<List<string>>(target.CardsJson) ?? new();
            if (!existingCards.Contains(wi.Id))
            {
                existingCards.Add(wi.Id);
                target.CardsJson = System.Text.Json.JsonSerializer.Serialize(existingCards);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Auto-link to workstream failed for {Wi}", wi.Id);
        }
    }

    /// <summary>
    /// Mint a new active workstream for the work item's repo when no in-product
    /// target exists. Emits <c>workstream.changed</c> so the UI picks it up
    /// without waiting for the 15s poll. Returns the persisted row, or null on
    /// failure (id collision after retry, save error, etc.).
    /// </summary>
    private async Task<WorkstreamRow?> CreateAutoWorkstreamAsync(
        ControlDbContext control,
        ProductDbContext db,
        WorkItemRow wi,
        RepoRow repo,
        List<WorkstreamRow> allWs,
        CancellationToken ct)
    {
        var existingIds = new HashSet<string>(allWs.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var baseId = $"ws-{repo.Id}";
        var id = baseId;
        if (existingIds.Contains(id))
        {
            id = $"{baseId}-{Guid.NewGuid().ToString("N")[..6]}";
            if (existingIds.Contains(id)) return null;
        }

        var maxSort = allWs.Count == 0 ? -1 : allWs.Max(w => w.Sort);
        var defaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
        var displayName = string.IsNullOrWhiteSpace(repo.DisplayName) ? repo.Id : repo.DisplayName;
        var row = new WorkstreamRow
        {
            Id = id,
            Sort = maxSort + 1,
            RepoId = repo.Id,
            Name = displayName,
            Branch = defaultBranch,
            Base = defaultBranch,
            Epic = string.Empty,
            Status = "active",
            WorktreeJson = "[]",
            Created = 0,
            LastActive = 0,
            AgentsJson = "[]",
            PrsJson = "[]",
            CardsJson = "[]",
            Commits = 0,
            Ahead = 0,
            Behind = 0,
            Conflicts = 0,
            Summary = $"Auto-created on first pick of {wi.Id}.",
            BarStart = 0,
            BarEnd = 5,
            Planned = false,
        };
        db.Workstreams.Add(row);

        try
        {
            await this.hub.Clients.All.SendAsync(
                "workstream.changed",
                new { kind = "auto-created", workstream = new { id = row.Id, name = row.Name, repoId = row.RepoId, status = row.Status } },
                ct);
        }
        catch
        {
            // Best-effort fan-out; the 15s UI poll will pick it up on the next refresh.
        }
        return row;
    }

    // ── Sweep 3 — strain detection ────────────────────────────────────────
    private async Task<int> SweepStrainAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var crewsWithQueue = await db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == "crew" && r.State == TaskQueueStates.Queued)
            .Select(r => r.TargetId)
            .Distinct()
            .ToListAsync(ct);

        var fired = 0;
        var observed = new HashSet<string>(crewsWithQueue.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var crewId in crewsWithQueue)
        {
            observed.Add(crewId);
            var stats = await queue.GetCrewStatsAsync(crewId, ct);
            if (!stats.Strained) { this.strainArmed.TryRemove(crewId, out _); continue; }

            // Debounce — only fire when transitioning from non-strained.
            if (this.strainArmed.ContainsKey(crewId)) continue;
            this.strainArmed[crewId] = true;
            fired++;

            StrainFiredCounter.Add(1, new KeyValuePair<string, object?>("crewId", crewId));

            // Resolve the lead's user channel; in single-user dev mode the
            // lead is an agent id, not a user id — broadcast on both groups
            // so whichever the UI subscribes to receives the signal.
            var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
            var leadId = crew?.Lead;
            var groupName = leadId is null ? $"crew.{crewId}" : $"user.{leadId}";
            await this.hub.Clients.Group(groupName).SendAsync("queue.strained", stats, ct);
            // Also broadcast to all clients viewing the head work item.
            if (stats.HeadWorkItemId is { Length: > 0 } head)
            {
                await this.taskChannel.EmitAsync(head, "queue.strained", stats, crewId: crewId, ct: ct);
            }
        }
        // Reset the debounce flag for crews that no longer have queues.
        foreach (var key in this.strainArmed.Keys.ToList())
        {
            if (!observed.Contains(key)) this.strainArmed.TryRemove(key, out _);
        }
        return fired;
    }

    // ── Sweep 4 — abandoned recovery ──────────────────────────────────────
    private async Task<int> SweepAbandonedAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var cutoff = DateTimeOffset.UtcNow - TaskQueueDefaults.AbandonedAfter;
        var stale = await db.TaskQueue
            .Where(r => r.State == TaskQueueStates.PickedUp)
            .ToListAsync(ct);
        stale = stale.Where(r => r.LastUpdatedAt < cutoff).ToList();

        var recovered = 0;
        foreach (var row in stale)
        {
            row.State = TaskQueueStates.Queued;
            row.PickedUpAt = null;
            row.PickedUpByAgent = null;
            row.LastUpdatedAt = DateTimeOffset.UtcNow;
            db.TaskQueueHistory.Add(new TaskQueueHistoryRow
            {
                QueueId = row.Id,
                Kind = "abandoned-recovered",
                FromValue = TaskQueueStates.PickedUp,
                ToValue = TaskQueueStates.Queued,
                At = DateTimeOffset.UtcNow,
            });

            // Phase 19 — re-queued items are back to "queued" status.
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
            if (wi is not null)
            {
                wi.ExecutionStatus = ExecutionStatuses.Queued;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            recovered++;
        }
        if (recovered > 0)
        {
            await db.SaveChangesAsync(ct);
            // Fan out so the UI un-pulses the "live" pill on stuck rows.
            foreach (var row in stale)
            {
                var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
                if (wi is not null) await this.BroadcastWorkItemChangedAsync(wi, "exec-requeued", ct, db);
            }
        }
        return recovered;
    }

    // ── Sweep 5 — live-badge reconciliation ───────────────────────────────
    /// <summary>
    /// Heals work items whose <c>ExecutionStatus = "live"</c> badge is stuck
    /// because their queue row terminated without the corresponding WI write
    /// (race during restart, crashed agent process, etc.). Without this sweep
    /// the UI badge pulses forever and operators have to re-queue manually.
    /// <para>
    /// Decision rule per stuck WI:
    /// <list type="bullet">
    ///   <item>Latest deliverable verdict is <c>iterate</c>/<c>stalled</c>/
    ///         <c>stalled-budget</c> AND WI state is <c>active</c>/<c>proposed</c>:
    ///         auto-enqueue back to the original crew so SweepClaims can
    ///         pick it up. Combined with the agent-pin in SweepClaims and
    ///         the iter-seed in DeliverableLoop, this is true resume-from-
    ///         disk with no operator action.</item>
    ///   <item>Otherwise (terminal verdict, missing deliverables, in-review/
    ///         resolved/closed): just flip the badge to <c>failed</c> so the
    ///         UI stops claiming it's running.</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<int> SweepLiveBadgeReconciliationAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        var liveWis = await db.WorkItems
            .Where(w => w.ExecutionStatus == ExecutionStatuses.Live)
            .ToListAsync(ct);
        if (liveWis.Count == 0) return 0;

        // OpenStates is a static array; materialize for the EF Contains() translation.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var reconciled = 0;

        foreach (var wi in liveWis)
        {
            var hasOpenRow = await db.TaskQueue.AsNoTracking()
                .AnyAsync(r => r.WorkItemId == wi.Id && openStates.Contains(r.State), ct);
            if (hasOpenRow) continue;

            // No open queue row + executionStatus=live → stuck badge.
            var delivCandidates = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == wi.Id)
                .ToListAsync(ct);
            var lastDeliv = delivCandidates
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
                .FirstOrDefault();

            var canResume = lastDeliv is not null
                && lastDeliv.Verdict is "iterate" or "stalled" or "stalled-budget"
                && wi.State is "active" or "proposed";

            if (canResume)
            {
                try
                {
                    var newRow = await queue.EnqueueAsync(wi.Id, "crew", lastDeliv!.CrewId, ct);
                    this.logger.LogInformation(
                        "Live-badge reconcile: auto-resumed wi={Wi} into crew={Crew} (lastIter={Iter} verdict={Verdict})",
                        wi.Id, lastDeliv.CrewId, lastDeliv.Iter, lastDeliv.Verdict);
                    await this.taskChannel.EmitAsync(wi.Id, "task.auto-resumed", new
                    {
                        queueId = newRow.Id,
                        crewId = lastDeliv.CrewId,
                        resumeFromIter = lastDeliv.Iter,
                        priorVerdict = lastDeliv.Verdict,
                        priorScore = lastDeliv.Score,
                    }, crewId: lastDeliv.CrewId, ct: ct);
                    // EnqueueAsync sets ExecutionStatus=Queued; broadcast it.
                    var refreshed = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wi.Id, ct);
                    if (refreshed is not null) await this.BroadcastWorkItemChangedAsync(refreshed, "exec-auto-resumed", ct, db);
                    reconciled++;
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Live-badge reconcile: auto-resume EnqueueAsync failed for wi={Wi}, falling back to failed badge", wi.Id);
                    try { await this.HealStuckBadgeAsync(control, db, wi, "exec-reconciled-failed", ct); }
                    catch (Exception healEx) { this.logger.LogWarning(healEx, "Live-badge reconcile: heal-fallback also failed for wi={Wi}", wi.Id); }
                    reconciled++;
                }
            }
            else
            {
                await this.HealStuckBadgeAsync(control, db, wi, "exec-reconciled-failed", ct);
                reconciled++;
            }
        }
        return reconciled;
    }

    private async Task HealStuckBadgeAsync(ControlDbContext control, ProductDbContext db, WorkItemRow wi, string broadcastKind, CancellationToken ct)
    {
        wi.ExecutionStatus = ExecutionStatuses.Failed;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await this.BroadcastWorkItemChangedAsync(wi, broadcastKind, ct, db);
        this.logger.LogInformation(
            "Live-badge reconcile: healed stuck badge wi={Wi} state={State} → failed", wi.Id, wi.State);
    }

    private async Task BroadcastQueueChangedAsync(TaskQueueRow row, string kind, CancellationToken ct)
    {
        await this.hub.Clients.All.SendAsync("queue.changed", new
        {
            kind,
            row,
        }, ct);
        // Also fan into the per-work-item live channel so the transcript shows queue moves inline.
        await this.taskChannel.EmitAsync(row.WorkItemId, $"queue.{kind}", new
        {
            queueId = row.Id,
            state = row.State,
            crewId = row.RoutedToCrewId,
        }, crewId: row.RoutedToCrewId, ct: ct);
    }

    /// <summary>
    /// Phase 19 — fan out a <c>workitem.changed</c> SignalR event whenever the
    /// background worker mutates <see cref="WorkItemRow.State"/> or
    /// <see cref="WorkItemRow.ExecutionStatus"/>. Without this the UI's
    /// useWorkItemsStore is never told the row moved out of "live", so the
    /// ExecBadge stays green-pulsing long after the PR is up.
    /// </summary>
    /// <summary>
    /// Phase 22 — resolve team name from crew name (same logic as
    /// <c>CrewsEndpoints.TeamFromCrewName</c> duplicated here to
    /// avoid coupling endpoint and worker packages).
    /// </summary>
    private static string? TeamFromCrewName(string name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        if (n.StartsWith("arch")) return "Architecture";
        if (n.StartsWith("eng"))  return "Engineering";
        if (n.StartsWith("rev"))  return "Review";
        if (n.StartsWith("qa"))   return "QA";
        return null;
    }

    private async Task BroadcastWorkItemChangedAsync(WorkItemRow row, string kind, CancellationToken ct, ProductDbContext? db = null)
    {
        try
        {
            string? prId = null;
            string? prCol = null;
            if (db is not null)
            {
                (prId, prCol) = await Forge.Api.Endpoints.WorkItemsEndpoints.LookupPrimaryPrAsync(db, row.Id, ct);
            }
            await this.hub.Clients.All.SendAsync(
                "workitem.changed",
                new { kind, workItem = Forge.Api.Endpoints.WorkItemsEndpoints.MapWorkItemRow(row, prId, prCol) },
                ct);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to broadcast workitem.changed for {Wi}", row.Id);
        }
    }

    /// <summary>Counts emitted by one tick.</summary>
    public sealed record WorkerTickResult(
        int AckTimeouts,
        int Claims,
        int StrainSignals,
        int Recovered,
        IReadOnlyList<ClaimSkip>? ClaimSkips = null,
        int Reconciled = 0);

    /// <summary>Why a crew's head row wasn't claimed on a given tick.</summary>
    public sealed record ClaimSkip(string CrewId, string Reason, string? Detail);

    // ── Git helpers for worktree management ──────────────────────────────

    /// <summary>
    /// Removes build-output directories (obj/, bin/Debug, node_modules/,
    /// target/, __pycache__/) from the staged index before a delivery commit
    /// so a blanket <c>git add -A</c> never lands generated binaries in the
    /// PR. The impl-path twin of
    /// <c>RealStoryGitOps.ScrubBuildArtifactsFromIndexAsync</c>; shares the
    /// pathspec set in <see cref="Stories.BuildArtifactPaths.IndexScrubPathspecs"/>.
    /// Best-effort — failures are swallowed.
    /// </summary>
    private static async Task ScrubBuildArtifactsFromIndexAsync(string cwd)
    {
        try
        {
            var specs = Stories.BuildArtifactPaths.IndexScrubPathspecs();
            var args = new string[5 + specs.Count];
            args[0] = "rm";
            args[1] = "-r";
            args[2] = "--cached";
            args[3] = "--ignore-unmatch";
            args[4] = "--";
            for (var i = 0; i < specs.Count; i++)
            {
                args[5 + i] = specs[i];
            }
            await RunGitAsync(cwd, args);
        }
        catch { /* best-effort */ }
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitAsync(
        string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    // ── Phase 26 — general-purpose command runner for build/test gate ────

    private const int DefaultCommandTimeoutSec = 600;
    private const int MaxOutputChars = 16_384;

    /// <summary>
    /// Runs an arbitrary command in the given working directory with a
    /// configurable timeout. On timeout the entire process tree is killed.
    /// Returns exit code and truncated stdout + stderr.
    /// </summary>
    /// <param name="cwd">Working directory.</param>
    /// <param name="command">
    ///   Full command string (e.g., <c>dotnet build Workflow.sln</c>). The
    ///   first token is the executable; remaining tokens are arguments,
    ///   parsed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </param>
    /// <param name="timeoutSeconds">
    ///   Kill the process tree after this many seconds. Null → <see cref="DefaultCommandTimeoutSec"/>.
    /// </param>
    internal static async Task<(int code, string stdout, string stderr, bool timedOut)> RunCommandAsync(
        string cwd, string command, int? timeoutSeconds = null)
    {
        var timeout = timeoutSeconds ?? DefaultCommandTimeoutSec;
        var parts = ParseCommandLine(command);
        if (parts.Length == 0) return (-1, "", "Empty command", false);

        var exe = parts[0];
        var argStart = 1;

        // .cmd/.bat files require cmd.exe /c on Windows with UseShellExecute=false.
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            exe = "cmd.exe";
            argStart = 0; // all original parts become args to cmd /c
        }

        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (exe == "cmd.exe")
        {
            psi.ArgumentList.Add("/c");
        }
        for (var i = argStart; i < parts.Length; i++) psi.ArgumentList.Add(parts[i]);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var timedOut = false;
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (proc.ExitCode, TruncateOutput(stdout), TruncateOutput(stderr), false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }

            // Collect whatever output is available.
            string partialOut = "", partialErr = "";
            try
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                partialOut = await proc.StandardOutput.ReadToEndAsync(readCts.Token);
                partialErr = await proc.StandardError.ReadToEndAsync(readCts.Token);
            }
            catch { /* best effort */ }

            return (-1, TruncateOutput(partialOut), TruncateOutput(partialErr + "\n[TIMED OUT]"), timedOut);
        }
    }

    /// <summary>Truncate command output to <see cref="MaxOutputChars"/>, keeping the tail.</summary>
    private static string TruncateOutput(string output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaxOutputChars) return output;
        return "…[truncated]\n" + output[^MaxOutputChars..];
    }

    /// <summary>
    /// Splits a command string into executable + arguments, respecting double-quoted tokens.
    /// E.g., <c>dotnet test --filter "DisplayName~Foo bar"</c> →
    /// <c>["dotnet", "test", "--filter", "DisplayName~Foo bar"]</c>.
    /// </summary>
    internal static string[] ParseCommandLine(string command)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c> output and returns the
    /// filesystem path of the worktree whose branch ends with <paramref name="branchSuffix"/>.
    /// Returns null when no match is found.
    /// </summary>
    private static string? ParseWorktreePath(string porcelainOutput, string branchSuffix)
    {
        string? currentPath = null;
        foreach (var line in porcelainOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("worktree "))
            {
                currentPath = trimmed["worktree ".Length..];
            }
            else if (trimmed.StartsWith("branch ") && currentPath is not null)
            {
                var branch = trimmed["branch ".Length..];
                if (branch.EndsWith(branchSuffix))
                    return currentPath;
            }
        }
        return null;
    }

}
