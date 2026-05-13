// -----------------------------------------------------------------------
// <copyright file="SloppyReassignmentReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 52 — auto-recovery for sloppy-iteration verdicts.
///
/// <para>When <see cref="DeliverableLoop"/> detects sloppy iteration (score
/// plateau, generator token cap, or iter cap) it returns
/// <c>verdict="sloppy"</c>. <see cref="CrewQueueWorker"/> parks the work
/// item at <see cref="ExecutionStatuses.PendingReassign"/> and appends
/// the current pair to <see cref="WorkItemRow.PriorPairsJson"/>.</para>
///
/// <para>This reconciler ticks every
/// <see cref="SloppyReassignmentOptions.TickInterval"/> (default 15 s) and
/// resolves the row under one of two paths:</para>
/// <list type="bullet">
///   <item><b>Reassign</b> — <see cref="WorkItemRow.PairAttemptCount"/>
///   &lt; <see cref="ScoringConstants.MaxPairAttempts"/>: increment the
///   counter, wipe transcript + deliverables + budget window + worktree
///   branch (Q52.4 clean slate), and requeue. The picker reads
///   <see cref="WorkItemRow.PriorPairsJson"/> and excludes those agents
///   so the new claim runs on a fresh pair.</item>
///   <item><b>Terminal failure</b> — <c>PairAttemptCount &gt;= MaxPairAttempts</c>:
///   stamp <c>ExecutionStatus=failed</c>,
///   <see cref="WorkItemRow.FailureReason"/>=<see cref="ScoringConstants.SloppyTerminalFailureReason"/>,
///   close the latest <c>agent_pair_assignment</c> row as
///   <c>terminal-failed</c>. No fourth pair is attempted.</item>
/// </list>
/// </summary>
public sealed class SloppyReassignmentReconciler : BackgroundService
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> ReassignedCounter =
        Meter.CreateCounter<long>("forge.sloppy.reassigned");
    private static readonly Counter<long> TerminalFailedCounter =
        Meter.CreateCounter<long>("forge.sloppy.terminal_failed");
    private static readonly Counter<long> BlockedNoPairCounter =
        Meter.CreateCounter<long>("forge.sloppy.blocked_no_pair");
    private static readonly Counter<long> SkippedCounter =
        Meter.CreateCounter<long>("forge.sloppy.skipped");

    /// <summary>
    /// Tracks per-row consecutive no-pick ticks so we emit the
    /// <c>task.sloppy-blocked</c> event only once after
    /// <see cref="SloppyReassignmentOptions.MaxNoPickTicks"/> consecutive
    /// failures. In-memory, resets on reconciler restart — acceptable per
    /// the spec's "approximate operator signal threshold" note.
    /// </summary>
    private readonly Dictionary<string, int> noPickTicksByWorkItem = new(StringComparer.Ordinal);

    /// <summary>
    /// Work items we've already emitted the blocked event for, so we don't
    /// re-emit every tick once the threshold is crossed.
    /// </summary>
    private readonly HashSet<string> blockedEventEmitted = new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<SloppyReassignmentOptions> options;
    private readonly ILogger<SloppyReassignmentReconciler> logger;
    private readonly TaskChannel taskChannel;
    private readonly IHubContext<ForgeHub> hub;

    public SloppyReassignmentReconciler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SloppyReassignmentOptions> options,
        TaskChannel taskChannel,
        IHubContext<ForgeHub> hub,
        ILogger<SloppyReassignmentReconciler> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.taskChannel = taskChannel;
        this.hub = hub;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait one tick before the first scan so app boot finishes
        // (migrations + seed). Matches the BudgetCooldownReconciler shape.
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
                this.logger.LogWarning(ex, "SloppyReassignmentReconciler tick errored — continuing");
            }

            try { await Task.Delay(this.options.CurrentValue.TickInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick. Public so tests can drive deterministically without waiting
    /// for the interval timer. Returns the count of rows that progressed
    /// (either reassigned or marked terminal-failed).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        var candidates = await db.WorkItems
            .Where(w => w.ExecutionStatus == ExecutionStatuses.PendingReassign)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var progressed = 0;
        foreach (var wi in candidates)
        {
            try
            {
                if (await this.TryReassignOrFailAsync(db, queue, wi, ct))
                {
                    progressed++;
                }
            }
            catch (Exception ex)
            {
                SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "exception"));
                this.logger.LogWarning(ex, "Sloppy reassignment failed for wi={Wi} — leaving parked", wi.Id);
            }
        }
        return progressed;
    }

    private async Task<bool> TryReassignOrFailAsync(
        ForgeDbContext db,
        ITaskQueueService queue,
        WorkItemRow wi,
        CancellationToken ct)
    {
        // Resolve crew from the latest deliverable (the one that just fired
        // sloppy). Falls back to the latest pair-assignment row's CrewId
        // when no deliverable exists (defensive — should not happen because
        // sloppy is only emitted after a deliverable persists).
        //
        // Note: SQLite cannot ORDER BY DateTimeOffset, so we materialize
        // then sort client-side (matches the convention used elsewhere in
        // this codebase, e.g. CrewQueueWorker.SweepClaimsAsync).
        var delivs = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == wi.Id)
            .ToListAsync(ct);
        var lastDeliv = delivs
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        var crewId = lastDeliv?.CrewId;
        if (string.IsNullOrEmpty(crewId))
        {
            // Pair-assignment fallback uses SequenceNo (int) — safe on SQLite.
            var lastPair = await db.AgentPairAssignments.AsNoTracking()
                .Where(p => p.WorkItemId == wi.Id)
                .OrderByDescending(p => p.SequenceNo)
                .FirstOrDefaultAsync(ct);
            crewId = lastPair?.CrewId;
        }
        if (string.IsNullOrEmpty(crewId))
        {
            this.logger.LogWarning(
                "Sloppy row has no deliverable or pair-assignment to anchor crew lookup: wi={Wi}. Demoting to Failed.",
                wi.Id);
            wi.ExecutionStatus = ExecutionStatuses.Failed;
            wi.FailureReason = "Sloppy-reassign-failed: could not resolve crew";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "no-crew"));
            return false;
        }

        // Terminal-failure path — already at the cap. We don't pick a fresh
        // pair; we stamp the existing latest pair-assignment as
        // terminal-failed and flip the work item to Failed with the locked
        // reason. The counter check happens FIRST so we never accidentally
        // start a fourth attempt.
        if (wi.PairAttemptCount >= ScoringConstants.MaxPairAttempts)
        {
            await this.MarkTerminalFailedAsync(db, wi, ct);
            return true;
        }

        // Reassignment path. Wipe execution-side state for the clean slate,
        // requeue. The CrewQueueWorker picker reads PriorPairsJson and
        // excludes prior pairs from the next claim's busy set.
        var newAttempt = wi.PairAttemptCount + 1;
        wi.PairAttemptCount = newAttempt;
        // Reset the budget window so the new pair's token+cost meter starts
        // at zero. Generator-token cap (Q52.2) and Phase 51 dollar-cap both
        // honor BudgetWindowStartUtc.
        wi.BudgetWindowStartUtc = DateTimeOffset.UtcNow;
        // Clear the Phase 51 rotation hint — Phase 52 owns the exclusion
        // now (via PriorPairsJson). Without this the picker would also try
        // to exclude the last pair on a separate code path.
        wi.LastPairAgentIdsJson = null;
        wi.ExecutionStatus = ExecutionStatuses.Queued;
        wi.UpdatedAt = DateTimeOffset.UtcNow;

        // Commit tracked changes on the work item BEFORE the bulk
        // ExecuteDeleteAsync calls. EF Core 8 ExecuteDeleteAsync runs
        // against the DB outside the change tracker, so we want tracked
        // changes flushed first to avoid the "saved state vs. pending
        // change tracker" race.
        await db.SaveChangesAsync(ct);

        // Q52.4 clean slate wipes — each is a bulk DELETE keyed on
        // WorkItemId; SQLite handles each statement in its own transaction.
        // Order doesn't matter (no FKs across these tables).
        var transcriptDeleted = await db.TaskTranscriptEvents
            .Where(t => t.WorkItemId == wi.Id)
            .ExecuteDeleteAsync(ct);
        var deliverablesDeleted = await db.Deliverables
            .Where(d => d.WorkItemId == wi.Id)
            .ExecuteDeleteAsync(ct);
        var questionsDeleted = await db.DeliverableQuestions
            .Where(q => q.WorkItemId == wi.Id)
            .ExecuteDeleteAsync(ct);

        // Worktree reset is best-effort — a missing worktree (e.g. on a
        // synthetic plan-doc work item) is fine; a failing worktree leaves
        // the row requeued with stale code, which the new pair will see
        // and (probably) overwrite anyway. We log so the operator can
        // diagnose if the agent gets confused.
        if (this.options.CurrentValue.ResetWorktreeOnReassign)
        {
            await this.TryResetWorktreeAsync(db, wi, ct);
        }

        // Pre-pick the generator via the existing diversity resolver so the
        // queue row's preferredAgentId carries the new generator. The
        // evaluator is picked at claim time by CrewQueueWorker; both sides
        // honor PriorPairsJson via the busy-set extension.
        var preferredGenerator = await this.PickFreshGeneratorAsync(db, wi, crewId, ct);
        if (preferredGenerator is null)
        {
            // No eligible pair right now. Roll back the wi state we just
            // committed so the row stays parked and the next tick can
            // try again — leaving it at Queued without an agent would
            // confuse the worker.
            wi.ExecutionStatus = ExecutionStatuses.PendingReassign;
            wi.PairAttemptCount = newAttempt - 1; // don't burn an attempt
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var consecutive = this.noPickTicksByWorkItem.TryGetValue(wi.Id, out var v) ? v + 1 : 1;
            this.noPickTicksByWorkItem[wi.Id] = consecutive;
            if (consecutive >= this.options.CurrentValue.MaxNoPickTicks
                && this.blockedEventEmitted.Add(wi.Id))
            {
                BlockedNoPairCounter.Add(1);
                await this.taskChannel.EmitAsync(wi.Id, "task.sloppy-blocked", new
                {
                    crewId,
                    reason = "no-eligible-pair",
                    consecutiveTicks = consecutive,
                }, ct: ct);
            }
            SkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "no-eligible-pair"));
            return false;
        }

        // Pair found — clear the no-pick counters for this row.
        this.noPickTicksByWorkItem.Remove(wi.Id);
        this.blockedEventEmitted.Remove(wi.Id);

        var queueRow = await queue.EnqueueAsync(
            wi.Id, "crew", crewId, ct,
            kind: TaskQueueKinds.Work,
            preferredAgentId: preferredGenerator);

        ReassignedCounter.Add(1, new KeyValuePair<string, object?>("attempt", newAttempt));
        this.logger.LogInformation(
            "SloppyReassignmentReconciler reassigned wi={Wi} crew={Crew} attempt={Attempt}/{Max} newGen={Gen} "
            + "wipedTranscript={T} wipedDeliverables={D} wipedQuestions={Q}",
            wi.Id, crewId, newAttempt, ScoringConstants.MaxPairAttempts, preferredGenerator,
            transcriptDeleted, deliverablesDeleted, questionsDeleted);

        await this.taskChannel.EmitAsync(wi.Id, "task.sloppy-reassigned", new
        {
            crewId,
            attempt = newAttempt,
            maxAttempts = ScoringConstants.MaxPairAttempts,
            newGeneratorAgentId = preferredGenerator,
            wipedTranscriptEvents = transcriptDeleted,
            wipedDeliverables = deliverablesDeleted,
            queueId = queueRow.Id,
        }, ct: ct);

        // Mirror Phase 51's "stalled workstream un-stalled" hook so a
        // workstream-card STALLED pill (set by the orchestrator after a
        // bad verdict) clears on the next UI tick.
        await this.ClearWorkstreamStallAsync(db, wi.Id, ct);

        await this.hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = queueRow }, ct);

        return true;
    }

    private async Task MarkTerminalFailedAsync(ForgeDbContext db, WorkItemRow wi, CancellationToken ct)
    {
        wi.ExecutionStatus = ExecutionStatuses.Failed;
        wi.FailureReason = ScoringConstants.SloppyTerminalFailureReason;
        wi.UpdatedAt = DateTimeOffset.UtcNow;

        // Close the latest pair-assignment as terminal-failed; the
        // DeliverableLoop already stamped it as reassigned-sloppy but the
        // terminal state takes precedence for KPI accounting (Phase 14.16
        // sloppyRate includes terminal-failed).
        var latestPair = await db.AgentPairAssignments
            .Where(p => p.WorkItemId == wi.Id)
            .OrderByDescending(p => p.SequenceNo)
            .FirstOrDefaultAsync(ct);
        if (latestPair is not null)
        {
            latestPair.Outcome = AgentPairAssignmentOutcomes.TerminalFailed;
            latestPair.EndedAt ??= DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        TerminalFailedCounter.Add(1);
        this.logger.LogWarning(
            "SloppyReassignmentReconciler terminal-failed wi={Wi} after {Attempts} attempts: {Reason}",
            wi.Id, wi.PairAttemptCount, wi.FailureReason);

        await this.taskChannel.EmitAsync(wi.Id, "task.sloppy-terminal-failure", new
        {
            crewId = latestPair?.CrewId,
            attempts = wi.PairAttemptCount,
            failureReason = wi.FailureReason,
        }, ct: ct);

        // Also mark any workstreams pointing at this work item as stalled
        // with a structured reason. Phase 52 spec: only on terminal failure
        // (the reassignment path itself leaves the workstream active).
        var workstreams = await db.Workstreams
            .Where(w => w.Status != WorkstreamStatuses.Stalled)
            .ToListAsync(ct);
        var touched = 0;
        foreach (var w in workstreams)
        {
            var matches = string.Equals(w.Id, wi.Id, StringComparison.Ordinal);
            if (!matches && !string.IsNullOrEmpty(w.CardsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(w.CardsJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String
                                && string.Equals(el.GetString(), wi.Id, StringComparison.Ordinal))
                            {
                                matches = true;
                                break;
                            }
                        }
                    }
                }
                catch (JsonException) { /* malformed CardsJson — skip */ }
            }
            if (!matches) continue;
            w.Status = WorkstreamStatuses.Stalled;
            w.StallReason = "Sloppy-3-attempts: " + ScoringConstants.SloppyTerminalFailureReason;
            w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            touched++;
        }
        if (touched > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pick a generator agent that has NOT been a generator OR evaluator on
    /// any prior pair-attempt for this work item. Returns null when every
    /// roster member is excluded — the caller leaves the row parked and
    /// retries next tick.
    /// </summary>
    private async Task<string?> PickFreshGeneratorAsync(
        ForgeDbContext db, WorkItemRow wi, string crewId, CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
        if (crew is null) return null;

        // Roster of crew members (excluding the lead per Phase 47 — leads
        // aren't typical generators). The fallback inside
        // PairDiversityResolver still allows lead when no member is on
        // shift; the reconciler does not re-implement schedule gating
        // because the picked agent flows into CrewQueueWorker's claim
        // path which re-applies the gate.
        var allMembers = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == crewId)
            .Select(m => m.AgentId)
            .ToListAsync(ct);
        var roster = allMembers
            .Where(id => !string.Equals(id, crew.Lead, StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        // Build the exclusion set from PriorPairsJson + currently-busy.
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var usageBusy = await db.AgentUsage.AsNoTracking()
            .Where(u => u.EndedAt == null)
            .Select(u => u.AgentId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var b in usageBusy)
        {
            if (!string.IsNullOrEmpty(b)) busy.Add(b);
        }
        if (!string.IsNullOrWhiteSpace(wi.PriorPairsJson))
        {
            try
            {
                var priorPairs = JsonSerializer.Deserialize<List<string[]>>(wi.PriorPairsJson);
                if (priorPairs is not null)
                {
                    foreach (var p in priorPairs)
                    {
                        if (p is null) continue;
                        foreach (var id in p)
                        {
                            if (!string.IsNullOrEmpty(id)) busy.Add(id);
                        }
                    }
                }
            }
            catch (JsonException) { /* malformed — skip */ }
        }

        // Caller-supplied seed so two reconciler invocations against the
        // same roster don't always pick the same agent — match Phase 47
        // convention of mixing tick-count + work-item-hash for fairness.
        var seed = unchecked(Environment.TickCount ^ (wi.Id?.GetHashCode(StringComparison.Ordinal) ?? 0));
        var (picked, _) = PairDiversityResolver.PickGenerator(roster, busy, lead: null, seed);
        return picked;
    }

    /// <summary>
    /// Best-effort reset of the work item's worktree branch to the repo's
    /// default base. Q52.4 clean-slate semantics: the new pair starts on
    /// an unmodified base instead of the prior pair's partial commits.
    ///
    /// <para>Failure here is non-fatal — the row remains requeued; the new
    /// pair sees whatever's on the branch (which on a real repo is the
    /// prior pair's commits, but a fresh agent will overwrite them).</para>
    /// </summary>
    private async Task TryResetWorktreeAsync(ForgeDbContext db, WorkItemRow wi, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wi.RepoId)) return;

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return;

        // Worktree path convention from Q2 / Phase 16.18:
        //   {repo.cloneRoot}\.worktree\ws-{workItemId}
        var worktreePath = System.IO.Path.Combine(repo.CloneRoot, ".worktree", "ws-" + wi.Id);
        if (!System.IO.Directory.Exists(worktreePath))
        {
            // No worktree → nothing to reset. Common for synthetic
            // plan-doc work items.
            return;
        }

        var baseBranch = string.IsNullOrEmpty(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
        try
        {
            await RunGitAsync(worktreePath, $"fetch origin {baseBranch}", ct);
            await RunGitAsync(worktreePath, $"reset --hard origin/{baseBranch}", ct);
            await RunGitAsync(worktreePath, "clean -fdx", ct);
            this.logger.LogInformation(
                "Worktree reset for sloppy reassignment: wi={Wi} path={Path} base={Base}",
                wi.Id, worktreePath, baseBranch);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex,
                "Worktree reset failed for sloppy reassignment: wi={Wi} path={Path} — continuing",
                wi.Id, worktreePath);
        }
    }

    private static async Task RunGitAsync(string workingDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Belt-and-suspenders: route through GitProcessEnv so the
        // process-wide prompt policy (FailFast / Interactive) governs
        // this call site too. Direct env-var setting here previously
        // forced FailFast unconditionally, escaping the policy.
        Forge.Api.Services.Git.GitProcessEnv.Apply(psi);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git failed to start");
        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { /* swallow */ }
        });
        // 30s hard cap per git command — fetch should be the only one that
        // can stall on a slow remote, and 30s is plenty for a default-branch
        // fetch (Phase 52 isn't trying to do a full repo sync).
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await p.WaitForExitAsync(linked.Token);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {args} failed ({p.ExitCode}): {err}");
        }
    }

    private async Task ClearWorkstreamStallAsync(ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        // Mirror of BudgetCooldownReconciler.ClearWorkstreamStallAsync — keep
        // both impls in lockstep so a sloppy-reassignment un-stalls the
        // workstream the same way a budget-cooldown resume does. Match
        // by Id-equals OR CardsJson-contains.
        var stalled = await db.Workstreams
            .Where(w => w.Status == WorkstreamStatuses.Stalled)
            .ToListAsync(ct);
        var touched = 0;
        foreach (var w in stalled)
        {
            var matches = string.Equals(w.Id, workItemId, StringComparison.Ordinal);
            if (!matches && !string.IsNullOrEmpty(w.CardsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(w.CardsJson);
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
                catch (JsonException) { /* malformed — skip */ }
            }
            if (!matches) continue;
            w.Status = "active";
            w.StallReason = null;
            w.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            touched++;
        }
        if (touched > 0) await db.SaveChangesAsync(ct);
    }
}
