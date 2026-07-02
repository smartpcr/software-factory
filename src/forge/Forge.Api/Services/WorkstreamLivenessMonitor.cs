// -----------------------------------------------------------------------
// <copyright file="WorkstreamLivenessMonitor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Chat;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Detects <b>silent stalls</b> — workstreams the orchestrator forgot to
/// drive forward — and annotates them as <see cref="WorkstreamStatuses.Stalled"/>
/// with a structured <c>StallReason</c> so an operator can recover via the
/// existing <c>POST /api/workstreams/{id}/restart</c> endpoint.
///
/// <para><b>What is a silent stall.</b> A workstream whose
/// <see cref="WorkstreamRow.Status"/> claims <c>active</c> AND whose
/// <see cref="WorkItemRow.ExecutionStatus"/> is <c>idle</c> (or null) for
/// longer than <c>IdleThreshold</c>, with <b>no</b> evidence of forward
/// progress: no open <see cref="TaskQueueRow"/>, no live
/// <see cref="IAgentRunner"/> registration, no recent transcript / deliverable
/// rows, and no unmet upstream dependency. Two real incidents motivated
/// this monitor:
/// <list type="bullet">
/// <item><c>ws-…-pri-postgres</c>, 2026-06-04 — idle 4h 26m undetected
///   after `Forge.Api` restart left the workstream in active-idle limbo.</item>
/// <item><c>ws-…-end-to-end-backfill-scenario</c>, created 2026-06-01 — idle
///   <b>~3 days</b> with <c>TrialCount=0</c>, never enqueued into any crew.</item>
/// </list>
/// None of the existing reactive reconcilers
/// (<see cref="StalledMergedRecoveryReconciler"/>,
/// <see cref="SloppyReassignmentReconciler"/>,
/// <see cref="BudgetCooldownReconciler"/>,
/// <see cref="WorktreeReaper"/>,
/// <c>/api/admin/workstreams/unblock-stale</c>, the in-loop
/// <c>NoProgressException</c> convergence detector) cover this class —
/// each watches a single known failure shape; none asks the underlying
/// liveness question.</para>
///
/// <para><b>Detection vs auto-restart.</b> v1 is <i>detection-only</i>:
/// it flips <c>Status=stalled</c> with a tagged <c>StallReason</c>
/// (<c>[silent-stall-before-claim]</c> or <c>[silent-stall-with-stale-worktree]</c>)
/// and emits a <c>workstream.silent_stall_detected</c> SignalR event so the
/// dashboard surfaces it immediately. Auto-restart is deferred to v2 once
/// an idempotent <c>EnqueueAsync</c> recovery path is proven; doing it here
/// risks clearing legitimate state for a workstream that is genuinely
/// idle for non-stall reasons (e.g. mid-handoff between iters that this
/// monitor's threshold-based predicate happened to race).</para>
///
/// <para><b>Configuration</b> (defaults shown):</para>
/// <list type="bullet">
/// <item><c>Forge:WorkstreamLivenessMonitor:Enabled</c> — true. Set false to disable.</item>
/// <item><c>Forge:WorkstreamLivenessMonitor:TickInterval</c> — 00:05:00 (5 minutes).</item>
/// <item><c>Forge:WorkstreamLivenessMonitor:IdleThreshold</c> — 00:30:00 (30 minutes).</item>
/// </list>
/// </summary>
public sealed class WorkstreamLivenessMonitor : BackgroundService
{
    /// <summary>Structured prefix for the <c>StallReason</c> annotation when the
    /// workstream stalled before any worktree was ever created (the canonical
    /// "orchestrator forgot to enqueue it" shape). UI / log consumers should
    /// branch on this prefix rather than free-text matching.</summary>
    public const string StallCodeBeforeClaim = "silent-stall-before-claim";

    /// <summary>Structured prefix for the <c>StallReason</c> annotation when an
    /// on-disk worktree exists but the workstream has been idle past
    /// the threshold (the "agent started once, queue forgot it" shape).</summary>
    public const string StallCodeStaleWorktree = "silent-stall-with-stale-worktree";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHubContext<ForgeHub>? hub;
    private readonly IAgentRunner? runner;
    private readonly IConfiguration config;
    private readonly ILogger<WorkstreamLivenessMonitor> logger;

    public WorkstreamLivenessMonitor(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<WorkstreamLivenessMonitor> logger,
        IHubContext<ForgeHub>? hub = null,
        IAgentRunner? runner = null)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
        this.hub = hub;
        this.runner = runner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot delay: don't race migrations / EnsureCreated on the very first tick.
        var interval = this.GetTickInterval();
        try { await Task.Delay(interval, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (this.IsEnabled())
                {
                    await this.RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "WorkstreamLivenessMonitor tick errored — continuing");
            }

            try { await Task.Delay(this.GetTickInterval(), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick across every product. Public so tests + an admin endpoint
    /// can drive it without waiting for the timer. Returns the number of
    /// workstreams newly annotated as silently stalled.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var threshold = this.GetIdleThreshold();
        using var scope = this.scopeFactory.CreateScope();
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var totalDetected = 0;
        var sync = new object();

        await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
        {
            var detected = await this.RunForProductAsync(control, db, slug, threshold, fct);
            if (detected > 0)
            {
                lock (sync) totalDetected += detected;
            }
        }, ct);

        return totalDetected;
    }

    /// <summary>
    /// Single-product scan. Factored out so unit tests can drive one
    /// product DB directly without the fanout. Returns the count of
    /// rows newly annotated this tick.
    /// </summary>
    public async Task<int> RunForProductAsync(
        ControlDbContext control,
        ProductDbContext db,
        string slug,
        TimeSpan idleThreshold,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - idleThreshold;

        // Alert sweep (independent of the silent-stall detector below): a
        // workstream can land in `stalled` status from paths this monitor never
        // sees — the orchestrator's terminal triage (gen-eval non-convergence,
        // build-fail), the sloppy/budget reconcilers, etc. Those set Status +
        // StallReason but never enqueue a chat alert, so the operator gets a
        // STALLED badge and no notification. Sweep them here so every stalled
        // workstream reaches chat. Idempotent (the notifier dedupes on
        // ws+reason) and best-effort — never affects stall detection.
        await this.SweepStalledForAlertsAsync(db, slug, ct);

        // Candidate pre-filter: cheap query over `work_items`.
        // UpdatedAt < cutoff is a NECESSARY but NOT sufficient condition;
        // we re-confirm with a compound activity watermark in the per-row
        // pass below. Excludes parked execution states that have their own
        // dedicated reconcilers (cooldown-budget, pending-reassign,
        // awaiting-answers) — those rows are not stalled in this sense.
        //
        // Two-step query (WorkItems then in-memory join with Workstreams)
        // because EF can't translate the LINQ Join across the two tables
        // — WorkstreamRow has REAL-backed timestamp columns that block
        // the relational translator.
        // EF SQLite translator can't handle `DateTimeOffset < parameter` in
        // a Where clause (mapped with a value converter); existing
        // convention (AgentMemoryReaper, CrewQueueWorker) is to materialize
        // and filter dates in memory. The active+state prefilter still runs
        // in SQL, so the in-memory step works on a bounded set.
        var wisRaw = await db.WorkItems.AsNoTracking()
            .Where(wi => wi.State == WorkItemStateActiveLiteral)
            .Select(wi => new
            {
                wi.Id,
                wi.CreatedAt,
                wi.UpdatedAt,
                wi.ExecutionStatus,
            })
            .ToListAsync(ct);
        var wisFiltered = wisRaw
            .Where(wi => wi.UpdatedAt < cutoff
                && (wi.ExecutionStatus is null
                    || wi.ExecutionStatus.Length == 0
                    || string.Equals(wi.ExecutionStatus, ExecutionStatuses.Idle, StringComparison.Ordinal)))
            .ToList();
        if (wisFiltered.Count == 0) return 0;
        var wsIds = wisFiltered.Select(x => x.Id).ToList();
        var wsRows = await db.Workstreams.AsNoTracking()
            .Where(w => wsIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Status, w.StallReason, w.DepsJson, w.RepoId, w.Branch })
            .ToListAsync(ct);
        var wsLookup = wsRows.ToDictionary(w => w.Id, StringComparer.Ordinal);

        var candidates = new List<CandidateRow>();
        foreach (var wi in wisFiltered)
        {
            if (!wsLookup.TryGetValue(wi.Id, out var ws)) continue;
            if (!string.Equals(ws.Status, WorkstreamStatusActiveLiteral, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(ws.StallReason)) continue;
            candidates.Add(new CandidateRow
            {
                WorkItemId = wi.Id,
                WorkItemCreatedAt = wi.CreatedAt,
                WorkItemUpdatedAt = wi.UpdatedAt,
                ExecutionStatus = wi.ExecutionStatus,
                DepsJson = ws.DepsJson,
                RepoId = ws.RepoId,
                Branch = ws.Branch,
            });
        }
        if (candidates.Count == 0) return 0;

        var detected = 0;
        foreach (var cand in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await this.IsSilentStallAsync(control, db, cand, cutoff, ct) is StallVerdict verdict)
                {
                    if (await this.RecordSilentStallAsync(db, cand, verdict, now, slug, ct))
                    {
                        detected++;
                    }
                    else
                    {
                    }
                }
                else
                {
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? $" / inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                this.logger.LogWarning(ex,
                    "WorkstreamLivenessMonitor: per-row evaluation failed for {WorkItem} (product {Slug}); skipping.",
                    cand.WorkItemId, slug);
            }
        }

        return detected;
    }

    /// <summary>
    /// Compound liveness check. Returns <c>null</c> when the workstream is
    /// legitimately active (open queue row / live runner / recent activity
    /// / unmet dep), or a <see cref="StallVerdict"/> describing the silent
    /// stall otherwise. Side-effect free — caller writes the annotation.
    /// </summary>
    private async Task<StallVerdict?> IsSilentStallAsync(
        ControlDbContext control,
        ProductDbContext db,
        CandidateRow cand,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        // (1) Live runner registered → not stalled. The runner registry is
        //     authoritative — even if all DB rows are quiet, a live agent
        //     is making progress.
        if (this.runner is not null && this.runner.IsTaskActive(cand.WorkItemId))
        {
            return null;
        }

        // (2) Open task_queue row in any of the open states → claim or routing
        //     is in flight; the queue worker will progress it. EF SQLite
        //     translator chokes on `string[].Contains` (parameter-extraction
        //     for ReadOnlySpan<string>); convert to List<string> first.
        var openStates = new List<string>(TaskQueueStates.OpenStates);
        var hasOpenQueue = await db.TaskQueue.AsNoTracking()
            .AnyAsync(q => q.WorkItemId == cand.WorkItemId
                        && openStates.Contains(q.State), ct);
        if (hasOpenQueue) return null;

        // (3) Recent transcript / deliverable row → DeliverableLoop is mid-iter.
        var recentTranscriptAts = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(t => t.WorkItemId == cand.WorkItemId)
            .Select(t => t.At)
            .ToListAsync(ct);
        if (recentTranscriptAts.Any(at => at > cutoff)) return null;
        var recentDeliverableAts = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == cand.WorkItemId)
            .Select(d => d.CreatedAt)
            .ToListAsync(ct);
        if (recentDeliverableAts.Any(at => at > cutoff)) return null;

        // (4) Unmet upstream dependency → legitimately blocked, not silently
        //     stalled. Walk the deps JSON and see whether every dep is in a
        //     terminal "delivered" state.
        if (await this.HasUnmetDepAsync(db, cand.DepsJson, ct)) return null;

        // (5) Worktree existence — EVIDENCE, not gating. Distinguishes the
        //     "agent started once then queue forgot it" case from the
        //     canonical "never claimed in the first place" case so the
        //     operator gets a more actionable StallReason.
        var hasWorktree = false;
        try
        {
            var ws = await db.Workstreams.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == cand.WorkItemId, ct);
            if (ws is not null)
            {
                var repo = string.IsNullOrEmpty(cand.RepoId)
                    ? null
                    : await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == cand.RepoId, ct);
                var path = await WorktreePathResolver.ResolveForWorkstreamAsync(control, db, ws, repo, ct);
                if (path is not null && !string.IsNullOrEmpty(path.WorktreePath) && Directory.Exists(path.WorktreePath))
                {
                    hasWorktree = true;
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort — a worktree path resolution failure must never
            // suppress detection; we just lose the "stale-worktree" nuance.
            this.logger.LogDebug(ex,
                "WorkstreamLivenessMonitor: worktree resolution failed for {WorkItem}; treating as no-worktree.",
                cand.WorkItemId);
        }

        var idle = DateTimeOffset.UtcNow - cand.WorkItemUpdatedAt;
        var code = hasWorktree ? StallCodeStaleWorktree : StallCodeBeforeClaim;
        return new StallVerdict(code, idle, hasWorktree);
    }

    /// <summary>
    /// Apply the stall annotation under a final race-check: re-read the
    /// workstream + work-item rows; only write when neither has been
    /// touched by another path since the candidate snapshot. Returns true
    /// when the write landed, false on a concurrent change race.
    /// </summary>
    private async Task<bool> RecordSilentStallAsync(
        ProductDbContext db,
        CandidateRow cand,
        StallVerdict verdict,
        DateTimeOffset now,
        string slug,
        CancellationToken ct)
    {
        var freshWs = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == cand.WorkItemId, ct);
        var freshWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == cand.WorkItemId, ct);
        if (freshWs is null || freshWi is null) return false;

        // Race: another writer touched either row after our snapshot →
        // bail. Use a 1-second tolerance because DateTimeOffset round-trips
        // through SQLite's value converter at sub-second precision; an
        // exact `>` comparison false-positives even when nothing changed.
        if (!string.Equals(freshWs.Status, WorkstreamStatusActiveLiteral, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(freshWs.StallReason)) return false;
        if (freshWi.UpdatedAt > cand.WorkItemUpdatedAt.AddSeconds(1)) return false;
        if (!string.IsNullOrEmpty(freshWi.ExecutionStatus)
            && !string.Equals(freshWi.ExecutionStatus, ExecutionStatuses.Idle, StringComparison.Ordinal))
        {
            return false;
        }

        freshWs.Status = WorkstreamStatuses.Stalled;
        freshWs.StallReason =
            $"[{verdict.Code}] idle for {FormatDuration(verdict.IdleDuration)} with no open queue row, no live agent, "
            + "no recent transcript/deliverable, and no unmet upstream dependency. "
            + (verdict.HasStaleWorktree
                ? "A worktree directory exists but no activity has touched it — the prior run was abandoned. "
                : "No worktree was ever created — the orchestrator forgot to enqueue this workstream. ")
            + $"Recover with `POST /api/workstreams/{cand.WorkItemId}/restart`.";

        await db.SaveChangesAsync(ct);

        this.logger.LogWarning(
            "WorkstreamLivenessMonitor: silent stall detected — workstream={WorkItem} code={Code} idle={Idle}",
            cand.WorkItemId, verdict.Code, verdict.IdleDuration);

        // (chat alert emitted at the end of this method — see TryEnqueueStalledChatAlertAsync)

        if (this.hub is not null)
        {
            try
            {
                await this.hub.Clients.All.SendAsync(
                    "workstream.silent_stall_detected",
                    new
                    {
                        id = cand.WorkItemId,
                        code = verdict.Code,
                        idleSeconds = (long)verdict.IdleDuration.TotalSeconds,
                        hasStaleWorktree = verdict.HasStaleWorktree,
                        at = now,
                    }, ct);
                await this.hub.Clients.All.SendAsync(
                    "workstream.changed",
                    new { kind = "silent-stall", id = cand.WorkItemId }, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex,
                    "WorkstreamLivenessMonitor: SignalR notify failed for {WorkItem}; annotation persisted regardless.",
                    cand.WorkItemId);
            }
        }

        // Phase 95 (Phase 5) — alert the operator over chat with action buttons.
        var stageNumber = await this.ResolveStageNumberAsync(db, cand.WorkItemId, freshWs.StorySpawnId, ct);
        await this.TryEnqueueStalledChatAlertAsync(
            cand.WorkItemId, freshWs.RepoId, freshWs.StallReason, freshWs.StorySpawnId, slug, freshWs.Name, stageNumber, ct);

        return true;
    }

    /// <summary>
    /// Phase 95 (Phase 5) — best-effort chat alert that a workstream stalled,
    /// with inline [Retry] [Accept] [Cancel] [Restart] buttons (callback
    /// <c>act|{action}|{workstreamId}</c>). Never affects stall detection.
    /// </summary>
    /// <summary>
    /// Emit a chat alert for every workstream already sitting in <c>stalled</c>
    /// status with a reason — whatever set it (orchestrator terminal triage,
    /// sloppy / budget reconcilers, or this monitor's own silent-stall pass).
    /// The notifier's dedupe key keeps at most one open alert per ws+reason, so
    /// this is safe to run every tick. Fully best-effort.
    /// </summary>
    private async Task SweepStalledForAlertsAsync(ProductDbContext db, string slug, CancellationToken ct)
    {
        try
        {
            var stalledStatus = WorkstreamStatuses.Stalled;
            var stalled = await db.Workstreams.AsNoTracking()
                .Where(w => w.Status == stalledStatus && w.StallReason != null && w.StallReason != "")
                .Select(w => new { w.Id, w.RepoId, w.StallReason, w.StorySpawnId, w.Name })
                .ToListAsync(ct);

            foreach (var w in stalled)
            {
                var stageNumber = await this.ResolveStageNumberAsync(db, w.Id, w.StorySpawnId, ct);
                await this.TryEnqueueStalledChatAlertAsync(w.Id, w.RepoId, w.StallReason, w.StorySpawnId, slug, w.Name, stageNumber, ct);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "WorkstreamLivenessMonitor: stalled-alert sweep failed; continuing.");
        }
    }

    private async Task TryEnqueueStalledChatAlertAsync(
        string workstreamId, string? repoId, string? stallReason, string? spawnId,
        string? productId, string? stageName, string? stageNumber, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetService<IChatNotifier>();
            if (notifier is null) return;

            var reason = string.IsNullOrWhiteSpace(stallReason) ? "Workstream stalled." : stallReason!;

            // Callback data carries ONLY the action — the workstream id is
            // resolved from the correlated notification on tap. Embedding the id
            // blows past Telegram's 64-byte callback_data cap for long ws ids
            // (BUTTON_DATA_INVALID), which silently wedged the alert as unsent.
            var buttons = new[]
            {
                new ChatButton("🔁 Retry", "act|retry"),
                new ChatButton("✅ Accept", "act|accept"),
                new ChatButton("🚫 Cancel", "act|cancel"),
                new ChatButton("⚠ Restart", "act|restart"),
            };

            // Lead with the STAGE identity (number + name) so multiple stalled
            // workstreams within one spawn — which all route to the same topic —
            // are individually identifiable, plus the product · repo · spawn scope.
            var reasonBody = reason.Length > 1500 ? reason[..1500] : reason;
            var stage = (stageNumber, stageName) switch
            {
                // A plan-stage number uniquely identifies the stage within its
                // spawn/topic — no ws id needed.
                ({ Length: > 0 } n, { Length: > 0 } s) => $"📍 Stage {n} — {s}",
                ({ Length: > 0 } n, _) => $"📍 Stage {n}",
                // No number (ad-hoc / legacy workstream, e.g. spawn-less stalls in
                // the group root): ALWAYS include the unique ws id so two stalls
                // sharing a name/reason are distinguishable.
                (_, { Length: > 0 } s) => $"📍 {s}\n🆔 {workstreamId}",
                _ => $"📍 {workstreamId}",
            };
            var scopeParts = new[]
            {
                string.IsNullOrEmpty(productId) ? null : productId,
                string.IsNullOrEmpty(repoId) ? null : $"repo {repoId}",
                string.IsNullOrEmpty(spawnId) ? null : spawnId,
            };
            var scopeLine = string.Join(" · ", scopeParts.Where(p => !string.IsNullOrEmpty(p)));
            var body = string.IsNullOrEmpty(scopeLine)
                ? $"{stage}\n\n{reasonBody}"
                : $"{stage}\n🔧 {scopeLine}\n\n{reasonBody}";

            var title = stageNumber is { Length: > 0 } num ? $"⚠ Stalled · Stage {num}" : "⚠ Workstream stalled";

            await notifier.EnqueueWorkstreamStalledAsync(
                new WorkstreamStalledAlert(
                    ProductId: productId ?? string.Empty,
                    RepoId: repoId,
                    WorkstreamId: workstreamId,
                    StallReason: reason,
                    Title: title,
                    Body: body,
                    Buttons: buttons,
                    SpawnId: spawnId),
                ct);
        }
        catch
        {
            // Best-effort — never let chat delivery affect stall detection.
        }
    }

    /// <summary>
    /// Resolve a stalled workstream's canonical <c>{phase}.{stage}</c> label
    /// (e.g. <c>1.2-e2e</c>) by numbering its whole spawn the same way the swarm
    /// board does (<see cref="StageNumbering"/>). Null when there's no spawn or
    /// the row can't be labelled. Best-effort.
    /// </summary>
    private async Task<string?> ResolveStageNumberAsync(ProductDbContext db, string workstreamId, string? spawnId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spawnId)) return null;
        try
        {
            var rows = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == spawnId)
                .OrderBy(w => w.Sort)
                .Select(w => new { w.Id, w.PhaseAnchor, w.PlanAnchor, w.Category })
                .ToListAsync(ct);
            var stageWs = rows
                .Select(r => new StageNumbering.StageWs(r.Id, r.PhaseAnchor, r.PlanAnchor, r.Category))
                .ToList();
            return StageNumbering.Compute(stageWs).TryGetValue(workstreamId, out var num) ? num : null;
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "WorkstreamLivenessMonitor: stage-number resolve failed for {WorkItem}; omitting.", workstreamId);
            return null;
        }
    }

    /// <summary>
    /// Best-effort dep check: parse the workstream's <c>DepsJson</c> (an
    /// array of dep work-item ids) and report whether ANY referenced
    /// workstream is still in a non-terminal status. Conservative — on
    /// parse failure or missing dep row, treats the dep as <i>unmet</i>
    /// so we don't false-positive a workstream that's correctly waiting.
    /// </summary>
    private async Task<bool> HasUnmetDepAsync(ProductDbContext db, string? depsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(depsJson) || depsJson == "[]" || depsJson == "null") return false;
        IReadOnlyList<string> depIds;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(depsJson);
            depIds = parsed ?? Array.Empty<string>();
        }
        catch
        {
            // Malformed deps JSON: be conservative — assume an unmet dep
            // exists so we don't mis-flag a workstream that's correctly
            // blocked on something we couldn't parse.
            return true;
        }

        if (depIds.Count == 0) return false;
        var depStatuses = await db.Workstreams.AsNoTracking()
            .Where(w => depIds.Contains(w.Id))
            .Select(w => w.Status)
            .ToListAsync(ct);

        // Missing dep rows OR any non-complete dep → unmet.
        if (depStatuses.Count < depIds.Count) return true;
        foreach (var s in depStatuses)
        {
            if (!string.Equals(s, WorkstreamStatusCompleteLiteral, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s, WorkstreamStatuses.Historic, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ── configuration ───────────────────────────────────────────────────

    private bool IsEnabled() =>
        !bool.TryParse(this.config["Forge:WorkstreamLivenessMonitor:Enabled"], out var enabled) || enabled;

    private TimeSpan GetTickInterval()
    {
        var raw = this.config["Forge:WorkstreamLivenessMonitor:TickInterval"];
        if (TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero) return parsed;
        return TimeSpan.FromMinutes(5);
    }

    private TimeSpan GetIdleThreshold()
    {
        var raw = this.config["Forge:WorkstreamLivenessMonitor:IdleThreshold"];
        if (TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero) return parsed;
        return TimeSpan.FromMinutes(30);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays}d{d.Hours:D2}h"
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:D2}m"
        : $"{(int)d.TotalMinutes}m";

    // ── well-known literals (avoid scattered string typos) ───────────────

    // `WorkstreamRow.Status` is a free-form column; production rows use
    // these literal values (mirrored from PlanReconciler / orchestrator).
    private const string WorkstreamStatusActiveLiteral = "active";
    private const string WorkstreamStatusCompleteLiteral = "complete";

    // `WorkItemRow.State` is also free-form; production uses `"active"` for
    // any non-terminal row (see WorkItemSyncJob projection).
    private const string WorkItemStateActiveLiteral = "active";

    private sealed class CandidateRow
    {
        public string WorkItemId { get; init; } = string.Empty;
        public DateTimeOffset WorkItemCreatedAt { get; init; }
        public DateTimeOffset WorkItemUpdatedAt { get; init; }
        public string? ExecutionStatus { get; init; }
        public string? DepsJson { get; init; }
        public string? RepoId { get; init; }
        public string? Branch { get; init; }
    }

    private sealed record StallVerdict(string Code, TimeSpan IdleDuration, bool HasStaleWorktree);
}
