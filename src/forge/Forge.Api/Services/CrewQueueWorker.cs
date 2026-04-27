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

            // Subscribe the worktree feed so the Live Execution view receives
            // file-change events while the agent is running.
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
                "Starting agent invocation: wi={Wi} agent={Agent} crew={Crew} model={Model} backend={Backend} mode={Mode}",
                workItemId, agentId, crewId, model, backend, invokeMode);

            var context = new AgentContext(
                CrewId: crewId,
                Model: model,
                SoulMd: soulMd,
                WorktreePath: worktreePath,
                AllowedTools: Array.Empty<string>(),
                MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
                Backend: backend,
                InvokeMode: invokeMode);

            var invocation = await runner.StartAsync(workItemId, agentId, prompt, context, ct);

            // Consume the event stream and emit to the transcript channel.
            // Event names are normalised to the task.* contract that the UI expects.
            await foreach (var evt in invocation.Events.WithCancellation(ct))
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
                }, crewId: crewId, ct: ct);
            }

            this.logger.LogInformation(
                "Agent invocation completed: wi={Wi} agent={Agent} crew={Crew} invocation={Inv}",
                workItemId, agentId, crewId, invocation.InvocationId);

            // Transition queue row: picked-up → completed.
            await this.CompleteQueueRowAsync(workItemId, ct);
            await this.taskChannel.EmitAsync(workItemId, "task.completed", new
            {
                invocationId = invocation.InvocationId,
                agentId,
                crewId,
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
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to complete queue row for {Wi}", workItemId);
        }
    }

    /// <summary>
    /// Auto-link a work item to an active workstream that shares the same repo.
    /// If the work item is already in a workstream's cards, this is a no-op.
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

            // Find a workstream for the same repo (prefer active ones).
            WorkstreamRow? target = null;
            if (!string.IsNullOrEmpty(wi.RepoId))
            {
                target = allWs
                    .Where(ws => ws.RepoId == wi.RepoId && ws.Status == "active")
                    .OrderByDescending(ws => ws.LastActive)
                    .FirstOrDefault();
            }
            // Fallback: any active workstream in the product.
            target ??= allWs
                .Where(ws => ws.Status == "active")
                .OrderByDescending(ws => ws.LastActive)
                .FirstOrDefault();

            if (target is null) return;

            // Add the work item ID to the workstream's cards.
            var tracked = await db.Workstreams.FirstOrDefaultAsync(ws => ws.Id == target.Id, ct);
            if (tracked is null) return;
            var existingCards = System.Text.Json.JsonSerializer.Deserialize<List<string>>(tracked.CardsJson) ?? new();
            existingCards.Add(wi.Id);
            tracked.CardsJson = System.Text.Json.JsonSerializer.Serialize(existingCards);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Auto-link to workstream failed for {Wi}", wi.Id);
        }
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
}
