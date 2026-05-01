// -----------------------------------------------------------------------
// <copyright file="CrewQueueWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

    private readonly IServiceScopeFactory scopeFactory;
    private readonly TaskChannel taskChannel;
    private readonly IHubContext<ForgeHub> hub;
    private readonly ILogger<CrewQueueWorker> logger;

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

    private readonly WorktreeChangeFeed worktreeFeed;

    public CrewQueueWorker(
        IServiceScopeFactory scopeFactory,
        TaskChannel taskChannel,
        IHubContext<ForgeHub> hub,
        WorktreeChangeFeed worktreeFeed,
        ILogger<CrewQueueWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.taskChannel = taskChannel;
        this.hub = hub;
        this.worktreeFeed = worktreeFeed;
        this.logger = logger;
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
            // Boot scan once before the timer.
            try { await this.RunOnceAsync(this.cts.Token); }
            catch (Exception ex) { this.logger.LogDebug(ex, "CrewQueueWorker boot scan errored"); }
            await this.LoopAsync(this.cts.Token);
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.cts?.Cancel();
        if (this.loop is not null)
        {
            try { await this.loop; } catch { /* shutdown */ }
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
        var ackTimeouts = await this.SafeSweepAsync("SweepAckTimeoutsAsync", () => this.SweepAckTimeoutsAsync(ct), ct);
        var claimsResult = await this.SafeSweepAsync(
            "SweepClaimsAsync",
            async () => { var r = await this.SweepClaimsAsync(ct); return (object?)r; },
            ct,
            fallback: (object?)(0, (IReadOnlyList<ClaimSkip>)Array.Empty<ClaimSkip>()));
        var (claims, claimSkips) = ((int, IReadOnlyList<ClaimSkip>))claimsResult!;
        var strainSignals = await this.SafeSweepAsync("SweepStrainAsync", () => this.SweepStrainAsync(ct), ct);
        var recovered = await this.SafeSweepAsync("SweepAbandonedAsync", () => this.SweepAbandonedAsync(ct), ct);
        var reconciled = await this.SafeSweepAsync("SweepLiveBadgeReconciliationAsync", () => this.SweepLiveBadgeReconciliationAsync(ct), ct);
        return new WorkerTickResult(ackTimeouts, claims, strainSignals, recovered, claimSkips, reconciled);
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
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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

    // ── Sweep 1 — auto-ack timeouts ───────────────────────────────────────
    private async Task<int> SweepAckTimeoutsAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crewsWithQueue = await db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == "crew" && r.State == TaskQueueStates.Queued)
            .Select(r => r.TargetId)
            .Distinct()
            .ToListAsync(ct);

        var claimed = 0;
        var skips = new List<ClaimSkip>();
        foreach (var crewId in crewsWithQueue)
        {
            var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
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
            var allMembers = await db.CrewMembers.AsNoTracking()
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
                var preferredAgent = await db.Agents.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == preferredId, ct);
                var inCrew = await db.CrewMembers.AsNoTracking()
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
                    var lead = await db.Agents.AsNoTracking()
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

            // Resume support — when a work item already has deliverables (boot-
            // recovery just put it back on the queue), prefer the same agent
            // that was driving it before. Without pinning, every restart spins
            // up a fresh round-robin pick and the live transcript looks like a
            // brand-new task instead of a continuation.
            if (string.IsNullOrEmpty(picked) && headRow is not null)
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
            picked ??= onShiftMembers.Count == 0
                ? crew.Lead
                : onShiftMembers[Math.Abs(Environment.TickCount) % onShiftMembers.Count];

            // Phase 18.6 (Q18.3) — when zero members are on-shift AND there's
            // no lead to fall back on, picked is null. Skip cleanly rather
            // than calling ClaimNextAsync(crewId, null), which would CAS the
            // row to picked-up with PickedUpByAgent=null and then run the
            // agent runner against a null agent id.
            if (string.IsNullOrEmpty(picked))
            {
                skips.Add(new ClaimSkip(crewId, "no-agent-available", $"members={allMembers.Count} onShift={onShiftMembers.Count} lead={crew.Lead ?? "(null)"}"));
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

            // Resolve the paired evaluator crew (if any).
            CrewRow? evaluatorCrew = null;
            if (!string.IsNullOrEmpty(crew.PairedWithCrewId))
            {
                evaluatorCrew = await db.Crews.AsNoTracking()
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
                var evalAgentId = crew.DefaultEvaluatorAgentId ?? evaluatorCrew?.Lead;
                var agents = new List<string> { picked! };
                if (evalAgentId is not null) agents.Add(evalAgentId);
                var now = DateTimeOffset.UtcNow;
                var crewTeam = TeamFromCrewName(crew.Name);
                var assignedAgent = new
                {
                    agents,
                    team = crewTeam,
                    crewId,
                    role = "generator",
                    assignedAt = now.ToString("o"),
                    assignedBy = "queue-worker",
                };
                wi.AssignedAgentJson = System.Text.Json.JsonSerializer.Serialize(assignedAgent);

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
                await this.AutoLinkToWorkstreamAsync(db, wi, ct);

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
            _ = Task.Run(() => RunAgentAsync(row.WorkItemId, crewId, picked!, crew, wi, row.SkipSoulPrompt, row.Kind, CancellationToken.None));
        }
        return (claimed, skips);
    }

    // ── Agent runner kickoff ───────────────────────────────────────────────
    private async Task RunAgentAsync(
        string workItemId, string crewId, string agentId,
        CrewRow crew, WorkItemRow? wi, bool skipSoulPrompt, string kind, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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
                return;
            }

            // Resolve the agent row for model/soul/backend overrides.
            var agent = await db.Agents.AsNoTracking()
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

            // Soul.md — skip when the queue row has SkipSoulPrompt set (force-restart).
            var soulMd = "";
            if (!skipSoulPrompt && !string.IsNullOrEmpty(agent?.SoulPath))
            {
                try { soulMd = await File.ReadAllTextAsync(agent.SoulPath, ct); }
                catch { /* best effort */ }
            }

            // Phase 24 Q24.13 — comment-response runs prepend the per-task
            // system template ahead of soul.md so the must-reply rubric is
            // surfaced regardless of the agent's persona. Missing template
            // is a soft fallback (empty string) so the rest of the loop
            // still runs; tests cover the loaded path.
            if (string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase) && !skipSoulPrompt)
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
            if (string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase) && !skipSoulPrompt)
            {
                var template = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.BuildFixFile, ct);
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

            // If the resolved path doesn't exist on disk, fall back to a temp
            // directory so the agent can at least start (e.g. repo not cloned).
            if (!string.IsNullOrEmpty(worktreePath) && !Directory.Exists(worktreePath))
            {
                this.logger.LogWarning(
                    "Worktree path {Path} does not exist for wi={Wi}; falling back to temp",
                    worktreePath, workItemId);
                worktreePath = Path.GetTempPath();
            }
            if (string.IsNullOrEmpty(worktreePath))
            {
                worktreePath = Path.GetTempPath();
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
            var branchName = $"ws/{workItemId}";
            var baseBranch = "main";
            string? repoCloneRoot = null;
            Persistence.RepoRow? wiRepo = null;
            if (!string.IsNullOrEmpty(wi?.RepoId))
            {
                wiRepo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                if (!string.IsNullOrEmpty(wiRepo?.DefaultBranch))
                    baseBranch = wiRepo.DefaultBranch;
                if (!string.IsNullOrEmpty(wiRepo?.CloneRoot) && Directory.Exists(wiRepo.CloneRoot))
                    repoCloneRoot = wiRepo.CloneRoot;
            }

            if (!string.IsNullOrEmpty(repoCloneRoot))
            {
                // Compute worktree directory: {cloneRoot}/.worktree/{sanitizedId}
                var wtRoot = Path.Combine(repoCloneRoot, ".worktree");
                var sanitizedId = workItemId.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
                var worktreeDir = Path.Combine(wtRoot, $"ws-{sanitizedId}");

                // Ensure .worktree/ is in .gitignore so it's never committed.
                try
                {
                    var gitignorePath = Path.Combine(repoCloneRoot, ".gitignore");
                    var needsEntry = true;
                    if (File.Exists(gitignorePath))
                    {
                        var lines = await File.ReadAllLinesAsync(gitignorePath, ct);
                        needsEntry = !lines.Any(l => l.Trim() is ".worktree/" or ".worktree");
                    }
                    if (needsEntry)
                    {
                        await File.AppendAllTextAsync(gitignorePath, "\n.worktree/\n", ct);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Could not update .gitignore for {Repo}", repoCloneRoot);
                }

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
                this.worktreeFeed.Subscribe(workItemId, workstreamId, worktreePath);
            }

            // Build prompt from the work item.
            var title = wi?.Title ?? workItemId;
            var desc = wi?.DescriptionMd ?? "";

            string prompt;
            if (string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase))
            {
                // Phase 24.9 — comment-response prompt loads the open PR comments
                // needing response and asks the agent to reply or commit a fix.
                prompt = await BuildCommentResponsePromptAsync(db, workItemId, title, ct);
            }
            else if (string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase))
            {
                // Phase 27 Q27.10 — build-fix prompt carries the failed
                // build's pipeline header, parsed FailurePoint rows, the
                // 256 KB log tail of failed stages, the artifact index, the
                // work-item brief, and recent commit messages. Logs and
                // artifacts are NOT inlined verbatim — the agent uses
                // `read_file` to descend into specific artifact files.
                prompt = await BuildBuildFixPromptAsync(db, workItemId, title, desc, ct);
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
            CrewRow? evalCrew = null;
            string? evaluatorAgentId = null;
            string? evaluatorCrewId = null;
            if (!string.IsNullOrEmpty(crew.PairedWithCrewId))
            {
                evalCrew = await db.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct);
                evaluatorCrewId = evalCrew?.Id;
                evaluatorAgentId = crew.DefaultEvaluatorAgentId ?? evalCrew?.Lead;
            }

            // (3) Run the deliverable loop (generator ↔ evaluator iterations).
            var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();

            // Generator delegate: invokes the agent runner and collects output.
            async Task<GeneratorRunOutput> GeneratorDelegate(int iter, string evalNotes, CancellationToken iterCt)
            {
                var iterPrompt = iter == 1
                    ? prompt
                    : $"{prompt}\n\n---\n\nEvaluator feedback from iteration {iter - 1}:\n{evalNotes}";

                var invocation = await runner.StartAsync(workItemId, agentId, iterPrompt, context, iterCt);
                var output = new System.Text.StringBuilder();
                decimal cost = 0m;
                long inTok = 0, outTok = 0;

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
                    }, crewId: crewId, ct: iterCt);

                    if (!string.IsNullOrEmpty(evt.Text) && evt.Kind == AgentEventKind.Token) output.Append(evt.Text);
                    if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                    if (evt.InputTokens is > 0) inTok += evt.InputTokens.Value;
                    if (evt.OutputTokens is > 0) outTok += evt.OutputTokens.Value;
                }

                return new GeneratorRunOutput(output.ToString(), cost, inTok, outTok);
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

                var evalAgent = await db.Agents.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == evaluatorAgentId, iterCt);
                var evalModel = !string.IsNullOrEmpty(evalAgent?.EvaluatorModel)
                    ? evalAgent!.EvaluatorModel
                    : evalCrew.Model;
                var evalSoul = "";
                if (!skipSoulPrompt && !string.IsNullOrEmpty(evalAgent?.SoulPath))
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
                var evalSoulForScoring = (string.IsNullOrEmpty(evalSoul) ? "" : evalSoul + "\n\n---\n\n")
                    + "CRITICAL RULES FOR THIS SESSION:\n"
                    + "1. Use tools (view, grep, powershell) to inspect the worktree and understand what changed.\n"
                    + "   Run `git diff HEAD~1 --stat -p` to see the actual code changes.\n"
                    + "   Read modified files to verify correctness.\n"
                    + "2. Do NOT modify any files. Do NOT commit, push, or run builds. Read-only inspection only.\n"
                    + "3. End your response with a fenced JSON block:\n"
                    + "```json\n{ \"score\": <0-100>, \"notes\": \"<feedback>\" }\n```\n"
                    + "4. Score >= 90 = pass, 70-89 = iterate, < 70 = reject.\n"
                    + "5. Your 'notes' MUST describe WHAT was changed, WHY it fixes the problem, and HOW the fix works.\n"
                    + "   Do NOT just say 'build clean, tests pass' — that is self-evident.\n"
                    + "   Focus on the technical substance: which code paths were modified, what error handling was added,\n"
                    + "   what edge cases are now covered, and whether the approach is correct.\n";

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
                var evalPrompt = "You are a critical code reviewer scoring agent work.\n\n"
                    + "IMPORTANT: You MUST end your response with a fenced JSON block in exactly this format:\n"
                    + "```json\n"
                    + "{ \"score\": <0-100>, \"notes\": \"<your review comment>\" }\n"
                    + "```\n\n"
                    + "Scoring rubric:\n"
                    + "- score >= 90: pass (code is correct, tested, ready to merge)\n"
                    + "- score 70-89: iterate (has issues that need fixing — describe them in notes)\n"
                    + "- score < 70: reject (fundamentally wrong approach)\n\n"
                    + "Your 'notes' field will be displayed as the **Evaluator Review** in the PR description.\n"
                    + "Write it as a review comment that covers:\n"
                    + "- What the change does and whether the approach is correct\n"
                    + "- Why this fix addresses the root cause (or doesn't)\n"
                    + "- Any remaining risks, edge cases, or suggestions\n"
                    + "Do NOT mention build/test pass — that is assumed. Focus on technical substance.\n\n"
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

                var evalOutput = new System.Text.StringBuilder();
                decimal evalCost = 0m;
                long evalInTok = 0, evalOutTok = 0;

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
                    }, crewId: evaluatorCrewId!, ct: iterCt);

                    if (!string.IsNullOrEmpty(evt.Text)) evalOutput.Append(evt.Text);
                    if (evt.CostUsd.HasValue) evalCost += evt.CostUsd.Value;
                    if (evt.InputTokens is > 0) evalInTok += evt.InputTokens.Value;
                    if (evt.OutputTokens is > 0) evalOutTok += evt.OutputTokens.Value;
                }

                return new EvaluatorRunOutput(evalOutput.ToString(), evalCost, evalInTok, evalOutTok);
            }

            // Phase 24 Q24.12 — propagate the queue row's kind into the loop so
            // downstream consumers (cost ledger tagging, transcript events,
            // evaluator preamble selection) can branch on it.
            var loopKind = string.Equals(kind, TaskQueueKinds.CommentResponse, StringComparison.OrdinalIgnoreCase)
                ? DeliverableKinds.CommentResponse
                : string.Equals(kind, TaskQueueKinds.BuildFix, StringComparison.OrdinalIgnoreCase)
                    ? DeliverableKinds.BuildFix
                    : DeliverableKinds.Coding;

            var loopRequest = new DeliverableLoopRequest(
                WorkItemId: workItemId,
                CrewId: crewId,
                GeneratorAgentId: agentId,
                EvaluatorAgentId: evaluatorAgentId ?? agentId,
                Generator: GeneratorDelegate,
                Evaluator: EvaluatorDelegate,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                EvaluatorCrewId: evaluatorCrewId,
                Kind: loopKind);

            var result = await loop.RunAsync(loopRequest, ct);

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
                    string? prRepoName = null;
                    string? prProjectName = null;
                    if (wiRepo is not null && repoKind == "ado")
                    {
                        prRepoName = ExtractAdoRepoName(wiRepo.RemoteUrl);
                        if (!string.IsNullOrEmpty(wiRepo.AdoProject))
                            prProjectName = wiRepo.AdoProject;
                    }

                    var provider = providers.FirstOrDefault(p => p.Kind == repoKind && p.IsConfigured)
                        ?? providers.FirstOrDefault(p => p.IsConfigured);
                    if (provider is null)
                        throw new InvalidOperationException($"No configured provider for kind '{repoKind}'");

                    var prTitle = wi?.Title ?? workItemId;

                    // Resolve agent full name + role for the PR header.
                    var agentRow = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
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
                        ? await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evaluatorAgentId, ct)
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
                    try
                    {
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
                                UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
                            });
                        }
                        else
                        {
                            existingPr.WorkItemId ??= workItemId;
                            existingPr.WebUrl ??= pr.WebUrl;
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
                await this.SetExecutionStatusAsync(workItemId, ExecutionStatuses.Failed, ct);
                await this.taskChannel.EmitAsync(workItemId, "task.failed", new
                {
                    agentId,
                    crewId,
                    error = $"Run ended with verdict='{result.FinalVerdict}' (score={result.FinalScore}). No PR was created.",
                }, crewId: crewId, ct: ct);
                return;
            }

            // Transition queue row: picked-up → completed.
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
        }
    }

    private async Task CompleteQueueRowAsync(string workItemId, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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

            // Phase 19 — mark execution as completed (success path;
            // failure path sets "failed" separately in the catch block).
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is not null && wi.ExecutionStatus == ExecutionStatuses.Live)
            {
                wi.ExecutionStatus = ExecutionStatuses.Completed;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await this.BroadcastWorkItemChangedAsync(wi, "exec-completed", ct, db);
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
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
    private static async Task<bool> VerifyPrCreatedAsync(ForgeDbContext db, string workItemId, CancellationToken ct)
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
    private static async Task<string> BuildCommentResponsePromptAsync(ForgeDbContext db, string workItemId, string workItemTitle, CancellationToken ct)
    {
        var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.WorkItemId == workItemId, ct);
        if (pr is null)
        {
            return $"Work item: {workItemTitle}\n\nThis task asked you to address PR comments, but no PR is linked to this work item.";
        }

        var openComments = await db.PrComments.AsNoTracking()
            .Where(c => c.PrId == pr.Id && c.RequiresAttention)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

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
        ForgeDbContext db, string workItemId, string title, string desc, CancellationToken ct)
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
    private async Task AutoLinkToWorkstreamAsync(ForgeDbContext db, WorkItemRow wi, CancellationToken ct)
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
                target = await CreateAutoWorkstreamAsync(db, wi, wiRepo, allWs, ct);
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
        ForgeDbContext db,
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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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
            var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
                    try { await this.HealStuckBadgeAsync(db, wi, "exec-reconciled-failed", ct); }
                    catch (Exception healEx) { this.logger.LogWarning(healEx, "Live-badge reconcile: heal-fallback also failed for wi={Wi}", wi.Id); }
                    reconciled++;
                }
            }
            else
            {
                await this.HealStuckBadgeAsync(db, wi, "exec-reconciled-failed", ct);
                reconciled++;
            }
        }
        return reconciled;
    }

    private async Task HealStuckBadgeAsync(ForgeDbContext db, WorkItemRow wi, string broadcastKind, CancellationToken ct)
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

    private async Task BroadcastWorkItemChangedAsync(WorkItemRow row, string kind, CancellationToken ct, ForgeDbContext? db = null)
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

    /// <summary>
    /// Extracts the ADO repository name from a remote URL.
    /// E.g. <c>https://dev.azure.com/org/project/_git/reponame</c> → <c>reponame</c>.
    /// </summary>
    private static string? ExtractAdoRepoName(string? remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)) return null;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return null;
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gitIdx = Array.IndexOf(segs, "_git");
        if (gitIdx >= 0 && gitIdx + 1 < segs.Length)
        {
            return segs[gitIdx + 1].TrimEnd('/');
        }
        return null;
    }
}
