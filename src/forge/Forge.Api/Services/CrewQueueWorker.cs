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
    /// Run all four sweeps once. Public so tests can drive it deterministically.
    /// </summary>
    public async Task<WorkerTickResult> RunOnceAsync(CancellationToken ct)
    {
        var ackTimeouts = await this.SweepAckTimeoutsAsync(ct);
        var (claims, claimSkips) = await this.SweepClaimsAsync(ct);
        var strainSignals = await this.SweepStrainAsync(ct);
        var recovered = await this.SweepAbandonedAsync(ct);
        return new WorkerTickResult(ackTimeouts, claims, strainSignals, recovered, claimSkips);
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
            var members = await db.CrewMembers.AsNoTracking()
                .Where(m => m.CrewId == crewId && m.AgentId != crew.Lead)
                .Select(m => m.AgentId)
                .ToListAsync(ct);
            var picked = members.Count == 0 ? crew.Lead : members[Math.Abs(Environment.TickCount) % members.Count];

            var row = await queue.ClaimNextAsync(crewId, picked, ct);
            if (row is null)
            {
                skips.Add(new ClaimSkip(crewId, "claim-cas-failed", $"members={members.Count} lead={crew.Lead ?? "(null)"} picked={picked ?? "(null)"}"));
                continue;
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
                // Include evaluator crew + agent in the assignment so the
                // execution endpoint can surface both sides from the start.
                var evalAgentId = crew.DefaultEvaluatorAgentId ?? evaluatorCrew?.Lead;
                var evalAgentJson = evalAgentId is not null ? $"\"{evalAgentId}\"" : "null";
                var evalCrewJson = evaluatorCrew is not null ? $"\"{evaluatorCrew.Id}\"" : "null";
                wi.AssignedToJson =
                    $"{{\"crewId\":\"{crewId}\",\"agentId\":\"{picked}\",\"generatorAgentId\":\"{picked}\",\"evaluatorAgentId\":{evalAgentJson},\"evaluatorCrewId\":{evalCrewJson},\"by\":\"queue-worker\"}}";

                // Auto-link work item to a workstream so the Workstreams view
                // shows the "● N live" badge. Find a workstream for this repo
                // whose cards don't already include this work item.
                await this.AutoLinkToWorkstreamAsync(db, wi, ct);

                await db.SaveChangesAsync(ct);
            }
            DwellMs.Record((DateTimeOffset.UtcNow - row.EnqueuedAt).TotalMilliseconds);
            await this.taskChannel.EmitAsync(row.WorkItemId, "task.delegated", new
            {
                queueId = row.Id,
                generatorCrewId = crewId,
                evaluatorCrewId = evaluatorCrew?.Id,
                memberAgentId = picked,
                paired = evaluatorCrew is not null,
                viaQueue = true,
            }, crewId: crewId, ct: ct);
            await this.BroadcastQueueChangedAsync(row, "picked-up", ct);

            // Kick off the agent runner in the background. The invocation
            // runs asynchronously — the claim sweep doesn't wait for it.
            _ = Task.Run(() => RunAgentAsync(row.WorkItemId, crewId, picked!, crew, wi, ct), ct);
        }
        return (claimed, skips);
    }

    // ── Agent runner kickoff ───────────────────────────────────────────────
    private async Task RunAgentAsync(
        string workItemId, string crewId, string agentId,
        CrewRow crew, WorkItemRow? wi, CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

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

            // Soul.md
            var soulMd = "";
            if (!string.IsNullOrEmpty(agent?.SoulPath))
            {
                try { soulMd = await File.ReadAllTextAsync(agent.SoulPath, ct); }
                catch { /* best effort */ }
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
            if (!string.IsNullOrEmpty(wi?.RepoId))
            {
                var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                if (!string.IsNullOrEmpty(repo?.DefaultBranch))
                    baseBranch = repo.DefaultBranch;
                if (!string.IsNullOrEmpty(repo?.CloneRoot) && Directory.Exists(repo.CloneRoot))
                    repoCloneRoot = repo.CloneRoot;
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
                        worktreePath = existingWtPath;
                        this.logger.LogInformation(
                            "Reusing existing worktree: path={Path} branch={Branch}",
                            worktreePath, branchName);
                    }
                    else
                    {
                        // Create the worktree root directory and the worktree.
                        Directory.CreateDirectory(wtRoot);
                        var (code, _, stderr) = await RunGitAsync(repoCloneRoot,
                            "worktree", "add", "-b", branchName, worktreeDir, baseBranch);
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
            var prompt = string.IsNullOrWhiteSpace(desc)
                ? $"Work item: {title}"
                : $"Work item: {title}\n\n{desc}";

            this.logger.LogInformation(
                "Starting agent invocation: wi={Wi} agent={Agent} crew={Crew} model={Model} backend={Backend} mode={Mode} worktree={Worktree}",
                workItemId, agentId, crewId, model, backend, invokeMode, worktreePath);

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
            var context = new AgentContext(
                CrewId: crewId,
                Model: model,
                SoulMd: soulMd,
                WorktreePath: worktreePath,
                AllowedTools: Array.Empty<string>(),
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                Backend: backend,
                InvokeMode: invokeMode);

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

                    if (!string.IsNullOrEmpty(evt.Text)) output.Append(evt.Text);
                    if (evt.CostUsd.HasValue) cost += evt.CostUsd.Value;
                }

                return new GeneratorRunOutput(output.ToString(), cost);
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
                if (!string.IsNullOrEmpty(evalAgent?.SoulPath))
                {
                    try { evalSoul = await File.ReadAllTextAsync(evalAgent.SoulPath, iterCt); }
                    catch { /* best effort */ }
                }

                var evalBackendStr = !string.IsNullOrEmpty(evalAgent?.Backend) ? evalAgent!.Backend : evalCrew.Backend;
                var evalBackend = string.Equals(evalBackendStr, "claude", StringComparison.OrdinalIgnoreCase)
                    ? AgentBackend.Claude : AgentBackend.Copilot;

                // The evaluator uses SDK mode but with a system message that
                // explicitly forbids tool use and demands structured JSON output.
                // This ensures the response is text-only with a parseable score.
                var evalSoulForScoring = (string.IsNullOrEmpty(evalSoul) ? "" : evalSoul + "\n\n---\n\n")
                    + "CRITICAL RULES FOR THIS SESSION:\n"
                    + "1. Do NOT call any tools. Do NOT use grep, view, edit, or any other tool.\n"
                    + "2. Review the work output provided in the user message.\n"
                    + "3. End your response with a fenced JSON block:\n"
                    + "```json\n{ \"score\": <0-100>, \"notes\": \"<feedback>\" }\n```\n"
                    + "4. Score >= 90 = pass, 70-89 = iterate, < 70 = reject.\n";

                var evalContext = new AgentContext(
                    CrewId: evaluatorCrewId!,
                    Model: evalModel,
                    SoulMd: evalSoulForScoring,
                    WorktreePath: worktreePath,
                    AllowedTools: Array.Empty<string>(),
                    MaxTaskBudgetUsd: evalCrew.MaxTaskBudgetUsd,
                    Backend: evalBackend,
                    InvokeMode: AgentInvokeMode.Sdk);

                var evalPrompt = "You are a critical code reviewer scoring agent work.\n\n"
                    + "IMPORTANT: You MUST end your response with a fenced JSON block in exactly this format:\n"
                    + "```json\n"
                    + "{ \"score\": <0-100>, \"notes\": \"<your feedback>\" }\n"
                    + "```\n\n"
                    + "Scoring rubric:\n"
                    + "- score >= 90: pass (code is correct, tested, ready to merge)\n"
                    + "- score 70-89: iterate (has issues that need fixing — describe them in notes)\n"
                    + "- score < 70: reject (fundamentally wrong approach)\n\n"
                    + $"Review the following work output from iteration {iter}:\n\n"
                    + genOutput.Substring(0, Math.Min(genOutput.Length, 50000));

                var invocation = await runner.StartAsync(workItemId, evaluatorAgentId, evalPrompt, evalContext, iterCt);
                var evalOutput = new System.Text.StringBuilder();
                decimal evalCost = 0m;

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
                }

                return new EvaluatorRunOutput(evalOutput.ToString(), evalCost);
            }

            var loopRequest = new DeliverableLoopRequest(
                WorkItemId: workItemId,
                CrewId: crewId,
                GeneratorAgentId: agentId,
                EvaluatorAgentId: evaluatorAgentId ?? agentId,
                Generator: GeneratorDelegate,
                Evaluator: EvaluatorDelegate,
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                EvaluatorCrewId: evaluatorCrewId);

            var result = await loop.RunAsync(loopRequest, ct);

            this.logger.LogInformation(
                "Deliverable loop completed: wi={Wi} verdict={Verdict} score={Score} iters={Iters} cost={Cost}",
                workItemId, result.FinalVerdict, result.FinalScore, result.Iterations, result.TotalCostUsd);

            // (4) On pass, push branch then create PR from worktree branch → base.
            if (result.FinalVerdict == "pass")
            {
                // Push the branch to the remote so ADO/GitHub can see it.
                var pushCwd = repoCloneRoot ?? worktreePath;
                if (!string.IsNullOrEmpty(pushCwd))
                {
                    var (pushCode, pushOut, pushErr) = await RunGitAsync(pushCwd, "push", "origin", branchName);
                    if (pushCode == 0)
                    {
                        this.logger.LogInformation(
                            "Pushed branch to remote: wi={Wi} branch={Branch}", workItemId, branchName);
                    }
                    else
                    {
                        this.logger.LogWarning(
                            "git push failed (exit {Code}): {Stderr}", pushCode, pushErr);
                    }
                }

                try
                {
                    // Pick the correct provider based on the repo's kind (ado / github).
                    var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<Forge.Api.Providers.IForgeProvider>>();
                    var repoKind = "ado";
                    string? prRepoName = null;
                    string? prProjectName = null;
                    if (!string.IsNullOrEmpty(wi?.RepoId))
                    {
                        var prRepo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                        if (!string.IsNullOrEmpty(prRepo?.Kind)) repoKind = prRepo.Kind;
                        // Extract the ADO repository name from the remote URL
                        // (e.g. https://dev.azure.com/org/project/_git/reponame).
                        if (prRepo is not null && repoKind == "ado")
                        {
                            prRepoName = ExtractAdoRepoName(prRepo.RemoteUrl);
                            if (!string.IsNullOrEmpty(prRepo.AdoProject))
                            {
                                prProjectName = prRepo.AdoProject;
                            }
                        }
                    }
                    var provider = providers.FirstOrDefault(p => p.Kind == repoKind && p.IsConfigured)
                        ?? providers.FirstOrDefault(p => p.IsConfigured);
                    if (provider is null)
                    {
                        throw new InvalidOperationException($"No configured provider found for kind '{repoKind}'");
                    }

                    var prTitle = wi?.Title ?? workItemId;
                    var prBody = $"Auto-generated by Forge agent {agentId} (crew {crewId}).\n\n"
                        + $"**Score:** {result.FinalScore} | **Iterations:** {result.Iterations} | **Cost:** ${result.TotalCostUsd:F2}";

                    var pr = await provider.CreatePullRequestAsync(
                        new Forge.Api.Providers.NewPullRequestRequest(branchName, baseBranch, prTitle, prBody,
                            RepositoryName: prRepoName, ProjectName: prProjectName),
                        Forge.Api.Providers.ProviderCallContext.Anonymous, ct);

                    await this.taskChannel.EmitAsync(workItemId, "task.pr-opened", new
                    {
                        prId = pr.Id,
                        prTitle = pr.Title,
                        branch = branchName,
                        baseBranch,
                    }, crewId: crewId, ct: ct);

                    this.logger.LogInformation(
                        "PR created: wi={Wi} pr={Pr} branch={Branch}", workItemId, pr.Id, branchName);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "PR creation failed for wi={Wi}", workItemId);
                    await this.taskChannel.EmitAsync(workItemId, "task.pr-failed", new
                    {
                        error = ex.Message,
                        branch = branchName,
                    }, crewId: crewId, ct: ct);
                }

                // (5) Transition work item to "active" (build column on the
                //     kanban board). The PR review cycle drives the subsequent
                //     resolved → closed transitions.
                try
                {
                    var wiRow = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
                    if (wiRow is not null)
                    {
                        wiRow.State = "active";
                        wiRow.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Work item state transition failed for wi={Wi}", workItemId);
                }
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
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex,
                "Agent invocation failed: wi={Wi} agent={Agent} crew={Crew}",
                workItemId, agentId, crewId);
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
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to set execution status for {Wi}", workItemId);
        }
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
        if (recovered > 0) await db.SaveChangesAsync(ct);
        return recovered;
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

    /// <summary>Counts emitted by one tick.</summary>
    public sealed record WorkerTickResult(
        int AckTimeouts,
        int Claims,
        int StrainSignals,
        int Recovered,
        IReadOnlyList<ClaimSkip>? ClaimSkips = null);

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
